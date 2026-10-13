# Arca measurement baseline: where the evidence lives

This file is an index for the Echelon measurement amendment of 2026-10-13. It
copies no telemetry. It names where Arca's historical evidence is, when each
Echelon capability arrived in this repository, and what is missing. Commit
hashes are this repository's own (`kemiller2002/arca`).

## Evidence sources

| Source | Where | Range | Notes |
|---|---|---|---|
| Praxis work items | `.ros/work/queue.json` (and `queue.md`) | WI-0001 .. WI-0035 | Status, title, tags and source reference for each item. |
| Praxis lifecycle events | `.ros/events/events.jsonl` | from `0adc288` (2026-10-08T08:03:16Z) | `work.started`, `work.checkpointed` and `work.completed`, with actor, execution id, checkpoint commit and evidence paths. |
| Praxis execution telemetry | `.ros/telemetry/executions/EXE-*.json` | 35 executions, the first `EXE-20261008T080258011Z-5eda7590` | One per execution. Telemetry steps appear only where an agent recorded them. |
| Metric registry | `telemetry/metrics.json` | from `0adc288` | Metric definitions only, not observations. |
| Context and handoff | `.ros/context/current.json`, `HANDOFF.md` | | The current projection and the handoff. |
| Decision records | `research/decisions/DF-ARCA-2026-*.md` | DF-0001 .. DF-0013 | Material decisions, with provenance front matter. |
| Pull requests | GitHub `kemiller2002/arca` PRs #1 .. #43 | first merge `d53e4c8` (#1, 2026-10-08) | Each PR body holds the tests run and the release verification. From #40, it also holds a Measurement section. |
| CI history | GitHub Actions on `kemiller2002/arca` | from 2026-10-08 | Workflows: `build-and-test`, `praxis-validation`, `echelon-foundations`, `release`, `browser-verification`. Runs are kept for GitHub's retention period, not in the repository. |
| Releases | GitHub releases `v0.1.0` .. `v0.5.0` | | Each carries `checksums.txt`, `echelon-release.json` and build-provenance attestations. `docs/distribution-short-term-plan.md` records each release's run and verification. |
| Registry records | `kemiller2002/echelon-registry` `releases/arca/*.release.json` | | Which echelon-current version selected which Arca release. |

## When each capability arrived

| Capability | Adopted | Commit | Evidence |
|---|---|---|---|
| Praxis 3.7.2 (work protocol, telemetry, provenance, validate) | 2026-10-08 | `0adc288` (WI-0001), re-aligned in `07c4ee8` (WI-0015) | `.echelon/ros.json`, `ros.json`; `praxis-validation.yml` from `0adc288`. Still pinned at 3.7.2 at `54da6f0`. |
| Conditor bootstrap (`conditor.json`, resolved-release-set authority) | 2026-10-08 | `0adc288` (WI-0001, echelon-current 1.2.0) | `.conditor/authority/resolved-release-set.json`; moved to 1.3.0 in `07c4ee8` (WI-0015, PR #2 `cceeb20`). |
| Conditor NuGet release-asset feed (`vendor/nuget`) | 2026-10-08 | `19c9763` (WI-0016) | limen-fsharp installed through Conditor. Moved to limen-fsharp 0.9.0 in PR #31 (`cce90c4`, WI-0027). |
| Ordo 1.5.0 (host tool) | 2026-10-08 | `0adc288` | Declared in `conditor.json`. Ordo construction (ECIR, typed plans, `praxis construct`) has **not been used** in this repository. `docs/ordo-observation.md` describes observation only. |
| Limen (IndexedDB queue and read cache through limen-fsharp) | 2026-10-08 | start `0603cca`, merge `bc3d3e2` (PR #25, WI-0016) | `src/Arca.Limen`. |
| Echelon release pattern (attested GitHub release assets, release contract) | 2026-10-08 | `release.yml` from `0adc288`; first release `a3143fc` (PR #9, v0.1.0, WI-0013) | DF-ARCA-2026-0006. |
| Echelon foundations check | 2026-10-08 | `0adc288` | `echelon-foundations.yml`. |
| Real-browser (WASM) verification | 2026-10-08 | `e92dd48` | `browser-verification.yml`. |
| Durable checkpoints (`work checkpoint`) | 2026-10-08 | first `work.checkpointed` event with WI-0001 (`0c6c5b7`) | `.ros/events/events.jsonl`. |
| Gated merge with `sha=` pinned to a fully green head | missing | | The repository has no record of the merge rule used before PR #40. From #40 (merge `ddff5aa`), the Measurement section of each PR body records it. |
| Dokimos checks | not adopted | | No Dokimos configuration or workflow in this repository. |
| Checklist enforcement beyond `praxis validate` | not adopted | | |
| OS containment | missing | | Nothing in the repository records the executor's sandbox. |

## What is missing

- **Tokens and cost.** No execution record holds provider-reported tokens or
  cost. The runtime did not expose them. They are unavailable, not zero.
- **Step telemetry before 2026-10-13.** Most executions have no recorded
  steps, so step-level time is unavailable for them.
- **CI wait and repair time before PR #40.** These can be derived only from
  GitHub Actions run timestamps while GitHub retains them. The repository
  holds no copy.
- **First-pass outcome before PR #40.** It can be derived from Actions runs
  per head commit, subject to the same retention.
