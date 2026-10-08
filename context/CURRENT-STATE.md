# Arca current state

## Repository status

Newly initialized with Praxis 3.7.2.

## Observed facts

- No domain evidence has been accepted.
- No vertical slice has been selected.
- No discipline-boundary claim has been tested.

## Assumptions

- A small, concrete communication problem can exercise the operating model.

## Active work

Complete the charter and select the first bounded pilot slice.

## Largest decision-relevant unknown

Which first use case will provide measurable value while exposing the important
communication constraints?

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
