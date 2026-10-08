# Arca handoff

## Objective

Bootstrap Arca as a greenfield Praxis pilot.

## Current state

- Praxis 3.7.2 greenfield profile installed on 2026-10-08.
- Project charter is a draft.
- No first vertical slice, evidence record, hypothesis, or experiment has been
  accepted.
- The operating system is under evaluation.

## Validation

Run:

```bash
./praxis registry check
./praxis validate
```

## Continuity

An executor session is disposable. Before handing off, commit and push the
work, record `./praxis work checkpoint --id ID --occurred-at NOW --summary ...
--next-action ...`, and push the `.ros/` state. A successor reads
`./praxis work context ID --text` and takes over with `./praxis work continue`.

## Unresolved questions

1. What concrete communication problem and user should the first slice serve?
2. What baseline workflow will be used for comparison?
3. What data, privacy, safety, and accessibility constraints apply?
4. Which outcome would distinguish useful engineering from additional process?

## Next action

Complete `PROJECT-CHARTER.md`, choose the first bounded outcome, and record its
baseline and acceptance criteria in `context/CURRENT-STATE.md`.
