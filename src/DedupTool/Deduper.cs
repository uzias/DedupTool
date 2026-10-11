using System.Globalization;

namespace DedupTool;

/// <summary>
/// Builds a deterministic, relationship-safe production view of a batch.
/// Equal content is detected globally, but records are only suppressed where
/// doing so preserves the complete family and its custodian provenance.
/// </summary>
public static class Deduper
{
    private const string Keep = "KEEP";
    private const string Suppress = "SUPPRESS";

    public static DedupResult Run(Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var documents = batch.Documents ?? throw new InvalidOperationException("Batch documents cannot be null.");
        var byId = BuildDocumentIndex(documents);
        ValidateDocuments(byId);

        var childrenByParent = BuildChildrenIndex(byId);
        var familyByDocument = BuildFamilyIndex(byId);
        var familySignatures = BuildFamilySignatures(byId, childrenByParent);
        var plan = BuildSuppressionPlan(byId, familySignatures);

        var decisions = new List<DedupDecision>(byId.Count);
        var rows = new List<LoadFileRow>(byId.Count);
        foreach (var document in byId.Values.OrderBy(document => document.DocId, StringComparer.Ordinal))
        {
            var family = familyByDocument[document.DocId];
            if (plan.SuppressedRoots.TryGetValue(family.RootId, out var canonicalRoot))
            {
                decisions.Add(new DedupDecision(document.DocId, Suppress, canonicalRoot,
                    "Duplicate email family within the same custodian."));
                continue;
            }

            if (plan.SuppressedLoose.TryGetValue(document.DocId, out var canonicalLoose))
            {
                decisions.Add(new DedupDecision(document.DocId, Suppress, canonicalLoose,
                    "Duplicate loose-file content within the same custodian."));
                continue;
            }

            var reason = document.DocType switch
            {
                "attachment" => "Preserved because the emitted parent family requires this association.",
                "email" when plan.CrossCustodianEmailIds.Contains(document.DocId) =>
                    "Logical email duplicate detected across custodians; retained for custodian completeness.",
                "loose" when plan.CrossCustodianLooseIds.Contains(document.DocId) =>
                    "Duplicate loose-file content detected across custodians; retained for custodian completeness.",
                _ => "No suppressible duplicate detected."
            };
            decisions.Add(new DedupDecision(document.DocId, Keep, document.DocId, reason));
            rows.Add(ToRow(document, family));
        }

        return new DedupResult(rows, decisions.Count(decision => decision.Action == Suppress), decisions);
    }

    private static Dictionary<string, Document> BuildDocumentIndex(IEnumerable<Document> documents)
    {
        var byId = new Dictionary<string, Document>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (document is null || string.IsNullOrWhiteSpace(document.DocId))
            {
                throw new InvalidOperationException("Every document must have a non-empty docId.");
            }

            if (!byId.TryAdd(document.DocId, document))
            {
                throw new InvalidOperationException($"Duplicate docId '{document.DocId}'.");
            }
        }

        return byId;
    }

    private static void ValidateDocuments(IReadOnlyDictionary<string, Document> byId)
    {
        foreach (var document in byId.Values)
        {
            if (document.DocType is not ("email" or "attachment" or "loose"))
            {
                throw new InvalidOperationException($"Document '{document.DocId}' has unsupported docType '{document.DocType}'.");
            }

            if (string.IsNullOrWhiteSpace(document.Sha256))
            {
                throw new InvalidOperationException($"Document '{document.DocId}' must have a SHA-256 value.");
            }

            if (document.DocType == "attachment")
            {
                if (string.IsNullOrWhiteSpace(document.ParentId) || !byId.TryGetValue(document.ParentId, out var parent))
                {
                    throw new InvalidOperationException($"Attachment '{document.DocId}' references a missing parent.");
                }

                if (parent.DocType == "loose")
                {
                    throw new InvalidOperationException($"Attachment '{document.DocId}' cannot have loose file '{parent.DocId}' as parent.");
                }
            }
            else if (document.ParentId is not null)
            {
                throw new InvalidOperationException($"Only attachments may have a parent ('{document.DocId}').");
            }

            if (document.DocType == "email" && !TryGetUtcSentDate(document, out _))
            {
                throw new InvalidOperationException($"Email '{document.DocId}' must have a valid sentDate with an offset.");
            }
        }
    }

    private static Dictionary<string, List<Document>> BuildChildrenIndex(IReadOnlyDictionary<string, Document> byId)
    {
        var childrenByParent = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
        foreach (var document in byId.Values.Where(document => document.ParentId is not null))
        {
            if (!childrenByParent.TryGetValue(document.ParentId!, out var children))
            {
                children = new List<Document>();
                childrenByParent.Add(document.ParentId!, children);
            }

            children.Add(document);
        }

        foreach (var children in childrenByParent.Values)
        {
            children.Sort((left, right) => StringComparer.Ordinal.Compare(left.DocId, right.DocId));
        }

        return childrenByParent;
    }

    private static Dictionary<string, FamilyInfo> BuildFamilyIndex(IReadOnlyDictionary<string, Document> byId)
    {
        var families = new Dictionary<string, FamilyInfo>(StringComparer.Ordinal);
        foreach (var start in byId.Values)
        {
            if (families.ContainsKey(start.DocId))
            {
                continue;
            }

            var path = new List<Document>();
            var pathIds = new HashSet<string>(StringComparer.Ordinal);
            var current = start;
            FamilyInfo root;
            while (true)
            {
                if (families.TryGetValue(current.DocId, out root!))
                {
                    break;
                }

                if (!pathIds.Add(current.DocId))
                {
                    throw new InvalidOperationException($"Parent cycle detected at document '{current.DocId}'.");
                }

                path.Add(current);
                if (current.ParentId is null)
                {
                    root = CreateRootFamily(current);
                    break;
                }

                current = byId[current.ParentId];
            }

            foreach (var document in path)
            {
                families[document.DocId] = root;
            }
        }

        return families;
    }

    private static FamilyInfo CreateRootFamily(Document root) => root.DocType == "email"
        ? new FamilyInfo(root.DocId, FormatUtcSentDate(root))
        : new FamilyInfo(root.DocId, "");

    private static Dictionary<string, string> BuildFamilySignatures(
        IReadOnlyDictionary<string, Document> byId,
        IReadOnlyDictionary<string, List<Document>> childrenByParent)
    {
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in byId.Values.Where(document => document.ParentId is null && document.DocType == "email"))
        {
            signatures[root.DocId] = BuildDescendantSignature(root.DocId, byId, childrenByParent);
        }

        return signatures;
    }

    private static string BuildDescendantSignature(
        string documentId,
        IReadOnlyDictionary<string, Document> byId,
        IReadOnlyDictionary<string, List<Document>> childrenByParent)
    {
        var document = byId[documentId];
        var childSignatures = childrenByParent.TryGetValue(documentId, out var children)
            ? children.Select(child => BuildDescendantSignature(child.DocId, byId, childrenByParent)).OrderBy(value => value, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        // The root email's raw hash is intentionally excluded: mailbox copies differ in raw bytes.
        var local = document.DocType == "email" && document.ParentId is null
            ? "root-email"
            : LengthEncode(document.DocType, NormalizeHash(document.Sha256), document.FileName);
        return LengthEncode(local, string.Concat(childSignatures));
    }

    private static SuppressionPlan BuildSuppressionPlan(
        IReadOnlyDictionary<string, Document> byId,
        IReadOnlyDictionary<string, string> familySignatures)
    {
        var suppressedRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        var crossCustodianEmailIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emailGroup in byId.Values
                     .Where(document => document.DocType == "email" && document.ParentId is null)
                     .GroupBy(CreateEmailIdentity)
                     .Where(group => group.Count() > 1))
        {
            var emails = emailGroup.ToList();
            if (emails.Select(email => email.Custodian).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                crossCustodianEmailIds.UnionWith(emails.Select(email => email.DocId));
            }

            foreach (var custodianGroup in emails.GroupBy(email => email.Custodian, StringComparer.Ordinal))
            {
                foreach (var signatureGroup in custodianGroup.GroupBy(email => familySignatures[email.DocId], StringComparer.Ordinal))
                {
                    var ordered = signatureGroup.OrderBy(email => email.DocId, StringComparer.Ordinal).ToList();
                    foreach (var duplicate in ordered.Skip(1))
                    {
                        suppressedRoots.Add(duplicate.DocId, ordered[0].DocId);
                    }
                }
            }
        }

        var suppressedLoose = new Dictionary<string, string>(StringComparer.Ordinal);
        var crossCustodianLooseIds = new HashSet<string>(StringComparer.Ordinal);
        var looseFiles = byId.Values.Where(document => document.DocType == "loose").ToList();
        foreach (var hashGroup in looseFiles.GroupBy(document => NormalizeHash(document.Sha256), StringComparer.Ordinal))
        {
            var files = hashGroup.ToList();
            if (files.Select(file => file.Custodian).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                crossCustodianLooseIds.UnionWith(files.Select(file => file.DocId));
            }

            foreach (var custodianGroup in files.GroupBy(file => file.Custodian, StringComparer.Ordinal))
            {
                var ordered = custodianGroup.OrderBy(file => file.DocId, StringComparer.Ordinal).ToList();
                foreach (var duplicate in ordered.Skip(1))
                {
                    suppressedLoose.Add(duplicate.DocId, ordered[0].DocId);
                }
            }
        }

        return new SuppressionPlan(suppressedRoots, suppressedLoose, crossCustodianEmailIds, crossCustodianLooseIds);
    }

    private static LoadFileRow ToRow(Document document, FamilyInfo family) => new()
    {
        DocId = document.DocId,
        FamilyId = family.RootId,
        ParentId = document.ParentId ?? "",
        Custodian = document.Custodian,
        DocType = document.DocType,
        FileName = document.FileName,
        SentDate = family.SentDate,
        Hash = document.Sha256,
    };

    private static string CreateEmailIdentity(Document document)
    {
        if (!TryGetUtcSentDate(document, out var sentDate))
        {
            throw new InvalidOperationException($"Email '{document.DocId}' must have a valid sentDate with an offset.");
        }

        var recipients = (document.To ?? new List<string>())
            .Select(NormalizeAddress)
            .OrderBy(address => address, StringComparer.Ordinal);
        return LengthEncode(
            NormalizeAddress(document.From ?? ""),
            string.Concat(recipients.Select(address => LengthEncode(address))),
            sentDate.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            NormalizeLineEndings(document.Subject ?? ""),
            NormalizeLineEndings(document.Body ?? ""));
    }

    private static bool TryGetUtcSentDate(Document document, out DateTimeOffset sentDate)
    {
        sentDate = default;
        var value = document.SentDate;
        var timeStart = value?.IndexOf('T') ?? -1;
        var hasExplicitOffset = timeStart >= 0 &&
                                (value!.EndsWith('Z') || value.EndsWith('z') ||
                                 value[(timeStart + 1)..].Contains('+') || value[(timeStart + 1)..].Contains('-'));
        return hasExplicitOffset && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out sentDate);
    }

    private static string FormatUtcSentDate(Document document)
    {
        TryGetUtcSentDate(document, out var sentDate);
        return sentDate.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string NormalizeAddress(string value) => value.Trim().ToUpperInvariant();
    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n").Replace("\r", "\n");
    private static string NormalizeHash(string value) => value.ToUpperInvariant();
    private static string LengthEncode(params string[] values) => string.Concat(values.Select(value => $"{value.Length}:{value}"));

    private sealed record FamilyInfo(string RootId, string SentDate);
    private sealed record SuppressionPlan(
        IReadOnlyDictionary<string, string> SuppressedRoots,
        IReadOnlyDictionary<string, string> SuppressedLoose,
        IReadOnlySet<string> CrossCustodianEmailIds,
        IReadOnlySet<string> CrossCustodianLooseIds);
}
