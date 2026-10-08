---
id: DF-ARCA-2026-0010
title: EchelonFoundry.Arca.Limen is the opt-in Limen bridge; the IndexedDB queue keeps snapshot and fencing epoch in one record per namespace; a composer takes the namespace lock first and reports the durability mode
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
  - research/decisions/DF-ARCA-2026-0009--interim-localstorage-queue-one-owner-and-a-content-fence.md
tags: [offline, persistence, indexeddb, limen, multi-tab, web-locks, packaging]
provenance:
  contributions:
    EXE-20261008T192749303Z-b0d26ee7:
      operations: [created]
      at: 2026-10-08T20:26:27.726Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Limen bridge package, IndexedDB queue layout and composer (WI-0016)"
---

# DF-ARCA-2026-0010 — The Limen bridge, the IndexedDB queue layout, and the composer

- **Date:** 2026-10-08
- **Status:** accepted
- **Decision type:** architecture (WI-0016)

## Context

Limen 0.8.0 released `limen.store` version 2 and its F# packages
(`EchelonFoundry.Limen.Contract`, `.Guest`, `.Store`, with the `FakeStore`).
DF-LIMEN-2026-0005 places Arca's IndexedDB adapters in a new bridge package,
`EchelonFoundry.Arca.Limen`. That record and LCP-059, LCP-060, LCP-062,
LCP-065 and LCP-070 fix the semantics. The port stays unchanged: one queue
owner per namespace, a fencing epoch compared by `putIf`, a composer that
reports the durability mode it obtained, and evidence for `LocalQueueLost`.
This record settles what those sources leave open.

## Decision

### 1. The bridge references Arca.Core, Arca.GitHub and Limen.Store

WI-0016 and LCP-046 say the package references `Arca.Core` and
`Limen.Store` only. Two other requirements, though, depend on
`LocalStorageQueue`, which lives in `Arca.GitHub`:

- the composer's `LocalStorage` mode goes "through the existing
  `LocalStorageQueue`" (LCP-065);
- the migration decodes through `LocalStorageQueue.loaded` (LCP-066).

The bridge therefore also references `Arca.GitHub`. The constraint that
mattered is kept: `Arca.Core` and `Arca.GitHub` take no Limen dependency, so
Signal, which uses the GitHub provider and opts out of the queue, is not
affected. The architecture tests enforce both directions. Aegis comes in
through `Arca.GitHub` already, and the bridge also references
`EchelonFoundry.Aegis.Core` directly to classify its failures (ARCA-ARCH-007).

Rejected: moving `LocalStorageQueue` into `Arca.Core`. Chrona calls
`Arca.GitHub.LocalStorageQueue.own` today, so the move would break a
consumer for no gain.

### 2. One IndexedDB record per namespace holds the snapshot and the epoch

The database is `arca-queue`, inside the application namespace the host
registers. It has one store, `queues`, keyed by `namespace`, with one record
per Arca namespace:

`{ namespace, epoch, queue, migrated }`

- `namespace` is the localStorage key, `arca.queue.<app>[.<dataset>]`.
- `queue` is the canonical queue text.
- `migrated` is the SHA-256 marker of an adopted localStorage queue
  (WI-0020).

A new owner raises `epoch` with a `putIf`. Every save is a `putIf` over the
exact record that owner last read or wrote. One comparison therefore checks
both the fence (the epoch) and the content. A pre-empted owner's save, or
any save over a queue another writer changed, aborts as a conflict. It
writes nothing and fails as `QueueStoreFailure.Unavailable`. Diagnostics
name the cause `OwnedElsewhere` (stable code
`arca.limen.owned-elsewhere.fenced`).

Rejected: an epoch record beside a snapshot record. It needs two operations
for the same guarantee.

### 3. The lock is shared with the localStorage queue, and the composer takes it first

The lock name is `arca.queue/<app>[/<dataset>]`, which is
`LocalStorageQueue.lockName`, so a tab on either store excludes tabs on the
other. `LimenQueue.own` and `LimenQueue.takeOver` acquire the lock before
trying any store:

- `Busy`: the answer is `OwnedElsewhere`, whatever store the owner holds.
- `Held`: the composer tries the declared order, by default IndexedDB, then
  localStorage, then memory. It obtains exactly one store, so the queue is
  never written to two stores at once.
- `Unsupported` (no Web Locks): the composer does not use a durable store.
  If memory is declared it obtains `MemoryOnly`, because memory is per tab
  and so never multi-writer. Otherwise it answers `OwnershipUnsupported`.
  Unfenced multi-writer saves never happen.

`takeOver` steals the lock (OQ-LIMEN-IDB-001). The old owner hears `LockLost`
and is fenced by the epoch. Requests are not forwarded between tabs.

### 4. Evidence of unsent changes lives in localStorage

LCP-062 requires a marker kept outside IndexedDB. The key is
`arca.queue-held.<app>[.<dataset>]`. It is set when a saved queue holds
anything not yet synchronized, and removed when a save holds nothing unsent.
`LocalQueueLost` is reported only when the database was found newly created
(`Opened { created = true }`) while the marker is set. A first use, or a
device whose changes had all synchronized, is never reported as a loss.

### 5. Persistence is asked for once per owner, after the first offline write

After the first successful save of a queue that holds unsent entries, the
owner checks `persisted`. If storage is not yet persisted, it asks `persist`
(OQ-LIMEN-IDB-004). It never asks at open, and it records the answer in
diagnostics.

### 6. Budgets and limits

The default budget is 1,000,000 UTF-16 code units. That is the localStorage
queue's budget, so any queue moved from there fits. Before sending, a save
checks the record's UTF-8 size, and the size of the compare-and-put that
carries it, against the limits the pack reported in `Opened`. A queue over
either limit is refused whole as `QuotaExceeded`, and nothing is sent.

### 7. Sign-out discards entries; it does not range-delete the queue

LCP-070 says discarding uses `deleteRange`. The queue, though, is one
snapshot holding every account's entries. `OwnedQueue.Discard` therefore
removes only that account's pending, conflicted and refused entries, then
saves the result and counts it in diagnostics. In-flight and outcome-unknown
entries may already have landed, so they stay to be reconciled. The read
cache, which is keyed by account, does clear an account with a single
`deleteRange` (WI-0022).

### 8. Time is the host's

Diagnostics' times (last save, last sync) come from the host's clock
function in `LimenHost`. The adapter never reads a clock itself.

## Consequences

- Chrona adopts the IndexedDB queue (Chrona WI-0059) by replacing
  `LocalStorageQueue.own` with `LimenQueue.own` at its composition root. The
  `QueueStore` it receives is unchanged.
- WI-0020 adds the one-time move from the localStorage queue into this
  record, which is why the record reserves `migrated`.
- The browser harness runs the adapter in Chromium and WebKit.
