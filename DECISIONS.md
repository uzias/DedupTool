# Decisions

## Confirmed incident root cause

- `Deduper.Run` previously used one batch-wide `HashSet` of raw SHA-256 values and immediately omitted every later matching document. This suppressed `DOC-000005`, `DOC-000010`, and `DOC-000011` from `batch-sample.json` even though their respective parent emails were emitted. It also suppressed `DOC-000013` solely because a different custodian had the same loose-file bytes.
- The old code only read one parent when assigning `FamilyID` and `SentDate`, so it did not support a nested attachment chain.

## Implemented design and invariants

- The tool indexes documents by `docId`, validates supported types and parent references, rejects attachment-to-loose relationships, and detects parent cycles while resolving each family root.
- Families are resolved before any suppression. Every emitted attachment retains its original `ParentID`, receives its original top-level `FamilyID`, and inherits the top-level email's UTC timestamp.
- Attachment hashes are still recognized as equal content, but an attachment is never suppressed independently: an equal attachment under a different parent is not a replacement for that parent-child relationship.
- Logical email identity is deterministic: normalized sender and recipient address casing/outer whitespace, recipient ordering, UTC instant, subject, and body are compared. CRLF/CR are normalized to LF. Subject/body casing and content are otherwise preserved; this intentionally favors false negatives over accidental merging of distinct evidence.
- Canonical selection is lexical `docId`, and rows are emitted in lexical `docId` order. Results do not depend on input order.
- CSV writing now escapes commas, quotation marks, and line breaks, while preserving the required header and LF output lines.

## Deduplication policy and specification conflict

- Sections 3.2–3.4 and 3.6 request global detection and suppression; section 3.5 requires complete email families; section 4 requires every custodian set to remain independently complete. A single CSV without an association/canonical-delivery format cannot both globally omit a cross-custodian copy and leave both custodian productions self-contained.
- I adopted the conservative policy requested by the exercise: detect duplicate logical emails and content globally, but retain records across custodians. For example, `DOC-000001` and `DOC-000009` are detected as equivalent logical emails, yet both families are emitted. Likewise `DOC-000008` and `DOC-000013` remain, one per custodian.
- Within one custodian, duplicate loose files are suppressed using the lexical canonical `docId`. Duplicate top-level email families are suppressed only when their logical email identity and complete descendant content/name structure match. This avoids suppressing a family whose extra or differently-associated evidence is not represented by the retained family.
- This deliberately does not meet the strictest reading of global suppression. It does meet the non-loss, family-integrity, and self-contained-custodian requirements. The unresolved policy needs product/legal direction before changing the production contract.
- The in-memory `DedupDecision` records canonical ID, action, and reason for each input document. It helps audit the decision, but it is not a substitute for a separate per-custodian delivery view or an emitted relationship mapping.

## Determinism and scale

- Index construction and duplicate grouping are linear apart from deterministic child and result ordering. Family resolution visits each relationship once; the current descendant-signature construction is recursive and should be made iterative for unusually deep nesting.
- No database, output columns, CLI modes, or physical-content store were added. The CLI remains `DedupTool <batch.json> <loadfile.csv>`.

## Tests run

- `dotnet test DedupTool.slnx` — passed: 9; failed: 0; skipped: 0.
- `dotnet run --project src\\DedupTool\\DedupTool.csproj -- data\\batch-sample.json data\\dedup-output.csv` — succeeded and printed `batch-sample: 16 documents in, 16 out, 0 suppressed as duplicates.`
- Tests cover the sample attachment regression, equal attachment content under different parents, cross-custodian logical email recognition, forward/reply distinction, loose-file policy, nested families, invalid parents/cycles, order determinism, suppression-count reconciliation, and CSV quoting/header behavior.

## Unfinished work, assumptions, and next steps

- Assumption: duplicate email copies can be safely suppressed within the same custodian only if their complete descendant structure and attachment metadata match. This is conservative and should be confirmed with ingest/legal stakeholders.
- The prompt does not define a delivery mechanism for canonical-content reuse or a global-review versus custodian-review view. Next, I would get that contract clarified, retain immutable source/relationship records, and then implement the explicitly approved output/view model.
- Before accepting two-million-document batches, I would add a realistic memory/performance benchmark, replace recursive family-signature construction with an iterative post-order algorithm, and add property tests/fuzzing around malformed graphs and CSV fields.

## Working with AI

- AI assistance was used to inspect the starter behavior, identify the hash-only suppression failure, implement and test the changes, and draft these decisions. The output was checked against the supplied JSON, the CSV contract, compilation, and the test run rather than accepted unverified.
- Automatic export of this Codex conversation is not available from the repository tooling. To satisfy the requested transcript deliverable, export or copy this chat from the Codex/IDE conversation UI into a text or Markdown file (including prompts, tool results, and final response) before submission; no transcript has been fabricated in this repository.
