# Arca current state

## Repository status

Bootstrapped by Conditor on 2026-10-08 (WI-0001): Praxis 3.7.2, Ordo 1.5.0,
Communication Engineering 1.0.0, and the `fsharp-nuget-library` scaffold. The
library is a placeholder. Requirements and the backlog were captured in
WI-0002.

## Observed facts

- No storage behaviour exists yet. `src/Arca/Library.fs` is the scaffold placeholder.
- The requirements are derived from Signal, Chrona and Summa and cite their IDs.
- The release workflow never publishes 0.0.0 and needs `NUGET_USER` to publish.

## Decisions

- DF-ARCA-2026-0001: GitHub only, behind a storage interface; pure core plus adapter; WASM; no Fides dependency.
- DF-ARCA-2026-0002: per-deployment data location; application-owned namespaces; separate repositories per permission boundary; no at-rest encryption for now.

## Active work

Slice 1 (WI-0003), then slices 2-6 for Chrona, which the user builds first.

## Largest decision-relevant unknown

Durable browser storage for the offline queue: Limen has no IndexedDB
capability yet (OQ-ARCA-003).

## Baseline

Not yet recorded. Define how the same slice would be approached without Praxis and
which comparison measures are feasible.

<!-- conditor:ordo-baseline:start -->
# Current State — Conditor Baseline

This is the initial greenfield baseline. It records what Conditor can prove before application implementation begins.

## Established facts

- The declared Echelon capabilities were planned and installed through their supported lifecycle contracts.
- Accepted requirement artifacts were materialized from immutable sources declared by `conditor.json`.
- Canonical execution contract: `none declared`.
- The generated `src/Arca/Library.fs` value is a scaffold placeholder, not a claim that the application domain has been modeled.

## Accepted requirement artifacts

- none declared

## Unknowns

- Application domain concepts have not yet been derived.
- Important legal state and illegal states have not yet been identified.
- Legal transitions, invariants, guards, capabilities, and effect obligations have not yet been established.
- Semantic feature boundaries and ownership are not yet known.

## Obligations before implementation expands

1. Read the canonical execution contract and its normative references.
2. Identify the smallest domain concepts and legal state required by the first meaningful vertical behavior.
3. Establish legal transitions, invariants, guards, capabilities, and explicit effects required by that behavior.
4. Update `SDE-MAP.md` and create feature manifests only when real semantic ownership is known.
5. Preserve unknowns explicitly rather than converting missing knowledge into assumptions.

## Next action

Execute the initial Praxis mission using the accepted governing inputs and establish the first evidence-backed Ordo semantic slice.
<!-- conditor:ordo-baseline:end -->
