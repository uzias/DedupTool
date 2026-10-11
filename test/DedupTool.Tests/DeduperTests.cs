using System.Text.Json;
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    [Fact]
    public void SampleBatch_PreservesEveryEmailFamilyAndCustodianSet()
    {
        var batch = LoadSampleBatch();

        var result = Deduper.Run(batch);

        Assert.Equal(16, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.Contains(result.Decisions, decision => decision.DocId == "DOC-000009"
                                                    && decision.Action == "KEEP"
                                                    && decision.Reason.Contains("across custodians"));
        AssertFamilyIntegrity(result.Rows);
        Assert.Equal("DOC-000009", Row(result, "DOC-000011").FamilyId);
        Assert.Equal("2024-03-05T19:22:00Z", Row(result, "DOC-000010").SentDate);
    }

    [Fact]
    public void EqualAttachmentHashes_UnderDistinctParents_AreBothEmitted()
    {
        var batch = new Batch { Documents = new()
        {
            Email("E1", "c1", "2024-01-01T12:00:00Z"),
            Attachment("A1", "c1", "E1", "same"),
            Email("E2", "c1", "2024-01-02T12:00:00Z", subject: "other"),
            Attachment("A2", "c1", "E2", "same"),
        }};

        var result = Deduper.Run(batch);

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal("E1", Row(result, "A1").ParentId);
        Assert.Equal("E2", Row(result, "A2").ParentId);
    }

    [Fact]
    public void EquivalentEmailsAcrossCustodians_AreDetectedButRetained()
    {
        var batch = new Batch { Documents = new()
        {
            Email("E2", "mlee", "2024-01-01T12:00:00Z", from: "JOHN@example.com", body: "one\r\ntwo"),
            Attachment("A2", "mlee", "E2", "same"),
            Email("E1", "jsmith", "2024-01-01T07:00:00-05:00", from: "john@example.com", body: "one\ntwo"),
            Attachment("A1", "jsmith", "E1", "same"),
        }};

        var result = Deduper.Run(batch);

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.All(result.Decisions.Where(decision => decision.DocId is "E1" or "E2"),
            decision => Assert.Contains("Logical email duplicate", decision.Reason));
    }

    [Fact]
    public void ForwardAndReply_AreNotMergedWithOriginal()
    {
        var result = Deduper.Run(LoadSampleBatch());

        Assert.Contains(result.Rows, row => row.DocId == "DOC-000001");
        Assert.Contains(result.Rows, row => row.DocId == "DOC-000004");
        Assert.Contains(result.Rows, row => row.DocId == "DOC-000012");
    }

    [Fact]
    public void LooseFiles_AreSuppressedOnlyWithinTheSameCustodian()
    {
        var batch = new Batch { Documents = new()
        {
            Loose("B", "c1", "same"),
            Loose("C", "c2", "same"),
            Loose("A", "c1", "same"),
        }};

        var result = Deduper.Run(batch);

        Assert.Equal(new[] { "A", "C" }, result.Rows.Select(row => row.DocId));
        Assert.Equal(1, result.Suppressed);
        Assert.Contains(result.Decisions, decision => decision.DocId == "B" && decision.CanonicalId == "A");
    }

    [Fact]
    public void NestedAttachments_UseTopLevelFamilyAndSentDate()
    {
        var batch = new Batch { Documents = new()
        {
            Email("E", "c1", "2024-01-01T07:00:01-05:00"),
            Attachment("A", "c1", "E", "hash-a"),
            Attachment("B", "c1", "A", "hash-b"),
        }};

        var result = Deduper.Run(batch);

        var nested = Row(result, "B");
        Assert.Equal("E", nested.FamilyId);
        Assert.Equal("A", nested.ParentId);
        Assert.Equal("2024-01-01T12:00:01Z", nested.SentDate);
    }

    [Fact]
    public void InvalidRelationships_AreRejected()
    {
        var missingParent = new Batch { Documents = new() { Attachment("A", "c", "missing", "h") } };
        var looseParent = new Batch { Documents = new() { Loose("L", "c", "h"), Attachment("A", "c", "L", "h2") } };
        var cycle = new Batch { Documents = new()
        {
            Attachment("A", "c", "B", "h1"), Attachment("B", "c", "A", "h2"),
        }};

        Assert.Throws<InvalidOperationException>(() => Deduper.Run(missingParent));
        Assert.Throws<InvalidOperationException>(() => Deduper.Run(looseParent));
        Assert.Throws<InvalidOperationException>(() => Deduper.Run(cycle));
    }

    [Fact]
    public void ReorderedInput_ProducesIdenticalCsvAndCanonicalSelection()
    {
        var batch = new Batch { Documents = new()
        {
            Loose("Z", "c", "h"), Loose("A", "c", "h"),
            Email("E2", "c", "2024-01-01T12:00:00Z"), Attachment("A2", "c", "E2", "child", "child.bin"),
            Email("E1", "c", "2024-01-01T12:00:00Z"), Attachment("A1", "c", "E1", "child", "child.bin"),
        }};
        var reordered = new Batch { Documents = batch.Documents.AsEnumerable().Reverse().ToList() };

        var first = Deduper.Run(batch);
        var second = Deduper.Run(reordered);

        Assert.Equal(LoadFileWriter.ToCsv(first.Rows), LoadFileWriter.ToCsv(second.Rows));
        Assert.Equal(new[] { "A", "E1", "A1" }.OrderBy(id => id, StringComparer.Ordinal),
            first.Rows.Select(row => row.DocId));
        Assert.Equal(batch.Documents.Count, first.Rows.Count + first.Suppressed);
    }

    [Fact]
    public void LoadFile_UsesExactHeaderAndEscapesSpecialCharacters()
    {
        var csv = LoadFileWriter.ToCsv(new[] { new LoadFileRow
        {
            DocId = "A", FamilyId = "A", ParentId = "", Custodian = "c,1", DocType = "loose",
            FileName = "a\"b\n.txt", SentDate = "", Hash = "h",
        }});

        Assert.Equal("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash\n" +
                     "A,A,,\"c,1\",loose,\"a\"\"b\n.txt\",,h\n", csv);
    }

    private static Batch LoadSampleBatch()
    {
        var path = FindRepositoryFile("data", "batch-sample.json");
        return JsonSerializer.Deserialize<Batch>(File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Sample batch was empty.");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate repository fixture.");
    }

    private static Document Email(string id, string custodian, string sentDate, string from = "john@example.com",
        string subject = "subject", string body = "body") => new()
    {
        DocId = id, Custodian = custodian, DocType = "email", FileName = id + ".eml", Sha256 = id + "-hash", SizeBytes = 10,
        SentDate = sentDate, From = from, To = new() { "maria@example.com" }, Subject = subject, Body = body,
    };

    private static Document Attachment(string id, string custodian, string parentId, string hash, string? fileName = null) => new()
    {
        DocId = id, Custodian = custodian, DocType = "attachment", ParentId = parentId,
        FileName = fileName ?? id + ".bin", Sha256 = hash, SizeBytes = 10,
    };

    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash, SizeBytes = 10,
    };

    private static LoadFileRow Row(DedupResult result, string docId) =>
        Assert.Single(result.Rows, row => row.DocId == docId);

    private static void AssertFamilyIntegrity(IEnumerable<LoadFileRow> rows)
    {
        var byId = rows.ToDictionary(row => row.DocId, StringComparer.Ordinal);
        foreach (var row in byId.Values.Where(row => !string.IsNullOrEmpty(row.ParentId)))
        {
            Assert.True(byId.ContainsKey(row.ParentId), $"Missing emitted parent for {row.DocId}.");
            Assert.Equal(byId[row.ParentId].FamilyId, row.FamilyId);
        }
    }
}
