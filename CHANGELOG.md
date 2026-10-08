# Changelog

Notable changes to Arca's packages, `EchelonFoundry.Arca.Core`,
`EchelonFoundry.Arca.GitHub` and (from 0.3.0) `EchelonFoundry.Arca.Limen`,
which share one version. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[semantic versioning](https://semver.org/spec/v2.0.0.html). In `0.x`, a minor
release may change the API; pin an exact version.

## [Unreleased]

### Added

- **`EchelonFoundry.Arca.Limen`**, a new opt-in package. It contains Arca's
  IndexedDB offline queue over Limen 0.8.0's `limen.store` (WI-0016;
  DF-LIMEN-2026-0005, DF-ARCA-2026-0010; Limen LCP-046, LCP-059, LCP-060,
  LCP-062, LCP-065, LCP-068, LCP-070, LCP-072, LCP-073). `Arca.Core` and
  `Arca.GitHub` take no Limen dependency.
  - `LimenQueue.own` and `LimenQueue.takeOver` take the namespace's Web Lock,
    the same `arca.queue/<app>[/<dataset>]` lock as the localStorage queue.
    They then obtain one store in the declared order (IndexedDB, then
    localStorage, then memory) and answer one of:
    - `Owned queue`, which carries the unchanged `QueueStore` and its
      `DurabilityMode`;
    - `OwnedElsewhere`;
    - `OwnershipUnsupported`;
    - `NothingUsable`.
  - Fencing: one IndexedDB record per namespace holds the snapshot and the
    fencing epoch. Every new owner raises the epoch, and every save is a
    `putIf` over what that owner last read or wrote. A pre-empted owner's
    save writes nothing.
  - `LocalQueueLost` is reported when the database is found recreated after
    the device held unsent changes. The evidence for this is kept in
    localStorage.
  - Persistence is asked for once, after the first offline write.
  - Sizes are checked against the pack's limits before anything is sent.
  - `QueueDiagnostics` reports the mode, the ownership, depth by state, the
    snapshot size against the budget, the last save, the last sync,
    whether storage is persisted, any notices, and the last failure. Every
    failure has a stable `arca.limen.*` code and an Aegis mapping.
  - `OwnedQueue.Discard` and `QueueSignOut` remove an account's discardable
    entries at sign-out, counted. In-flight entries are kept.
  - Tested against Limen's `FakeStore` and Limen 0.8.0's 32 shared store
    vectors (pinned by digest), and in real Chromium and WebKit tabs.
- The browser verification now runs every page in WebKit as well as
  Chromium (OQ-LIMEN-IDB-006), on Limen 0.8.0.

- **Queue-store conformance suite** (WI-0019; Limen LCP-046, LCP-060,
  LCP-075). `QueueStoreConformance` in `Arca.Core` is one executable
  contract for every `QueueStore`. It checks that an absent queue loads
  nothing; that a round-trip keeps order, sequences, states and policy,
  through the store and after a reopen; that a save replaces the whole
  snapshot; that an over-budget save is `QuotaExceeded` with nothing
  truncated; that another namespace's queue and unreadable text are
  `Corrupt`; that unavailable storage is `Unavailable`; and that a queue
  carrying a credential is refused. A fault the harness cannot arrange is
  reported `Unsupported`, never passed. `QueueStoreConformance.roundTrip`
  serves property tests. The in-memory and localStorage stores pass it.
- **`MemoryQueueStore`**: a `QueueStore` in memory. It encodes, budgets and
  checks the namespace like a durable store, so it serves tests and the
  memory-only durability mode.
- **Read-cache port** (WI-0021; Limen LCP-082..LCP-086, DF-LIMEN-2026-0005
  section 4), pure, in `Arca.Core`:
  - `CacheEntry`: keyed by `[account, namespace, partition]`. It carries the
    change token it reflects, every record's content hash, the record schema
    version and the read time. Only a `Fresh` provider read makes one
    (`ReadCache.entry`).
  - `Cached<'T>` and `Fresh<'T>` are distinct types. A cached value's token
    is a `CachedToken`, not a `ChangeToken`, so it cannot condition a write.
    A compile-failure fixture proves this (LCP-085).
  - `ReadCache.revalidate`: an equal token confirms (`Fresh` at the
    provider's token); a different one asks for a refresh and is shown
    stale; a removed partition is removed; an unreachable provider leaves
    the entry shown as of its token.
  - `ReadCache.load` validates the token and hashes. A failing entry is
    `Corrupt` and is dropped.
  - The `ReadCacheStore` port, the `MemoryReadCache` implementation, and a
    `ReadCacheConformance` suite of 13 cases.
  - `SharedDevicePolicy` (`Ask` | `DiscardOnSignOut`) and `SignOut.plan`.
    Under `Ask`, keeping the unsent changes keeps the account's cache
    (OQ-LIMEN-IDB-002); otherwise sign-out clears it. `Keep` is not offered
    under `DiscardOnSignOut`.

## [0.2.1] — 2026-10-08

A patch release for the offline queue across browser tabs. The `QueueStore`
port, the storage layout and every 0.2.0 API are unchanged; `own` is added.

### Fixed

- **Two tabs no longer lose offline-queue entries** (WI-0024,
  DF-ARCA-2026-0009). Tabs share localStorage, and the queue is saved as one
  snapshot, so a tab saving a stale snapshot overwrote an entry another tab
  had saved. `LocalStorageQueue.store` now fences every save on the stored
  text: it writes only over what it last loaded or saved. Otherwise it fails
  as `QueueStoreFailure.Unavailable`, with nothing written. The `QueueStore`
  port and the storage layout are unchanged, and a 0.2.0 queue is adopted in
  place.

### Added

- **`LocalStorageQueue.own`**: one owner per namespace through a Web Lock,
  with Limen LCP-059's semantics. It returns `Owned store`, `OwnedElsewhere`
  or `OwnershipUnsupported`. `QueueLockRequest` is shaped like Limen's
  coordination `acquire`, and `LocalStorageQueue.lockName` names the lock.
  `LocalStorageQueue.encodeWithin` and `mayReplace` expose the pure steps.
- Multi-tab tests: a deterministic interleaving, properties over arbitrary
  interleavings of tabs (with and without ownership), hand-off on close, and
  ownership moved mid-send (exactly one commit). A real-browser test runs two
  Chromium tabs through Limen's Storage effect and coordination pack.

## [0.2.0] — 2026-10-08

Backlog slices 7–10: conformance and an in-memory provider, integrity of
stored content, the offline change queue, and derived indexes, export and
migration.

### Added

- **Provider conformance suite** (`Conformance`, ARCA-TEST-001). Eighteen
  cases run through the provider-neutral interface against a fresh subject
  each. A harness arranges faults, and one that cannot is reported
  `Unsupported`, never `Passed`.
- **In-memory provider** (`InMemory`, `InMemoryStore`). A pure, deterministic
  implementation of the storage contract, with faults you can arrange
  (OutcomeUnknown landed or lost, rate limit, revoked credential, read-only,
  listing limit, size limit), for consumers' tests.
- **`Operation.requireChangeToken`.** An operation may also be conditioned on
  the provider's whole state; if the state has moved, the result is a typed
  `StaleChangeToken`.
- **`GitHubConfig.MaxObjectBytes` and `ListingLimit`.** The adapter now
  refuses an oversized write up front with `ObjectTooLarge`.

- **Real-browser verification** (`verification/browser`, ARCA-TEST-003).
  - A Limen engine runs Arca's GitHub adapter on the .NET WebAssembly
    runtime, with Limen 0.7.1's `BrowserKernel` executing every request
    through `fetch` in Chromium.
  - It commits a record and reads it back against a simulated GitHub,
    including a ref update whose answer is lost: Limen reports it as
    `OutcomeUnknown`, and Arca reconciles it as landed without resending.
  - CI runs it on every pull request (`browser-verification.yml`).

- **Integrity of stored content** (ARCA-INT-001..004).
  - `Integrity.validate` checks every record read: size, canonical envelope,
    the id and type its path names, and schema support. Each failure is a
    typed `IntegrityFailure`.
  - `Integrity.unchanged` and `immutableUnchanged` detect content-hash
    changes.
  - `Integrity.origin` and `externalEdits` tell Arca commits from edits made
    outside the application.
  - `StorageProvider.History` (both providers) lists the commits that
    touched an object, with their origin.
  - Both providers refuse, as `IntegrityRefused`, to update or delete a record
    whose stored state is not a valid record, and to change or delete an
    immutable record.
  - Three new conformance cases cover this.

- **Offline change queue** (`OfflineQueue`, `OfflineSync`, ARCA-OFF-001..006).
  - The queue is plain data: ordered entries with idempotency keys, expected
    revisions and states (pending, in flight, synchronized, conflicted,
    outcome unknown, refused, abandoned). An application can inspect it, and
    `OfflineQueue.status` never reports unsynchronized work as synchronized.
  - Offline writes are opt-in: under `OfflinePolicy.ReadOnlyWhenOffline`,
    `enqueue` refuses with `OfflineWritesDisabled`.
  - `OfflineSync.step` and `run` synchronize in strict order and write ahead:
    an entry is persisted as in flight before it is sent. After a restart an
    in-flight entry becomes `OutcomeUnknown` and is reconciled, never resent
    blindly. A conflicted or refused entry blocks the entries after it until
    the application revises (`revise`) or abandons (`abandon`) it.
  - The queue is persisted through a `QueueStore` port. Its first adapter,
    `Arca.GitHub.LocalStorageQueue`, describes Limen `Storage` effect requests
    as data. It enforces a size budget, so an over-budget queue fails whole
    with `QuotaExceeded` and is never truncated (DF-ARCA-2026-0005). A Limen
    IndexedDB adapter can replace it later (WI-0016).

- **Snapshots** (`Snapshot.take`). A snapshot is every object a namespace
  holds, read between two equal change tokens.
  - It excludes the application's dataset sub-namespaces.
  - A partial listing fails as `Incomplete`. If the namespace keeps changing,
    the result is `Unstable`.
- **Rebuildable derived indexes** (`Derived`, ARCA-MIG-001).
  - An `IndexDefinition` is a pure projection over validated records.
  - Each index records its source set (count, and a hash over each path and
    content hash) and is stored under `derived/indexes/`.
  - `check` returns Current, Stale, OtherVersion or Missing, and `compare`
    gives the differences between two indexes.
  - `rebuild` is idempotent and conditioned on the change token. It refuses
    to build on a record that does not validate.
- **Canonical export** (`Export`, ARCA-MIG-003). An export is a canonical JSON
  archive of everything a namespace stores, byte for byte, with each object's
  SHA-256. `decode` verifies every hash.
- **Migration workflow** (`Migration`, ARCA-MIG-002, ARCA-LOC-009;
  DF-ARCA-2026-0008).
  - It validates, copies, verifies and activates into a different location,
    with an optional record transform for schema migrations.
  - Progress is kept in the target manifest, so a run is resumable and
    idempotent. The source is untouched until `Migration.retire`, an explicit
    step conditioned on the source being unchanged.
  - Manifest phases gain `completed` and `retired`, and `ManifestProblem`
    gains `Retired`.
- **`Layout.keyOf`.** It returns the record key an authoritative path names.

### Changed

- **Reflection-free compilation.** Both packages compile with
  `--reflectionfree`, as Limen's engines do, so a trimmed browser WASM host
  keeps no F# printf. `LocationError.describe` and `JsonError.describe` give
  readable messages.

## [0.1.0] — 2026-10-08

The first release: the slices Chrona's storage needs (backlog slices 1–6).
It ships as Sigstore-attested GitHub release assets, which Conditor installs
into a local feed. nuget.org publication is dormant until `NUGET_USER` exists
(DF-ARCA-2026-0006).

### Arca.Core (pure: no I/O, clock or randomness)

- **Capability model.** Provider-neutral capabilities, with explicit refusal
  of anything a provider does not declare (ARCA-ARCH-003). The `StorageProvider`
  interface and typed `StorageFailure`.
- **Data location.**
  - The location (repository, branch, base path) is configured per deployment.
  - Each application owns its namespace, which cannot be escaped, and never
    assumes it is alone in the repository.
  - Dataset sub-namespaces may live in another repository.
  - The environment identity is part of every binding.
  - Production data in a public repository needs an explicit override.
  - Changing a location requires a migration (ARCA-LOC).
- **Records.**
  - Canonical JSON (format 1) with exact decimal numbers and content hashes.
  - A closed record envelope with a stable id inside the record.
  - Schema support decisions, deterministic partitioned layout, and
    application and dataset manifests (ARCA-REC).
- **Concurrency and commits.**
  - Conditional changes, typed conflicts, and a three-way merge over record
    sets.
  - The deterministic commit message with `Arca-*` trailers, and credential
    guards (ARCA-CON, ARCA-COMMIT).
- **Credentials.**
  - A redacting `AccessToken` and the `TokenProvider` port; Arca has no Fides
    dependency.
  - The non-secret identity and capability snapshot (ARCA-AUTH).

### Arca.GitHub (effects described as data; the host executes HTTP)

- **Conversations.** `Conversation<'a>` (Send, Wait, RequestToken, Done) can
  be driven by a Limen engine step by step, or by `Conversation.run`.
- **Identity.** `Identity.resolve` reads the identity, the repository and the
  branch rulesets.
- **`GitHubStorage`.**
  - `read`, `list`, `measure`, `changeToken`, `commit` and `reconcile`, and
    `provider` for async hosts.
  - One atomic commit per operation through one fast-forward ref update.
  - Expectations are checked at the exact head, and fast-forward races are
    resolved without lost updates.
  - An unknown outcome is reconciled through the candidate commit or the
    idempotency key.
  - ETag and immutable caching, rate-limit evidence, back-off with
    deterministic jitter, and typed size and partial-listing results (ARCA-API,
    ARCA-OUT).
- **Failures.** Operational failures are classified through Aegis's GitHub
  failure model (ARCA-ARCH-007).
