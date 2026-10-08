---
id: DF-ARCA-2026-0001
title: Arca supports GitHub only, behind a provider-neutral storage interface, as a pure F# core plus a GitHub adapter that runs in browser WASM and never depends on Fides
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
tags: [architecture, storage, github, wasm, provider]
provenance:
  contributions:
    EXE-20261008T080342508Z-ce85c57b:
      operations: [created]
      at: 2026-10-08T08:04:42.674Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Derived from Signal, Chrona and Summa requirements and user decisions of 2026-10-08"
---

# DF-ARCA-2026-0001 — GitHub only, behind a storage interface

- **Date:** 2026-10-08
- **Status:** accepted. The user confirmed the GitHub-only assumption on 2026-10-08.
- **Decision type:** architecture

## Context

Signal (SIG ADM-003..006), Chrona (CHX-021..027, CHX-210..230) and Summa
(SUM0-002..021) all need GitHub-backed authoritative storage and list it as
missing. Each would otherwise build its own. All three are Limen/Forma F#
WASM applications. Signal's requirements reserve a future installable-service
provider (SIG ADM-006).

## Decision

1. Arca supports **one provider, GitHub**, behind a clean, provider-neutral
   interface with explicit, versioned capabilities (ARCA-ARCH-003). Other
   providers can be added later without changing consumer code.
2. Arca is a **pure F# core** (record formats, versioning, conflict detection
   and merge, commit/audit format, the offline change queue as data) plus a
   **GitHub adapter** (API calls, retries, rate limits, browser persistence
   through Limen interop) (ARCA-ARCH-001).
3. Arca **runs in browser WebAssembly** (ARCA-ARCH-002).
4. Arca **does not depend on Fides**. It takes a token-provider abstraction
   (ARCA-AUTH-001), and Fides supplies one.
5. Arca is published as a **NuGet package** through the Echelon
   `nuget-library` release contract (ARCA-ARCH-006).

## Consequences

- Applications reach storage only through Arca's interface, so a later
  service provider is an Arca change, not an application change.
- The core is testable without a network, and the adapter is verified in a
  real browser through Limen (ARCA-TEST-003).
- Fides and Arca can be released independently.
