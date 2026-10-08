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

## Status (0.2.0, 2026-10-08)

| Step | Where |
|---|---|
| Release | [`v0.2.0`](https://github.com/kemiller2002/arca/releases/tag/v0.2.0) from `2577cd6`. Both `.nupkg` files, `checksums.txt` and `echelon-release.json` are attested, and the release run verified them. `v0.1.0` (from `a3143fc`) remains available. |
| Registry | `releases/arca/0.2.0.release.json` and echelon-current **1.6.0**, with Arca `>=0.2.0 <1.0.0` as an optional project binding (echelon-registry `61a81f0`, records in #47). 0.1.0 came in echelon-registry#45 (merge `385949f`, echelon-current 1.4.0). REG-REL-014 lets one NuGet release ship a package family. |
| Conditor | conditor#59 (merge `8dffc14`): the NuGet release-asset feed (`docs/nuget-feed-contract.md` in Conditor). |
| Consumers | [`consuming-arca.md`](consuming-arca.md) |

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
