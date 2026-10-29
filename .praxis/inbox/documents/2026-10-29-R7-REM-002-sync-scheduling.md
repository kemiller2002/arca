# R7-REM-002: bounded synchronization scheduling and save state (Arca)

Source: Indy-init `docs/rehearsal-007/REMEDIATION-PLAN.md`, work package R7-REM-002, from Rehearsal 6 defect **R6-D-003** (Indy-init `docs/REHEARSAL-006-RESULTS.md`).

## Defect

In Rehearsal 6 a save that could not be sent because the device was offline was retried without backoff: 151 POSTs in 60 s for one change, while the page said "Saving to GitHub" instead of "Waiting for the connection".

## Why Arca owns part of it

`OfflineSync.step` returns `SyncStep.Deferred of string` and leaves *when* to try again to the host. Network loss arrives as `ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, …)`, the entry goes back to Pending, and nothing tells the host to wait. A host that steps again on Deferred spins. A permanent refusal while reading the change token (for example a revoked credential) is also returned as Deferred, so it is retried forever. Every application writes its own loop, so every application can repeat R6-D-003.

## Requirement (proposed ARCA-OFF-010)

Queue synchronization MUST be scheduled by Arca's pure core, never by an ad-hoc host loop:

1. A deferral is typed: offline, rate limited, transient, awaiting a credential, or permanent.
2. Transient failures back off exponentially with jitter, bounded by a maximum delay. Rate limits honour the provider's retry-after or reset evidence.
3. While the device is offline, nothing is sent until connectivity returns, except a slow bounded probe.
4. A permanent failure or a missing credential is never retried automatically.
5. Connectivity returning resets the backoff and sends at once.
6. The user-facing save state distinguishes: saved on this device, sending, waiting for the connection, retry scheduled, checking an unknown outcome, conflict, confirmed by the provider, failed permanently. "Confirmed" comes only from a Synchronized entry (ARCA-OFF-003, ARCA-OFF-009).

## Acceptance

- A simulated 60 s offline window produces at most the probe count of send attempts (zero POSTs: an offline probe reads, it never commits).
- A permanent refusal produces exactly one attempt.
- Recovery after connectivity returns synchronizes the entry and reports confirmed.
- `OfflineSync.step`'s existing results and reason strings are unchanged.
