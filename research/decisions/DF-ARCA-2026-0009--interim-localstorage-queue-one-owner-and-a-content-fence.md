---
id: DF-ARCA-2026-0009
title: The interim localStorage queue gets one owner per namespace through a Web Lock, and every save is fenced on the stored text; the storage layout does not change
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - arca
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/ARCA-STORAGE-REQUIREMENTS.md
  - research/decisions/DF-ARCA-2026-0005--offline-queue-persists-to-localstorage-behind-a-port.md
tags: [offline, persistence, localstorage, multi-tab, web-locks, limen]
provenance:
  contributions:
    EXE-20261008T155254756Z-dca45d75:
      operations: [created]
      at: 2026-10-08T16:14:52.552Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Interim multi-tab fix for the localStorage offline queue (WI-0024)"
---

# DF-ARCA-2026-0009 — Interim localStorage queue: one owner, and a fence on every save

- **Date:** 2026-10-08
- **Status:** accepted
- **Decision type:** architecture (bug fix, WI-0024)

## Context

The `QueueStore` port saves the whole queue as one snapshot
(DF-ARCA-2026-0005). Tabs of one application share localStorage. Two tabs
each load the snapshot, enqueue, and save: the last save wins, and an entry
only the overwritten tab held is lost. Chrona's WI-0033 runs on this adapter.
The real fix, the Limen IndexedDB adapter (WI-0016, DF-LIMEN-2026-0005),
waits on Limen 0.8.0. The port must not change.

The eventual semantics are Limen LCP-059 and LCP-060: one owner per namespace
through a coordination-pack Web Lock; other tabs are told `OwnedElsewhere`;
a pre-empted owner's save writes nothing; FIFO per namespace; only the owner
synchronizes, and a new owner recovers and reconciles an in-flight entry.

## Decision

1. **One owner, through a Web Lock** (`LocalStorageQueue.own`, adapter API
   outside the port). The factory asks for the exclusive lock
   `arca.queue/<app>[/<dataset>]` without waiting or stealing, as a
   `QueueLockRequest` shaped like Limen's coordination `acquire`. It answers
   `Owned store`, `OwnedElsewhere`, or `OwnershipUnsupported`. The browser
   releases the lock when the owner closes, so another tab can take over.
2. **Every save is fenced on the stored text** (`LocalStorageQueue.store`,
   also used by `own`). A store remembers the text it last loaded or saved.
   A save first reads the key, and writes only if the key still holds exactly
   that text. Otherwise another tab wrote since, and the save fails as
   `QueueStoreFailure.Unavailable` with nothing written. A store that never
   loaded expects nothing stored. A corrupt queue is never overwritten.
3. **The layout does not change.** It is one key per namespace,
   `arca.queue.<app>[.<dataset>]`, in queue format 1. A 0.2.0 queue is
   adopted in place, so no migration exists to be interrupted.

## Alternatives

| Option | Why not |
|---|---|
| One key per entry, plus an ordering index | Without a lock, the shared index is itself a last-writer-wins snapshot. Two tabs also allocate the same sequence numbers and both synchronize the same entries, so FIFO and exactly-once hand-off fail. With a lock, it adds nothing that the single snapshot lacks. |
| Read-merge-write | The port receives whole snapshots, so a merge cannot tell a pruned entry from one it never saw. Merging two queues also re-sequences entries and breaks FIFO and the in-flight marker (LCP-066 rationale). |
| `storage` events | They tell other tabs that something changed, but prevent no loss. With one owner, no other tab holds a store to refresh. |
| An epoch beside the snapshot (LCP-059's fence) | Its job is to fence an owner whose lock was stolen. `own` never steals, and the content fence already refuses a save after any foreign write. An epoch would add a second key and a migration for no extra guarantee. |
| A lease lock kept in localStorage | localStorage has no compare-and-set, and Chromium propagates writes between tabs asynchronously, so a lease cannot exclude. It also needs timers, which background tabs throttle. |

## Consequences

- **Guarantee.** Under `own`, only one tab holds a store, so no save can
  overwrite another tab's entry. Only the owner synchronizes, and a new owner
  loads exactly the last saved queue. `OfflineSync`'s existing write-ahead and
  reconciliation give exactly-once hand-off.
- **Without the lock.** Callers of `store` alone (0.2.0's API, such as
  Chrona until it adopts `own`) no longer lose an acknowledged entry to a
  stale snapshot. The second tab's save is refused, and the entry stays in
  that tab. One residual window remains: two tabs whose read-then-write
  overlap within the browser's cross-tab propagation delay. Only `own` closes
  it.
- **For IndexedDB.** WI-0016 replaces the medium. Its factory has the same
  shape. WI-0020's migration reads the same `arca.queue.*` key as before.

## What reopens this

- The port gains per-entry operations.
- A host cannot provide Web Locks but must have exclusive ownership.
