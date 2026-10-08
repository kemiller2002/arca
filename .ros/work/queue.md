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
| WI-0008 | Arca slice 6: GitHub adapter - atomic commits, conditional requests, rate limits, OutcomeUnknown and idempotency (ARCA-API-001..005, ARCA-OUT-001..002, ARCA-COMMIT-001/006) | complete | arca, slice:6, minimal, github-adapter | high |
| WI-0009 | Arca slice 7: provider conformance suite, in-memory provider and real-browser WASM verification (ARCA-TEST-001..004) | complete | arca, slice:7, conformance, testing | high |
| WI-0010 | Arca slice 8: storage content is untrusted input - validation, manual-edit and tamper detection (ARCA-INT-001..004) | complete | arca, slice:8, integrity | high |
| WI-0011 | Arca slice 9: offline change queue as data with durable browser persistence through Limen (ARCA-OFF-001..006) | complete | arca, slice:9, offline | high |
| WI-0012 | Arca slice 10: rebuildable derived indexes, explicit migration workflow and canonical export (ARCA-MIG-001..003, ARCA-LOC-009) | complete | arca, slice:10, derived-state, migration | medium |
| WI-0013 | Arca first release: interim distribution as Sigstore-attested GitHub release assets, echelon-registry entry, Conditor local-feed install; nuget.org Trusted Publishing kept dormant until NUGET_USER exists | complete | arca, release, registry | high |
| WI-0014 | (Deferred, possible future) per-application at-rest encryption for co-located data | captured | arca, deferred, future, encryption | low |
| WI-0015 | Align Arca to echelon-current 1.3.0 (conditor upgrade --current) and regenerate the test project as a real xUnit dotnet test project (conditor#54) | complete | arca, conditor, release-set, testing | high |
| WI-0016 | Limen IndexedDB adapter for Arca's offline queue-store port (ARCA-OFF-002) in a new EchelonFoundry.Arca.Limen package, replacing the interim localStorage adapter (Limen LCP-046, LCP-059, LCP-060, LCP-062, LCP-065) | captured | arca, offline, limen, indexeddb, follow-up | medium |
| WI-0017 | Release Arca 0.2.0 (slices 7-10) as attested GitHub release assets and register it in echelon-registry | complete |  | medium |
| WI-0018 | Namespace-scoped change tokens: condition a commit on the application's own namespace, not the whole repository | captured |  | medium |
| WI-0019 | Queue-store port conformance suite: one executable contract that the localStorage, in-memory and (later) IndexedDB QueueStore implementations all pass (ARCA-OFF-002; Limen LCP-046, LCP-060, LCP-075) | captured | arca, offline, limen, indexeddb | medium |
| WI-0020 | One-time move of the localStorage queue into IndexedDB: idempotent, drain-then-adopt, safe at every interruption point (ARCA-OFF-002, ARCA-MIG-002; Limen LCP-066, LCP-067) | captured | arca, offline, limen, indexeddb | medium |
| WI-0021 | Read-cache port in Arca.Core: Cached and Fresh values, token-stamped partitions, freshness rules, an in-memory implementation and a read-cache conformance suite (Limen LCP-082..LCP-086) | captured | arca, offline, limen, indexeddb | medium |
| WI-0022 | IndexedDB read cache in EchelonFoundry.Arca.Limen: compound-keyed partitions, online revalidation, policy-driven clearing, budget and least-recently-used eviction (Limen LCP-082..LCP-087) | captured | arca, offline, limen, indexeddb | medium |
| WI-0023 | Cross-reference Limen's IndexedDB requirements (LCP-043..087, DF-LIMEN-2026-0005) from Arca's requirements and backlog plan; refine WI-0016 and capture WI-0019..WI-0022 | complete | arca, limen, indexeddb | medium |
| WI-0024 | Fix multi-tab loss in the interim localStorage offline queue: two tabs saving one whole snapshot lose entries (last save wins); single owner per namespace behind the unchanged QueueStore port (Limen LCP-059, LCP-060, DF-LIMEN-2026-0005) | complete | arca, offline, bug, data-loss | high |
| WI-0025 | Release Arca 0.2.1 (multi-tab localStorage queue fix) as attested GitHub release assets and register it in echelon-registry | captured | arca, release, registry | high |
