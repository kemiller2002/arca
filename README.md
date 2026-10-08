# Arca

Arca is the shared data layer for Echelon applications. It stores
authoritative records in GitHub repositories, runs in browser WebAssembly
behind Limen, and ships as three NuGet packages of one version
([DF-ARCA-2026-0003](research/decisions/DF-ARCA-2026-0003--two-packages-core-and-github-adapter-with-effects-as-data.md),
[DF-ARCA-2026-0010](research/decisions/DF-ARCA-2026-0010--limen-bridge-package-indexeddb-queue-layout-and-composer.md)):

- `EchelonFoundry.Arca.Core`: the pure F# core (data location, record format,
  versioning, conflict detection and merge, commit/audit format, offline
  change queue). No I/O.
- `EchelonFoundry.Arca.GitHub`: the GitHub adapter. It describes each GitHub
  conversation as data, and the host executes the HTTP requests (a Limen
  kernel's Http effect in the browser), so it runs in WebAssembly without
  network or interop authority of its own.
- `EchelonFoundry.Arca.Limen` (from 0.3.0, opt-in): the IndexedDB offline
  queue over Limen's store pack, with one owner per namespace, fenced saves,
  a durability-mode composer, and the one-time move from the localStorage
  queue; and the offline-start read cache. Only applications that reference
  it take a Limen dependency.

Arca takes a token provider and does not depend on Fides. Until nuget.org
publishing is set up, releases ship as attested GitHub release assets that
Conditor installs into a local feed
([short-term plan](docs/distribution-short-term-plan.md)). To use Arca, see [`docs/consuming-arca.md`](docs/consuming-arca.md).

- Requirements: [`docs/requirements/ARCA-STORAGE-REQUIREMENTS.md`](docs/requirements/ARCA-STORAGE-REQUIREMENTS.md)
- Decisions: [`research/decisions/`](research/decisions/)
- Backlog: `./praxis work ready` (minimal slices WI-0003..WI-0008 come before Chrona's storage work)

The repository runs Praxis 3.7.2 and Ordo 1.5.0, and builds against Limen 0.8.0's F# packages (`limen-fsharp`), from echelon-current 1.10.0, installed by Conditor
(`conditor.json`). An older installation may also have `./ros`, a
compatibility alias of `./praxis`.

## Start here

1. Read [`AGENTS.md`](AGENTS.md) and [`BOOTSTRAP.md`](BOOTSTRAP.md).
2. Complete [`PROJECT-CHARTER.md`](PROJECT-CHARTER.md).
3. Establish the baseline in [`context/CURRENT-STATE.md`](context/CURRENT-STATE.md).
4. Select the first bounded mission and its observable acceptance criteria.
5. Record durable evidence, decisions, and handoffs as the work proceeds.

## Local operating commands

```bash
./praxis work begin --id TASK-001 --occurred-at TIMESTAMP --type task
./praxis work context TASK-001
./praxis status
./praxis registry check
./praxis registry build
./praxis validate
```

`work context` reports legal actions and completion evidence. Validation errors include repair instructions; use `./praxis validate --json` for machine-readable output. Complete work with explicit evidence paths as described in `docs/work-protocol.md`.

The installed snapshot is self-contained. It does not read from the source Praxis
repository. `.ros/installation.json` records the package version and checksums
of installed files.

## Pilot rule

The operating system is itself under evaluation. Do not infer that
Arca is a validated discipline, method, or product merely because
the repository follows a rigorous process. Measure whether the process improves
decisions, traceability, handoffs, and rework relative to the declared baseline.
