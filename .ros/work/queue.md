# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| ROS-INSTALL-3-7-2 | ROS-INSTALL-3-7-2 | complete |  |  |
| WI-0001 | Bootstrap Arca with Conditor: Praxis 3.7.2, Ordo 1.5.0, Communication Engineering 1.0.0 from echelon-current 1.2.0, plus the fsharp-nuget-library scaffold (foundations, build-and-test, release workflow) | complete | bootstrap, conditor | high |
| WI-0002 | Derive Arca's storage requirements from Signal, Chrona and Summa, record the decisions, re-anchor the charter, and capture the dependency-ordered backlog | complete | planning, requirements | high |
| WI-0003 | Arca slice 1: package layout - pure core and GitHub adapter packages, WASM-safe, Aegis-bound (ARCA-ARCH-001..007) | complete | arca, slice:1, minimal, architecture | high |
| WI-0004 | Arca slice 2: per-deployment data location, app-owned namespaces and path safety (ARCA-LOC-001..010) | complete | arca, slice:2, minimal, data-location | high |
| WI-0005 | Arca slice 3: canonical JSON record format, stable ids, manifests and schema versioning (ARCA-REC-001..007) | complete | arca, slice:3, minimal, record-format | high |
| WI-0006 | Arca slice 4: optimistic concurrency, conflict detection, three-way merge and commit/audit format (ARCA-CON-001..004, ARCA-COMMIT-001..006) | complete | arca, slice:4, minimal, concurrency, commit | high |
| WI-0007 | Arca slice 5: token-provider port, identity resolution and capability snapshot (ARCA-AUTH-001..005) | complete | arca, slice:5, minimal, auth | high |
| WI-0008 | Arca slice 6: GitHub adapter - atomic commits, conditional requests, rate limits, OutcomeUnknown and idempotency (ARCA-API-001..005, ARCA-OUT-001..002, ARCA-COMMIT-001/006) | ready | arca, slice:6, minimal, github-adapter | high |
| WI-0009 | Arca slice 7: provider conformance suite, in-memory provider and real-browser WASM verification (ARCA-TEST-001..004) | captured | arca, slice:7, conformance, testing | high |
| WI-0010 | Arca slice 8: storage content is untrusted input - validation, manual-edit and tamper detection (ARCA-INT-001..004) | captured | arca, slice:8, integrity | high |
| WI-0011 | Arca slice 9: offline change queue as data with durable browser persistence through Limen (ARCA-OFF-001..006) | captured | arca, slice:9, offline | high |
| WI-0012 | Arca slice 10: rebuildable derived indexes, explicit migration workflow and canonical export (ARCA-MIG-001..003, ARCA-LOC-009) | captured | arca, slice:10, derived-state, migration | medium |
| WI-0013 | Arca first release: interim distribution as Sigstore-attested GitHub release assets, echelon-registry entry, Conditor local-feed install; nuget.org Trusted Publishing kept dormant until NUGET_USER exists | captured | arca, release, registry | high |
| WI-0014 | (Deferred, possible future) per-application at-rest encryption for co-located data | captured | arca, deferred, future, encryption | low |
| WI-0015 | Align Arca to echelon-current 1.3.0 (conditor upgrade --current) and regenerate the test project as a real xUnit dotnet test project (conditor#54) | complete | arca, conditor, release-set, testing | high |
| WI-0016 | Limen IndexedDB adapter for Arca's offline queue-store port (ARCA-OFF-002), replacing the interim localStorage adapter | captured | arca, offline, limen, indexeddb, follow-up | medium |
