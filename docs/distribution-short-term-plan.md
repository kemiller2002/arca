# Arca distribution: short-term plan

Decision: [DF-ARCA-2026-0006](../research/decisions/DF-ARCA-2026-0006--interim-distribution-through-attested-github-release-assets.md).

## Why there is a short-term plan

The Echelon `nuget-library` pattern publishes to nuget.org through Trusted
Publishing. That needs the repository variable `NUGET_USER` (the nuget.org
account that owns the Trusted Publishing policy), which does not exist yet.
Chrona needs Arca now, so Arca ships the way Ordo ships `ordo-core.nupkg`
until then.

## While `NUGET_USER` is unset

1. **Release.** Raising `<Version>` in `Directory.Build.props` on `main` runs
   the release workflow. The workflow then:
   - builds, tests and packs both packages once;
   - writes `checksums.txt` and the `echelon.release/v2` manifest
     (`echelon-release.json`) from those same bytes;
   - attests every asset with GitHub build provenance, which is
     Sigstore-signed;
   - creates the immutable GitHub release `v<version>` with
     `EchelonFoundry.Arca.Core.<version>.nupkg`,
     `EchelonFoundry.Arca.GitHub.<version>.nupkg`, `checksums.txt` and
     `echelon-release.json`.

   The nuget.org push step is skipped, and the workflow says so.
2. **Register.** The release is recorded in echelon-registry as
   `releases/arca/<version>.release.json`, with every artifact's sha256 and an
   `arca` system entry.
3. **Consume.** A consumer installs the packages with Conditor, which:
   - downloads the release assets;
   - checks each one's sha256 against the registry entry;
   - places the files in the consumer's local feed (`vendor/nuget/`) with a
     lock that records each package's URL and sha256;
   - maps `EchelonFoundry.Arca.*` to that feed only, in `NuGet.config` package
     source mapping, so it can never come from a public feed.

   The consumer then references the packages by exact version.
4. **Verify.** Anyone can check provenance with
   `gh attestation verify <file> --repo kemiller2002/arca`.

## Status (0.4.1, 2026-10-09)

| Step | Where |
|---|---|
| Release | [`v0.4.1`](https://github.com/kemiller2002/arca/releases/tag/v0.4.1) from `8274543` (#36), published by `release.yml` run 37882697760. It is a patch release: `OfflineQueue.revise` keeps the entry's account id, so a revised entry still matches at sign-out, and refuses an erasure (WI-0031). Downloaded into an empty directory, the three `.nupkg` files match `checksums.txt`; all five assets match GitHub's digests and verify against `release.yml` on `main`; a tampered copy is rejected. Earlier: [`v0.4.0`](https://github.com/kemiller2002/arca/releases/tag/v0.4.0) from `a252caf` (#34), published by `release.yml` run 37873327778. It changes the API: namespace-scoped change tokens (#30), explicit erasure of immutable records (#32) and stable account ids at sign-out (#33). It is built against limen-fsharp 0.9.0 (#31). Downloaded into an empty directory, the three `.nupkg` files match `checksums.txt`. All five assets match GitHub's digests, and each passes `gh attestation verify` against `release.yml` on `main`; a tampered copy is rejected. Earlier: [`v0.3.0`](https://github.com/kemiller2002/arca/releases/tag/v0.3.0) from `6f3acc7` (#28). It holds three packages: `EchelonFoundry.Arca.Core`, `.GitHub` and the new `.Limen`, which carries the IndexedDB offline queue, the one-time move from the localStorage queue and the read cache (#23..#27). The three `.nupkg` files, `checksums.txt` and `echelon-release.json` are attested. The release run verified them, and they were verified again independently from an empty directory. `v0.2.1`, `v0.2.0` and `v0.1.0` remain available. |
| Registry | `releases/arca/0.4.1.release.json` and echelon-current **1.18.0**, with Arca `>=0.4.1 <1.0.0` as an optional project binding, so the affected 0.4.0 is no longer selected (echelon-registry#63, merge `e710e24`). 0.4.0 was recorded in #59 (merge `616d10e`, 1.16.0). Earlier: `releases/arca/0.3.0.release.json` and echelon-current **1.12.0**, with Arca `>=0.3.0 <1.0.0` as an optional project binding (echelon-registry#55, merge `d76ea0c`). Limen 0.8.0 and limen-fsharp came in #52 (1.10.0) and moved to 0.9.0 in #53 (1.11.0). Limen.Store is unchanged between them, and Arca.Limen 0.3.0 was proven on limen-fsharp 0.9.0 from the released assets. 0.2.1 came in #51 (1.9.0). |
| Conditor | conditor#59 (merge `8dffc14`): the NuGet release-asset feed (`docs/nuget-feed-contract.md` in Conditor). Arca itself installs limen-fsharp through it (`vendor/nuget`). |
| Consumers | [`consuming-arca.md`](consuming-arca.md). Chrona WI-0059 adopts the IndexedDB queue (section 5a), and WI-0057 the read cache (section 5b). |

## When `NUGET_USER` is set

Nothing else changes in Arca. The dormant nuget.org step pushes both packages
through Trusted Publishing. The GitHub release keeps its assets. A consumer
moves to nuget.org by removing the local-feed mapping for `EchelonFoundry.Arca.*`;
its package references stay the same.

## Exit criteria

- `NUGET_USER` exists, and one release has reached nuget.org with the same
  sha256 as its GitHub release asset.
- Every consumer has dropped its local-feed mapping for Arca, or chosen to
  keep the pinned local feed.
