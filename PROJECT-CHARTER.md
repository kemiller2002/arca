---
id: PROJECT-CHARTER-arca
title: Arca Project Charter
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
provenance:
  contributions:
    EXE-20261008T080342508Z-ce85c57b:
      operations: [modified]
      at: 2026-10-08T08:04:56.843Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Re-anchor the charter on Arca's purpose"
---

# Arca project charter

## Purpose

Arca is the shared data layer for Echelon applications. It stores
authoritative application records in GitHub repositories and is published as
a NuGet package. It replaces the "GitHub storage" that Signal, Chrona and Summa
would otherwise each build themselves.

## Intended users

- **Applications**: Chrona first, then Summa, then Signal and later apps. They
  are Limen/Forma F# WebAssembly UIs that need durable, auditable,
  concurrency-safe storage in GitHub.
- **Developers and agents** building those applications, who need one storage
  contract and one conformance suite instead of three.

## First bounded outcome

The minimal slices that Chrona's storage work needs (backlog slices 1-6):

1. a pure core and a GitHub adapter, running in browser WASM;
2. the per-deployment data location and application-owned namespaces;
3. the canonical record format;
4. optimistic concurrency, merge and the commit format;
5. the token-provider port;
6. the GitHub adapter with OutcomeUnknown handling.

## Included

- The pure F# core: record formats, versioning, conflict detection and merge,
  the commit/audit format, and the offline change queue as data.
- The GitHub adapter: API calls, retries, rate limits, and browser persistence
  through Limen interop.
- The conformance suite and an in-memory provider for consumers.

## Excluded

- Domain meaning: each application decides what its records mean and how
  semantic conflicts resolve.
- Authentication: Arca takes a token provider and does not depend on Fides.
- Providers other than GitHub (DF-ARCA-2026-0001), and at-rest encryption
  (DF-ARCA-2026-0002). Both may come later.
- Large binary artifacts.

## Success criteria

- Chrona, then Summa and Signal, persist through Arca and pass its
  conformance suite.
- No lost updates: conflicting writes are always detected, and unknown
  outcomes are always explicit.
- No token ever appears in stored data, commits, logs or telemetry.
- Every requirement traces to a consumer requirement
  (`docs/requirements/ARCA-STORAGE-REQUIREMENTS.md`).

## Constraints and assumptions

- It must run in .NET browser WebAssembly.
- It is written in a functional style.
- It is released through the Echelon `nuget-library` release contract.
- GitHub permissions are per repository, so separate repositories provide the
  permission boundaries between applications.

## Owners and decision authority

Owner: kemiller2002. Decisions are recorded in `research/decisions/`.
