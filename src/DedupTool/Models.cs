namespace DedupTool;

public class Batch
{
    public string BatchId { get; set; } = "";
    public List<Document> Documents { get; set; } = new();
}

public class Document
{
    public string DocId { get; set; } = "";
    public string Custodian { get; set; } = "";
    public string DocType { get; set; } = "";      // email | attachment | loose
    public string? ParentId { get; set; }
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }

    // Email-only fields. Null for attachments and loose files.
    public string? SentDate { get; set; }
    public string? From { get; set; }
    public List<string>? To { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
}

public class LoadFileRow
{
    public string DocId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string Custodian { get; set; } = "";
    public string DocType { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SentDate { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed record DedupDecision(
    string DocId,
    string Action,
    string CanonicalId,
    string Reason);

public record DedupResult(
    List<LoadFileRow> Rows,
    int Suppressed,
    IReadOnlyList<DedupDecision> Decisions);
