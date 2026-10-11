# Codex Master Prompt — eDiscovery Batch Deduplication (.NET 8)

## Role and mandate
Act as a Principal Software Engineer experienced in C#/.NET 8, eDiscovery, evidence preservation, deterministic batch processing, and test-driven debugging. Work directly in the supplied repository. Analyze first, then make the smallest defensible implementation, verify it, and document all decisions.

**Goal:** Address `INGEST-4471` (emails missing attachments), implement cross-mailbox email deduplication, respect the specified CSV and CLI contracts, add tests, and complete `DECISIONS.md` within the spirit of a **60-minute engineering exercise**. Leave a maintainable repository even if not all requirements can be satisfied.

**Non-negotiable:** Do not silently discard evidence or parent-child context. Treat duplicate *detection*, duplicate *suppression*, and physical content reuse as separate concerns.

## 1. Analyze before changing code
Read `README.md`, `SPEC.md`, `data/batch-sample.json`, `data/current-tool-output.csv`, `src/`, all tests, and any existing `DECISIONS.md`.

1. Map input parsing, data structures, duplicate selection, family handling, output generation and CLI behavior.
2. Reproduce the reported bug with the provided batch; compare current output with the input relationships.
3. Identify confirmed root cause(s) with file/function references. Do **not** assume hash-based suppression is the culprit until verified.
4. Map implementation behavior to each requirement in `SPEC.md`, notably §§3.2–3.6, 4, 5, and 6.
5. Briefly report confirmed observations, ambiguity, and the intended minimal change set **before modifying files**.

## 2. Important source scenarios
Verify these sample cases using the actual JSON:

- `DOC-000001` (`jsmith`) and `DOC-000009` (`mlee`) are logically equivalent email copies despite different raw SHA-256, sender casing, line endings, and timezone representation.
- `DOC-000002` and `DOC-000010` are equal-content Excel attachments in different families.
- `DOC-000003`, `DOC-000005`, and `DOC-000011` are equal-content PDFs under different email parents. Losing one association loses contextual evidence.
- `DOC-000008` and `DOC-000013` are same-hash loose files belonging to different custodians.
- `DOC-000004` is a forward, **not** a duplicate of the original `DOC-000001` just because it cites similar content.

Do not hardcode any sample IDs or outputs into the production algorithm.

## 3. Architecture: logical indices, not new infrastructure
Use simple in-memory structures where useful, reusing existing code when possible:

- **DocumentIndex:** `docId -> source document`.
- **FamilyGraph:** parent/child adjacency, top-level family roots, nested attachment support.
- **ContentIndex:** hashes identify identical raw content but **do not** by themselves determine whether an association may be omitted.
- **EmailIdentity:** normalized deterministic comparison of logical email fields.
- **DuplicateGroups / Decisions:** distinguish `KEEP` from `SUPPRESS`, and record canonical ID and a reason internally.
- **RelationshipIndex:** conceptual association of original child, original parent, family, custodian, and content hash. It may simply be dictionaries/lists, not a database table.

Do not add an ORM, SQL database, microservice, file-storage subsystem, new output schema, or new CLI mode to solve this exercise. The concept of storing physical bytes once is a future architectural option, **not** an additional deliverable.

### Core invariants
- Every emitted attachment has an emitted parent, recursively up to an emitted top-level family root.
- `FamilyID` points to the correct original top-level root.
- Any emitted email retains all of its required descendants; an identical attachment under another parent is **not** a substitute.
- The same input, in any document order, produces the same output.
- Custodian provenance remains intact; do not silently move a record to a different custodian.
- Never conflate an email's different raw hashes with proof of different logical messages.

## 4. Resolve and document the specification conflict honestly
Interpret explicitly:

- §3.2: attachments with identical SHA-256 are duplicates.
- §3.3: cross-mailbox copies of a logical email are duplicates despite raw hash differences.
- §3.4: deduplication is batch-wide.
- §3.5: emitted families must remain complete.
- §3.6: suppressed duplicates are omitted.
- §4: each custodian's production must be complete and self-contained.

A single strict global CSV with suppressed cross-custodian records does **not automatically** provide a self-contained per-custodian production. An internal mapping alone does not fulfill §4 if it is neither emitted nor part of the specified delivery contract.

**Default conservative policy when clarification is unavailable:** prioritize complete emitted families and independently usable custodian sets; identify duplicates globally but only suppress records where doing so preserves those invariants. This may deliberately emit duplicate content across custodians and thus fail a strict interpretation of §§3.3–3.4/3.6. State this trade-off prominently; do not claim that every clause is simultaneously satisfied.

If the existing tests/fixtures or an interviewer clarification provide an authoritative expected interpretation, reconcile carefully and record the basis. Do not modify tests merely to hide a real spec conflict. Treat `DECISIONS.md` as essential, not a postscript.

A longer-term design could keep immutable originals plus canonical content and relationship indexes, expose separate global-review and per-custodian production views, but **do not silently implement extra CSV columns or another output format**.

## 5. Implementation plan
1. **Parse and validate**: unique `docId`, known `docType`, correct `parentId` references; detect parent cycles; avoid excessive repeated scans.
2. **Build families before suppression**: identify roots and descendants, including nested attachments. Determine `FamilyID` and inherited email `SentDate`.
3. **Recognize cross-mailbox emails**: compare normalized sender, recipients, timestamp in UTC, subject and body. Normalize casing where defensible, trim only inconsequential address whitespace, normalize line endings; avoid fuzzy matching and stripping meaningful body content or collapsing `Re:`/`FW:`.
4. **Group duplicate contents** by SHA-256 for attachments and loose files; distinguish equal bytes from equal contextual records.
5. **Canonical selection**: deterministic tie-breaking (e.g. stable `docId` order), not first-seen order. Consider family members and custody before suppressing anything.
6. **Apply suppression policy** at a boundary that preserves families and custodian production. Do not leave dangling `ParentID` or incorrect `FamilyID`. Avoid silently reparenting an original child.
7. **Validate the final set** and only then serialize the CSV.

Keep complexity linear or close to linear for batches near two million records; avoid quadratic comparisons, repetitive traversal, excessive intermediate copies, and unnecessary parallelization.

## 6. Exact output contract
Keep the CLI interface:

```bash
cd src/DedupTool
dotnet run -- ../../data/batch-sample.json out.csv
cd ../..
dotnet test
```

Output CSV headers **exactly** in this order:

```text
DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash
```

- `DocID` = original `docId`.
- `FamilyID` = original root `docId`; loose files use their own ID.
- `ParentID` = original `parentId`, or empty.
- `Custodian`, `DocType`, `FileName`, `Hash` = original corresponding fields.
- `SentDate` = UTC `yyyy-MM-ddTHH:mm:ssZ`, no fractions; attachments inherit their top-level email timestamp; loose files empty.
- Escape CSV quotation marks, commas, and line breaks correctly.
- Deterministic row order is advisable although order is not semantically significant.
- On successful run, print exactly one summary line describing documents in, out, and suppressed, with coherent counts.

## 7. Tests: minimum useful suite
Write focused tests for:

1. Original missing-attachment regression with `batch-sample.json`.
2. Equal attachment hashes under distinct parents: all necessary associations remain.
3. Logical email equivalence across different sender case, LF/CRLF and equivalent UTC instants.
4. Similar-but-distinct emails, including forwards and replies, are not mistakenly merged.
5. Duplicated loose files across custodians and within a custodian, per documented suppression policy.
6. Nested attachments and root/parent resolution.
7. Missing parent, unknown parent type, and parent cycles.
8. Determinism under reordered input, including stable canonical selection and CSV bytes where practical.
9. Exact CSV column order, quoting, UTC timestamps, and empty fields.
10. Completeness of each emitted custodian set and suppression summary reconciliation.

Do not claim test success without actually running them. Report the commands and observed results. If a test cannot be run, say why.

## 8. DECISIONS.md is a first-class deliverable
Create or update it with short, defensible sections:

- Confirmed incident root cause and code location.
- Implemented design and invariants.
- Email identity normalization rationale and false-positive/false-negative trade-offs.
- Precise precedence adopted for §§3.2–3.6 vs §4, with a concrete example from the sample showing the unresolved tension.
- Why a relationship index helps preserve context but is **not** by itself a substitute for per-custodian outputs.
- Determinism and complexity considerations.
- Tests run and outcomes.
- Unfinished work, assumptions, questions for the interviewer, and next steps.

Do not describe assumptions as facts. Do not invent an interviewer answer.

## 9. AI transcript requirement
`README.md` requests transcript(s) of AI conversations used during the exercise. Preserve or export this Codex session using the tooling actually available. If automatic transcript export is unavailable, state the limitation and include a reproducible manual export instruction; do not fabricate a transcript.

## 10. Execution and completion
Proceed with changes after the brief analysis. Do not stop after suggesting a design, and do not ask for confirmation for ordinary engineering decisions. Prefer a minimal complete vertical slice over half-finished architectural scaffolding.

At the end, report:

1. Root cause and exact fix.
2. Files changed.
3. Chosen deduplication and custodian policy.
4. Tests executed and pass/fail counts.
5. Remaining unresolved specification conflicts and risks.
6. What you would implement next if more time were available.

**Definition of done:** runnable .NET 8 CLI with unchanged contract; correct deterministic CSV; no incomplete emitted email families; tested cross-mailbox matching; explicit decisions and limitations; no fabricated outcomes.
