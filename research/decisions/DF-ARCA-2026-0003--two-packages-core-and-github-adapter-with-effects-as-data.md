---
id: DF-ARCA-2026-0003
title: Arca ships as two packages, a pure core (Arca.Core) and a GitHub adapter (Arca.GitHub) that describes GitHub conversations as data for the host to execute
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
  - research/decisions/DF-ARCA-2026-0001--github-only-behind-a-storage-interface.md
tags: [architecture, packaging, wasm, effects]
provenance:
  contributions:
    EXE-20261008T084806354Z-6573ebbf:
      operations: [created]
      at: 2026-10-08T08:54:30.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "User decisions of 2026-10-08 (coordinator brief), recorded with slice 1"
---

# DF-ARCA-2026-0003 — Two packages; the GitHub adapter describes effects as data

- **Date:** 2026-10-08
- **Status:** accepted. The user decided on two packages on 2026-10-08, which resolves OQ-ARCA-002.
- **Decision type:** architecture and packaging

## Context

OQ-ARCA-002 asked whether Arca should be one package or two. The user chose two:

- a pure F# core with no I/O;
- a separate GitHub adapter.

Two facts shape how the adapter runs:

- Arca's consumers are Limen engines compiled to browser WebAssembly
  (ARCA-ARCH-002).
- Limen's engine boundary rules (`architecture/boundary-rules.json`) forbid
  `HttpClient`, `System.Net.*`, `System.IO` and JavaScript interop in engine
  code. An engine requests effects and receives their results.

## Decision

1. **`EchelonFoundry.Arca.Core`** (assembly `Arca.Core`, namespace `Arca`):
   - record format, data location, versioning, conflict detection and merge,
     the commit and audit format, and the offline queue, as data and total
     functions;
   - no clock, randomness, browser, network, file or process access. Time,
     identifiers and entropy are inputs;
   - it depends on FSharp.Core and the BCL only.
2. **`EchelonFoundry.Arca.GitHub`** (assembly `Arca.GitHub`):
   - It **describes each GitHub conversation as data**: HTTP requests, waits
     and token requests, each with the continuation that reads the answer.
   - The host executes the requests. In the browser that is a Limen kernel's
     `Http` effect; elsewhere it is any HTTP client.
   - The adapter therefore holds no network, interop, thread or file
     authority, and runs unchanged in WebAssembly.
   - It depends on `Arca.Core` and on `EchelonFoundry.Aegis.Integration.GitHub`
     (ARCA-ARCH-007).
3. Both packages share one version. They are released together through the
   Echelon `nuget-library` release contract (DF-ARCA-2026-0006).
4. The architecture tests enforce the rules mechanically:
   - a lexical ban list over both packages' sources;
   - the dependency rules in each project file;
   - the built assemblies' references.

## Consequences

- A consumer whose pure domain needs only records and merge takes no HTTP or
  Aegis dependency.
- A Limen engine drives the adapter step by step from its own state machine.
  A test or a server drives it with a function. Neither changes Arca.
- The adapter's HTTP types mirror Limen's `HttpEffectRequest` and
  `EffectOutcome`, including `OutcomeUnknown`, so a host's translation is
  one to one.
