# Changelog

Notable changes to Arca's packages, `EchelonFoundry.Arca.Core` and
`EchelonFoundry.Arca.GitHub`, which share one version. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[semantic versioning](https://semver.org/spec/v2.0.0.html). In `0.x`, a minor
release may change the API; pin an exact version.

## [Unreleased]

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
