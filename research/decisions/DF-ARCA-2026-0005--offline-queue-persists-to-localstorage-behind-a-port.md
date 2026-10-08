---
id: DF-ARCA-2026-0005
title: The offline change queue persists to localStorage through Limen's Storage effect, behind a small queue-store port, until a Limen IndexedDB adapter replaces it
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
tags: [offline, persistence, limen, indexeddb, localstorage]
provenance:
  contributions:
    EXE-20261008T084806354Z-6573ebbf:
      operations: [created]
      at: 2026-10-08T08:54:31.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "User decisions of 2026-10-08 (coordinator brief), recorded with slice 1"
---

# DF-ARCA-2026-0005 — Offline queue: localStorage now, behind a port; IndexedDB later

- **Date:** 2026-10-08
- **Status:** accepted. This resolves OQ-ARCA-003.
- **Decision type:** architecture

## Context

ARCA-OFF-002 needs the offline change queue to survive a refresh and a
restart. The first direction (2026-10-08) was to add an IndexedDB capability
to Limen and use it. That direction was then revised. The user approved the
coordinator's recommendation: use **localStorage until Limen gets IndexedDB**,
put the queue behind a small storage interface, and add no IndexedDB work to
Limen in this effort.

**What Limen already has (observed 2026-10-08 in `kemiller2002/limen` main):**

- Limen 0.7.x ships an optional IndexedDB store pack (`./capabilities/store`,
  contract unit `limen.store` v1, LCP-018, limen#28).
- The pack declares schema versions and indexes. It runs atomic transactions,
  offers `putIf` compare-and-put, and reports typed outcomes.
- Its F# bindings are generated (`guests/fsharp/Limen.Contract/Generated/Store.fs`).
  They are not published as a consumable package: `Limen.Contract` is not
  packable, and Limen releases only the npm package. Chrona, for example,
  hand-codes Limen's protocol in F#.

The real gap is therefore a consumable F# store binding plus an Arca adapter,
not the browser capability itself. That gap is the follow-up.

## Decision

1. The core models the queue as plain data (ARCA-OFF-001). Persistence goes
   through a small **queue-store port**: load the persisted snapshot and save
   a new one, each with typed failures.
2. Arca provides a **localStorage adapter** for that port. It speaks Limen
   Core's built-in `Storage` effect (`get`/`set`/`remove`, outcomes `Success`,
   `unavailable`, `quota-exceeded`) as data. It does not use JavaScript
   interop, so it stays inside Limen's engine boundary.
3. The adapter enforces a size budget and fails explicitly with a typed
   quota-exceeded failure. It never truncates or drops queued changes
   silently. GitHub stays authoritative (ARCA-OFF-006).
4. **Follow-up work item:** a Limen IndexedDB adapter for the queue-store
   port, over `limen.store`. It is blocked on Limen publishing its F# store
   binding as a consumable package. It replaces the localStorage adapter
   without touching the core.

## Consequences

- The queue's durability is bounded by localStorage (typically about 5 MB per
  origin, strings only). The size budget makes the limit visible instead of
  silent.
- Moving to IndexedDB changes only which adapter the host wires in. A one-time
  migration of the persisted snapshot is part of that follow-up.
