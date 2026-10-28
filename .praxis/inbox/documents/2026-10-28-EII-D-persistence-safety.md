# EII Workstream D — Persistence safety by construction (Arca)

- Received: 2026-10-28
- Source: Echelon Engineering Improvement Initiative, Workstream D, delegated
  by the repository owner's session (owner approval 2026-10-28).
- Target repository: kemiller2002/arca

## Problem

Rehearsal 4's application bypassed its own sync state machine and marked
unsaved investigations as persisted (indy-init-rehearsal-004
evidence/reviews/T33-findings-6950158.json, DCT-002-B, R2-01). Rehearsal 5
avoided the bypass only by convention. In Arca 0.5.0:

- `EntryState` (src/Arca.Core/OfflineQueue.fs) is a public union, so any
  caller can construct `EntryState.Synchronized "x"`; `QueueEntry` and
  `OfflineQueue` are public records, so `{ entry with State = ... }` works
  from application code.
- `CommitReceipt` (src/Arca.Core/Storage.fs) is a public record: any caller
  can fabricate one and feed `OfflineQueue.recordResult`.

## Owner decision

A breaking Arca minor release is approved to make "synchronized/confirmed"
unforgeable outside Arca: private/internal constructors and representations
with read-only active patterns or accessors, confirmation obtainable only
from authoritative provider results, and compile-failure tests. Record it as
a Decision Record (DF-ARCA-2026-NNNN), citing "owner approval 2026-10-28,
Echelon Engineering Improvement Initiative, Workstream D".

## Requirements

1. Application code (any assembly other than Arca's first-party assemblies)
   cannot construct `EntryState.Synchronized` or any state asserting the
   provider accepted a write, cannot construct a `CommitReceipt`, and cannot
   set `QueueEntry.State` directly. Every transition goes through
   OfflineQueue's commands. InternalsVisibleTo only for Arca's own adapter
   and test assemblies that genuinely mint receipts. Decoding a persisted
   queue keeps working.
2. Preserve all other behaviour and every existing test; tests that build
   these values by hand use InternalsVisibleTo rather than a weaker API.
3. Compile-failure fixtures in tests/Arca.CompileFailure, asserted by a test:
   forging Synchronized; forging CommitReceipt; mutating QueueEntry.State by
   record copy; plus a control file that compiles (legitimate reads).
4. Application-level tests against the InMemory provider and OfflineQueue
   for: duplicate writes, concurrent writes, network timeout to
   OutcomeUnknown, unknown outcomes reconciled both ways, credential change
   mid-queue, reload while an entry is InFlight, remote deletion, conflicting
   changes, nonconflicting changes, stale acknowledgment, out-of-order
   responses; a scenario-to-test table.
5. Next minor version, CHANGELOG entry with a migration section,
   docs/consuming-arca.md updated.
6. Remaining runtime-only risks documented honestly.
