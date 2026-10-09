---
id: DF-ARCA-2026-0012
title: Immutable records are erased by an explicit, capability-gated operation that replaces the blob in the current tree with a hash-only tombstone at the same path; Git history is out of Arca's reach and is documented as such
status: accepted
version: 1.0.0
created: 2026-10-09
updated: 2026-10-09
owners:
  - arca
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/ARCA-STORAGE-REQUIREMENTS.md
  - docs/consuming-arca.md
  - research/decisions/DF-ARCA-2026-0011--namespace-scoped-change-tokens.md
tags: [integrity, erasure, retention, immutable, tombstone, audit]
provenance:
  contributions:
    EXE-20261009T005625812Z-9cc68a91:
      operations: [created]
      at: 2026-10-09T01:29:34.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Explicit erasure of immutable records (WI-0028, ARCA-INT-005)"
---

# DF-ARCA-2026-0012 — Explicit erasure of immutable records

- **Date:** 2026-10-09
- **Status:** accepted
- **Decision type:** architecture and API (WI-0028, ARCA-INT-005)

## Context

ARCA-INT-003 makes an immutable record unchangeable and undeletable by any
ordinary write. Signal's retention rules need some immutable content, such as
survey responses, to stop being stored after a period. The coordinator asked
for an erasure that meets five conditions:

- it is separate from delete and gated by an explicit capability;
- it is audited in the same commit by hash only;
- later reads report it as erased, not as corrupt;
- nothing can recreate the record;
- the limits of the guarantee are stated honestly.

## Decision

1. **A separate operation, gated twice.** `Erasure.request` takes the record
   as the application last validated it. The record must be immutable and
   stored at an authoritative path, and the reason must be one line.
   `Erasure.operation` builds an `Operation` that is marked as an erasure.
   `Erasure.commit` refuses the operation before sending when the provider
   does not declare `Capability.Erase`, and each provider refuses it again on
   its own side. `Change.Delete` keeps refusing immutable records.
2. **The tombstone replaces the blob at the same path, in the same commit.**
   - The tombstone holds the erased content hash (`ValidatedRecord.ContentHash`),
     the erased revision, the time and the reason. It never holds the content.
   - The change is conditioned on the erased revision. The provider also
     checks that the stored record is immutable and has exactly that hash, so
     the erasure removes only the content it names.
   - Who erased it is in the commit trailers, as for every Arca commit.
   - We rejected a separate audit file beside a deleted path. Because the
     tombstone sits at the record's own path, every read of that path finds
     it in one request and can report `Erased`. A `Create` of that path then
     conflicts, so the record cannot be recreated without another lookup.
3. **Reads and integrity.**
   - `ReadOutcome.ofStored` turns a tombstone into `ReadOutcome.Erased`, so a
     read never reports it as an integrity failure.
   - The write guard refuses to update, delete or erase again any path that
     holds a tombstone (`IntegrityRefusal.ErasedRecord`).
   - Someone outside Arca could still write a tombstone-shaped file. Like any
     external edit, it is visible in `History` as `External` (ARCA-INT-002).
4. **Derived state.**
   - `Snapshot.Erased` lists tombstones separately from `Objects`, so derived
     indexes never read them.
   - `Export` keeps the tombstones, and `Migration` copies them. A migrated
     namespace therefore keeps the record erased.
   - The offline queue refuses to hold an erasure, because erasing is a
     deliberate act against current state.
   - `ReadCache.purgeErased` removes any cached partition that holds an
     erased path from a device.
5. **The limit, stated.** Erasure removes the content from the current tree
   only. Git history, clones and forks keep it. Permanent erasure needs a
   history rewrite by the repository owner. Arca never rewrites history. The
   consumer guide says so wherever erasure is described.

## Consequences

- New API in 0.4.0: `ReadOutcome.Erased`, `Snapshot.Erased`,
  `Capability.Erase`, and two new `IntegrityRefusal` cases.
- Evidence: three storage conformance cases (in-memory and the GitHub adapter
  over FakeGitHub), one read-cache case (in-memory and the Limen FakeStore),
  negative tests showing a provider that hides tombstones or lets them be
  overwritten fails the suite, and unit tests for requests, format, the
  capability gate, the queue, snapshot, export, index and migration.
