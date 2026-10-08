# Arca backlog plan

Captured 2026-10-08 (WI-0002) from [`ARCA-STORAGE-REQUIREMENTS.md`](ARCA-STORAGE-REQUIREMENTS.md). Build order, per the user: Arca's and Fides's minimal slices first, then **Chrona**, then Summa, then the rest. Slices 1-6 (WI-0003..WI-0008) are the minimum Chrona's storage work needs; 8 and 9 add integrity and the offline queue that Chrona's offline use needs. The test runner (`tests/Arca.Tests`) fails if any requirement is not named by an open work item.

| Order | Work item | Slice | Depends on |
|---:|---|---|---|
| 1 | WI-0003 | Arca slice 1: package layout - pure core and GitHub adapter packages, WASM-safe, Aegis-bound (ARCA-ARCH-001..007) | - |
| 2 | WI-0004 | Arca slice 2: per-deployment data location, app-owned namespaces and path safety (ARCA-LOC-001..010) | WI-0003 |
| 3 | WI-0005 | Arca slice 3: canonical JSON record format, stable ids, manifests and schema versioning (ARCA-REC-001..007) | WI-0004 |
| 4 | WI-0006 | Arca slice 4: optimistic concurrency, conflict detection, three-way merge and commit/audit format (ARCA-CON-001..004, ARCA-COMMIT-001..006) | WI-0005 |
| 5 | WI-0007 | Arca slice 5: token-provider port, identity resolution and capability snapshot (ARCA-AUTH-001..005) | WI-0003 |
| 6 | WI-0008 | Arca slice 6: GitHub adapter - atomic commits, conditional requests, rate limits, OutcomeUnknown and idempotency (ARCA-API-001..005, ARCA-OUT-001..002, ARCA-COMMIT-001/006) | WI-0006 and WI-0007 |
| 7 | WI-0009 | Arca slice 7: provider conformance suite, in-memory provider and real-browser WASM verification (ARCA-TEST-001..004) | WI-0008 |
| 8 | WI-0010 | Arca slice 8: storage content is untrusted input - validation, manual-edit and tamper detection (ARCA-INT-001..004) | WI-0005 and WI-0008 |
| 9 | WI-0011 | Arca slice 9: offline change queue as data with durable browser persistence through Limen (ARCA-OFF-001..006) | WI-0006 and WI-0008 |
| 10 | WI-0012 | Arca slice 10: rebuildable derived indexes, explicit migration workflow and canonical export (ARCA-MIG-001..003, ARCA-LOC-009) | WI-0010 |
| 11 | WI-0013 | Arca first release 0.1.0: nuget.org Trusted Publishing, NUGET_USER variable and echelon-registry system entry | WI-0009 |
| 12 | WI-0014 | (Deferred, possible future) per-application at-rest encryption for co-located data | - |

The backlog itself lives in `.ros/work/queue.json` and is managed only through the Praxis CLI. This table is a readable snapshot from when the slices were captured.
