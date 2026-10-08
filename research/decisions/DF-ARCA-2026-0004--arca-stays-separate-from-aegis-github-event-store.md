---
id: DF-ARCA-2026-0004
title: Arca stays separate from Aegis's GitHub event store because they serve different concerns; Arca reuses only Aegis's GitHub failure model
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
tags: [architecture, aegis, boundaries]
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

# DF-ARCA-2026-0004 — Arca stays separate from Aegis's GitHub event store

- **Date:** 2026-10-08
- **Status:** accepted. The user decided on 2026-10-08, which resolves OQ-ARCA-004.
- **Decision type:** architecture boundary

## Context

`EchelonFoundry.Aegis.Store.GitHub` already writes immutable fault-event files
to GitHub. OQ-ARCA-004 asked whether Arca should reuse it, or whether Aegis
should later move onto Arca.

## Decision

Arca and Aegis's GitHub event store **stay separate**. They serve different
concerns:

| | Aegis event store | Arca |
|---|---|---|
| What it stores | Operational fault evidence about the software | Authoritative application records: an application's domain data |
| Write pattern | Append-only, immutable events; no conflicts by construction | Mutable and immutable records; optimistic concurrency, conflict detection, three-way merge (ARCA-CON) |
| Atomicity | One event per file | One logical operation = one atomic commit across several records (ARCA-COMMIT-001) |
| Failure semantics | Best-effort delivery of diagnostics; losing one event must not break the application | No lost updates; OutcomeUnknown is explicit and reconciled (ARCA-OUT) |
| Owner of meaning | Aegis | Each application |

If the fault store depended on the data layer, every Arca outage would also
silence the evidence of that outage. If Arca depended on the fault store, its
domain model would be shaped by diagnostics. Keeping them apart keeps each
failure domain independent.

Arca **does** reuse Aegis's GitHub *failure model* (`GitHubFailure.T` and its
mapping) to classify unexpected operational failures (ARCA-ARCH-007), so the
same GitHub failure means the same fault everywhere.

## Consequences

- Aegis is not migrated onto Arca, and Arca does not import
  `Aegis.Store.GitHub`. The architecture tests check that Arca.GitHub
  references only `Aegis.Integration.GitHub`.
- Revisit only if an application needs its fault history as authoritative
  domain data, which would be an application decision, not Arca's.
