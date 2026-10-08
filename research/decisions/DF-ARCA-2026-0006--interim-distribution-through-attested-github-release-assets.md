---
id: DF-ARCA-2026-0006
title: Until nuget.org Trusted Publishing is set up, Arca ships its .nupkg files as Sigstore-attested GitHub release assets, registered in echelon-registry and installed by Conditor through a local feed
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
  - docs/distribution-short-term-plan.md
tags: [release, distribution, nuget, registry, conditor]
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

# DF-ARCA-2026-0006 — Interim distribution through attested GitHub release assets

- **Date:** 2026-10-08
- **Status:** accepted (user decision of 2026-10-08)
- **Decision type:** release and distribution

## Context

The Echelon `nuget-library` release pattern publishes to nuget.org through
Trusted Publishing. That needs the repository variable `NUGET_USER`, which
does not exist yet. The user will set it up later. Chrona must not wait for it.

Ordo already ships a package outside nuget.org. Its `ordo-core.nupkg` is a
GitHub release asset with a build-provenance (Sigstore) attestation, and its
sha256 is recorded in the echelon-registry release entry. Praxis consumes it
from a vendored local NuGet feed (`vendor/nuget/` with a lock file and
`NuGet.config` package-source mapping).

## Decision

1. Each Arca release publishes `EchelonFoundry.Arca.Core.<v>.nupkg`,
   `EchelonFoundry.Arca.GitHub.<v>.nupkg`, `checksums.txt` and
   `echelon-release.json` as assets of an immutable GitHub release `v<v>`.
   Every asset has a GitHub build-provenance attestation, which is
   Sigstore-signed.
2. Each release is registered in echelon-registry (`releases/arca/<v>.release.json`)
   with every artifact's sha256.
3. Consumers install the packages through **Conditor**, into a local NuGet
   feed in the consuming repository:
   - download the release assets;
   - verify their sha256 against the registry;
   - write a lock;
   - map the `EchelonFoundry.Arca.*` package source in `NuGet.config` to that
     feed only.
4. The nuget.org publication path stays in the release workflow, dormant: it
   runs only when the repository variable `NUGET_USER` exists. Setting the
   variable activates it with no other change.

## Consequences

- Chrona can consume Arca now, pinned by version and digest, with a verifiable
  supply chain.
- When nuget.org publishing starts, consumers can switch their package source
  mapping from the local feed to nuget.org. The package ids and versions stay
  the same.
- The short-term plan is in `docs/distribution-short-term-plan.md`.
