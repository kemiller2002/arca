# Arca

Arca is the shared data layer for Echelon applications. It stores
authoritative records in GitHub repositories, runs in browser WebAssembly
behind Limen, and is published as the `EchelonFoundry.Arca` NuGet package. It
has a pure F# core (record formats, versioning, conflict detection and merge,
commit/audit format, offline change queue) and a GitHub adapter. It takes a
token provider and does not depend on Fides.

- Requirements: [`docs/requirements/ARCA-STORAGE-REQUIREMENTS.md`](docs/requirements/ARCA-STORAGE-REQUIREMENTS.md)
- Decisions: [`research/decisions/`](research/decisions/)
- Backlog: `./praxis work ready` (minimal slices WI-0003..WI-0008 come before Chrona's storage work)

The repository runs Praxis 3.7.2 and Ordo 1.5.0, installed by Conditor
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
