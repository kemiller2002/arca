---
id: DF-ARCA-2026-0007
title: Arca is aligned to the echelon-current 1.3.0 release set and tests with a real xUnit dotnet test project (the conditor#54 scaffold)
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
tags: [release-set, conditor, testing]
provenance:
  contributions:
    EXE-20261008T084806354Z-6573ebbf:
      operations: [created]
      at: 2026-10-08T08:54:32.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "User decisions of 2026-10-08 (coordinator brief), recorded with slice 1"
---

# DF-ARCA-2026-0007 — echelon-current 1.3.0 and an xUnit test project

- **Date:** 2026-10-08
- **Status:** accepted (user decisions of 2026-10-08); implemented in arca#2 (WI-0015)
- **Decision type:** tooling

## Decision

1. Arca's Echelon tooling follows the **echelon-current 1.3.0** release set:
   - the linux-x64 resolved set, sha256 `f917318c…7d48`;
   - applied with `conditor upgrade --current`;
   - Praxis 3.7.2, Ordo 1.5.0 and Communication Engineering 1.0.0;
   - Aegis 1.0.0 is the set's Aegis selection, used for
     `EchelonFoundry.Aegis.Integration.GitHub`.
2. The test project is a **real xUnit `dotnet test` project**, regenerated from
   the conditor#54 scaffold. CI fails a run that is empty, skips a test, or
   has a failing test. Property-based tests use FsCheck through
   `FsCheck.Xunit`.

## Consequences

- Test evidence reaches every TRX consumer, such as the Dokimos quality gate.
- Observed while aligning Arca: Conditor (main at 71e33b9) treats a scaffold
  file that differs from the scaffold as drift and refuses to plan. Once a
  project has grown past its scaffold, as Arca's two-package layout does
  (DF-ARCA-2026-0003), a later `conditor upgrade --current` cannot proceed.
  This is a Conditor defect to fix in Conditor, not by keeping Arca at its
  scaffold.
