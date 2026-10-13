---
id: DF-ARCA-2026-0013
title: The release carrying the batched read-cache save and pinned GitHub reads is 0.5.0, a minor release, because it adds public API without changing any; its echelon-current range stays >=0.4.1 <1.0.0
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
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
  - research/decisions/DF-ARCA-2026-0006--interim-distribution-through-attested-github-release-assets.md
tags: [release, versioning, semver, distribution]
provenance:
  contributions:
    EXE-20261013T123521720Z-05597783:
      operations: [created]
      at: 2026-10-13T12:35:56.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Version choice for the 0.5.0 release (WI-0034, arca#38, arca#39)"
---

# DF-ARCA-2026-0013 — 0.5.0 is a minor release

- **Date:** 2026-10-13
- **Status:** accepted
- **Decision type:** release versioning (WI-0034; arca#38, arca#39)

## Context

The release carries two changes:

- WI-0032 (arca#38, ARCA-OFF-008): `IndexedDbCache.SaveMany` and `KeepMany`,
  `MemoryReadCache.saveMany`, `ReadCache.saveSequentially`, and
  `IndexedDbReadCache.plan`.
- WI-0033 (arca#39, ARCA-API-006): the `PinnedReads` Core type,
  `GitHubConnection` and `GitHubStorage.connect`, `readAt`, `listAt`,
  `namespaceStateAt`, and `Snapshot.at`.

Arca's version policy is the CHANGELOG header: semantic versioning 2.0.0
(<https://semver.org/spec/v2.0.0.html>), with the `0.x` caveat that "a minor
release may change the API; pin an exact version." Arca has applied it
consistently:

- 0.2.1 and 0.4.1 were patches because the API was unchanged and each fixed
  a bug;
- 0.3.0 and 0.4.0 were minors because they added or changed API.

## Decision

1. **The version is 0.5.0.** SemVer 2.0.0 item 7 requires the MINOR version
   to be incremented when "new, backwards compatible functionality is
   introduced to the public API". Both changes add public types, functions
   and record fields, so a patch (0.4.2) would break the policy. Nothing is
   removed or changed incompatibly, so no larger step is called for.
2. **Backward compatibility was checked case by case.**
   - `StorageProvider`, `ReadCacheStore` and `GitHubStorage.provider` are
     unchanged. A new field on either record would break every application
     that builds the record itself, such as a test fake, so both features
     sit beside them instead.
   - Unpinned `Read`, `List` and `NamespaceState` still resolve the branch on
     every call, and `Snapshot.take` keeps its before-and-after token check.
   - `Save` on the IndexedDB cache is now `SaveMany [ entry ]`. Its
     behaviour (budget refusal, evictions, diagnostics) is unchanged, and
     the existing read-cache conformance suite and Limen read-cache tests
     pass unchanged.
   - The one source-level caveat is that `IndexedDbCache` gains two fields.
     Only `IndexedDbReadCache.openCache` builds that record; no consumer
     constructs it. The checked consumer is Chrona, which only holds the
     opened value.
3. **Registry range.** echelon-current's Arca range `>=0.4.1 <1.0.0` already
   admits 0.5.0, so it stays as it is. The registry change records the
   release, adds it to the catalog and moves echelon-current's selection
   from 0.4.1 to 0.5.0. Following the registry's normal process, that is a
   new echelon-current profile version, 1.19.0. In `0.x` consumers pin an
   exact version, so nobody moves until they run `conditor upgrade
   --current` and accept the plan.

## Consequences

- Consumers adopt 0.5.0 through Conditor exactly as they adopted 0.4.1.
  Without code changes they get the same behaviour. To get the latency
  gains they call `SaveMany` or `KeepMany`, and `GitHubStorage.connect`
  with `Pin` or `PinAt`, as `docs/consuming-arca.md` §3c and §5b describe.
- 0.4.1 stays published and selectable through older echelon-current
  profiles.

## Alternatives considered

- **0.4.2 (patch).** Rejected. It would hide new API in a patch, which
  SemVer forbids and Arca has never done.
- **1.0.0.** Rejected. Leaving `0.x` is a separate stability decision, not a
  side effect of a performance release.
