---
id: DF-ARCA-2026-0014
title: Only the provider confirms a write; synchronized entry states, commit receipts and queue entries are unforgeable outside Arca, in a breaking 0.6.0 minor release
status: accepted
version: 1.0.0
created: 2026-10-28
updated: 2026-10-28
owners:
  - arca
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - CHANGELOG.md
  - Directory.Build.props
  - docs/consuming-arca.md
  - docs/requirements/ARCA-STORAGE-REQUIREMENTS.md
  - src/Arca.Core/OfflineQueue.fs
  - src/Arca.Core/Storage.fs
  - src/Arca.Core/Arca.Core.fsproj
  - tests/Arca.CompileFailure/Arca.CompileFailure.fsproj
  - tests/Arca.Tests/PersistenceSafetyTests.fs
  - research/decisions/DF-ARCA-2026-0005--offline-queue-persists-to-localstorage-behind-a-port.md
  - research/decisions/DF-ARCA-2026-0013--release-0-5-0-is-a-minor-release.md
tags: [offline, queue, integrity, api, release, versioning]
provenance:
  contributions:
    EXE-20261028T005603298Z-4cba0be1:
      operations: [created]
      at: 2026-10-28T01:12:34.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Only the provider confirms a write; breaking 0.6.0 (WI-0036, ARCA-OFF-009; owner approval 2026-10-28, EII Workstream D)"
---

# DF-ARCA-2026-0014 — Only the provider confirms a write

- **Date:** 2026-10-28
- **Status:** accepted
- **Decision type:** public API and release versioning (WI-0036, ARCA-OFF-009)
- **Approval:** owner approval 2026-10-28, Echelon Engineering Improvement
  Initiative, Workstream D ("Persistence safety by construction").

## Context

Arca's offline queue (ARCA-OFF-001..007) records whether each queued write
reached the provider. In 0.5.0 that record was forgeable by any caller:

- `EntryState` was a public union, so `EntryState.Synchronized "x"` compiled
  anywhere.
- `QueueEntry` and `OfflineQueue` were public records, so
  `{ entry with State = ... }` and `{ queue with Entries = ... }` compiled
  anywhere.
- `CommitReceipt` was a public record, so a caller could fabricate one and
  pass it to `OfflineQueue.recordResult`.

Rehearsal 4's application bypassed its own synchronization state machine and
marked unsaved investigations as persisted (indy-init-rehearsal-004
`evidence/reviews/T33-findings-6950158.json`, DCT-002-B, R2-01). Rehearsal 5
avoided the bypass only by convention. A convention is not a guarantee. The
initiative asked for the guarantee to be structural.

## Decision

1. **Representations are internal to Arca.** `EntryState` is a record over an
   internal union (`EntryCase`); `QueueEntry`, `OfflineQueue` and
   `CommitReceipt` are records with internal representations and public
   read-only members of the 0.5 names. F# `internal` is used rather than
   `private` because it is the access level that `InternalsVisibleTo`
   extends, and Arca's own GitHub adapter must mint receipts.
2. **Reads keep their syntax.** A public multi-case active pattern in the
   `EntryState` module reads a state as `EntryState.Pending`,
   `EntryState.Synchronized token` and so on. Making the union cases
   internal while keeping same-named patterns is not possible in F#: name
   resolution finds the inaccessible case first (FS1093). The opaque record
   avoids that, so 0.5 pattern matches compile unchanged. The patterns are
   not constructors.
3. **Confirmation comes only from a provider.** `CommitReceipt.mint` is
   internal and called only where a provider has the provider's answer: the
   in-memory provider (Arca.Core) and the GitHub adapter after a published,
   non-forced ref update or a reconciliation that found the operation's key
   in history. A receipt carries the idempotency key of the operation it
   confirms.
4. **The queue checks the receipt against the entry.** `recordResult` and
   `recordReconciliation` accept a receipt only for the entry in flight (or
   outcome-unknown) whose operation carries the receipt's key; another
   operation's receipt is `QueueError.ReceiptMismatch`, a result for an entry
   not awaiting one is `NotApplicable`, and neither changes the queue.
5. **InternalsVisibleTo is first-party only:** `Arca.GitHub` (mints
   receipts) and `Arca.Tests` (builds states and receipts by hand for
   tests). `Arca.Limen` does not need it: its sign-out discard now calls the
   new public command `OfflineQueue.discardUnsent`, which can only remove
   entries that never reached the provider. The compile-failure project
   references Arca.Core as an application does, without it.
6. **Proof is by compilation.** `tests/Arca.CompileFailure` (the existing
   LCP-085 mechanism, extended) holds three forging fixtures (a Synchronized
   state, a receipt, a record copy of an entry's state and of a queue's
   entries) and a control file of legitimate reads that must compile.
   `PersistenceSafetyTests` asserts each fixture's exact error codes and that
   no other file fails.
7. **Version 0.6.0, a minor release that breaks the API.** Arca's policy (the
   CHANGELOG header, DF-ARCA-2026-0013) is SemVer 2.0.0 with the `0.x`
   caveat that "a minor release may change the API; pin an exact version".
   0.4.0 was the precedent for an API-changing minor. The persisted queue
   format (`arcaQueue` 1) is unchanged, so no stored queue migrates.

## Consequences

- Application code that constructed states, receipts or queue records stops
  compiling; the CHANGELOG's migration table maps each such use to a
  command. Reads compile unchanged. Test fakes that returned a hand-built
  receipt commit through `InMemoryStore().Provider` instead.
- An exhaustive match on `QueueError` gains a case.
- Echelon consumers pin exact versions, so nobody moves until they adopt
  0.6.0 deliberately. Merging the version change to `main` publishes
  `v0.6.0` through `release.yml`; recording it in echelon-registry is a
  separate step.

## Remaining runtime-only risks

The types close the application-code path. They do not close:

- **A dishonest first-party adapter.** Arca.GitHub can mint receipts through
  InternalsVisibleTo. Review of that assembly is the control.
- **Receipts are bound to a key, not to a provider instance.** An
  application that deliberately commits a queued operation to another
  provider (for example an in-memory store in production code) obtains a
  genuine receipt for that key, which the queue accepts.
- **The provider's expected-head guard.** "Accepted" means "applied on the
  state the operation was built on" only because the provider refuses a
  write whose head moved (GitHub's non-forced ref update; the in-memory
  provider's conflict check).
- **Untrusted local storage.** `OfflineQueue.decode` reads text Arca wrote.
  It refuses malformed text but cannot distinguish a well-formed edit of a
  stored state from Arca's own output.

## Alternatives considered

- **`private` representations.** Rejected: `private` in a namespace is not
  extended by InternalsVisibleTo, so the GitHub adapter could not mint
  receipts and the test assembly could not build fixtures without a weaker
  public API.
- **Union with internal cases and same-named active patterns.** Rejected:
  F# resolves `EntryState.Pending` to the inaccessible case, so every 0.5
  read would break. Verified by experiment before choosing the opaque record.
- **A runtime check only (for example a signed token in the receipt).**
  Rejected as the primary control: it would still let application code
  write `{ entry with State = ... }`. The compile-time boundary is the
  owner-approved approach; the idempotency-key binding complements it.
- **A patch release (0.5.1).** Rejected: the public API changes
  incompatibly.
