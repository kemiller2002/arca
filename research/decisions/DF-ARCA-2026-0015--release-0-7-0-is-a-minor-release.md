---
id: DF-ARCA-2026-0015
title: The release carrying bounded synchronization scheduling is 0.7.0, a minor release, because it adds public API; its one source-level caveat is the new CallFailure.ServerError case
status: accepted
version: 1.0.0
created: 2026-10-29
updated: 2026-10-29
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
  - src/Arca.Core/SyncSchedule.fs
  - src/Arca.Core/OfflineQueue.fs
  - src/Arca.GitHub/Api.fs
  - src/Arca.GitHub/Session.fs
  - research/decisions/DF-ARCA-2026-0013--release-0-5-0-is-a-minor-release.md
  - research/decisions/DF-ARCA-2026-0014--only-the-provider-confirms-a-write.md
tags: [release, versioning, semver, distribution]
provenance:
  contributions:
    EXE-20261029T135524249Z-054f4d84:
      operations: [created]
      at: 2026-10-29T13:56:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Version choice for the 0.7.0 release (WI-0039, arca#47)"
---

# DF-ARCA-2026-0015 — 0.7.0 is a minor release

- **Date:** 2026-10-29
- **Status:** accepted
- **Decision type:** release versioning (WI-0039; arca#47, merge `916d0ca`)

## Context

The repository owner instructed the release on 2026-10-29 ("Cut them, then
registry"). It carries arca#47 (WI-0037, R7-REM-002, ARCA-OFF-010; WI-0038,
R7-REM-001):

- Arca.Core: `Deferral` and `Deferral.ofFailure`, `SyncAttempt`,
  `OfflineSync.attempt` and `OfflineSync.drive`, `RetryPolicy`,
  `SyncSchedule` (with `SyncEvent.Resolved`, `isDue`, `wakeAt`) and
  `SaveState.ofQueue`.
- Arca.GitHub: `CallFailure.ServerError(status, aegis)` and the storage
  failure codes `ARCA.GITHUB.SERVER_ERROR` and `ARCA.GITHUB.NOT_LANDED`.
- Fixes: GitHub 5xx answers are transient rather than "network unavailable"
  or permanent; an uncertain ref update that reconciles as not landed resends
  once, then backs off; a permanent refusal while reading the change token is
  typed instead of retried forever.
- Real-browser multi-tab safety evidence for the IndexedDB queue (tests and
  verification only).

Arca's version policy is the CHANGELOG header: semantic versioning 2.0.0,
with the `0.x` caveat that "a minor release may change the API; pin an exact
version" (DF-ARCA-2026-0013 applied it to 0.5.0).

## Decision

1. **The version is 0.7.0.** SemVer 2.0.0 item 7 requires a MINOR increment
   when new, backwards compatible functionality is added to the public API.
   A patch (0.6.1) would hide new API, which Arca has never done.
2. **Compatibility.** `OfflineSync.step` and `run` keep their results and
   reason text; existing types keep their shape. The one source-level caveat
   is that the public union `CallFailure` gains the case `ServerError`, so an
   application that matches `CallFailure` exhaustively must add it (with
   warning 25 as an error, it will not compile until it does). Arca's own
   code is the only known matcher. In `0.x` that is permitted in a minor
   release, and it is recorded in the CHANGELOG.
3. **Behaviour changes are fixes.** The changed classification of GitHub 5xx
   answers and the bounded resend are corrections of ARCA-OUT-001 and
   ARCA-OFF-010 behaviour, listed under *Fixed*.
4. **Registry.** Recording 0.7.0 in echelon-registry and any change to
   echelon-current's selection or range is a separate, following change
   ("then registry"); this decision does not set it.

## Consequences

- The release workflow publishes `v0.7.0` from the merge to `main` that
  changes `<Version>`, with attested assets, as for every earlier release.
- Consumers stay on what the Registry selects until they run `conditor
  upgrade --current` against a set that selects 0.7.0.

## Alternatives considered

- **0.6.1 (patch).** Rejected: the release adds public API.
- **1.0.0.** Rejected: leaving `0.x` is a separate stability decision.
