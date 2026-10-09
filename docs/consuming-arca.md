# Consuming Arca

How an application (Chrona first) takes Arca 0.4.1 and wires it into a Limen
engine. Distribution follows the short-term plan
([`distribution-short-term-plan.md`](distribution-short-term-plan.md)): the
packages are attested GitHub release assets, which Conditor installs into the
repository's local NuGet feed.

## 1. Install the packages through Conditor

You need Conditor with the NuGet release-asset feed (kemiller2002/conditor#59,
merged at `8dffc14`). Until a Conditor release carries it, build it from
`main`:

```bash
dotnet build src/Conditor.Cli -c Release -o <dir>
```

echelon-registry `main` at `616d10e` selects Arca 0.4.0 as an optional
project binding in echelon-current **1.16.0**, with limen-fsharp 0.9.0 (the
F# packages `EchelonFoundry.Arca.Limen` needs). 0.4.0 changes the API, so
read sections 3a, 3b and 5a/5b before moving from 0.3.0. In the consuming repository:

1. Declare Arca in `conditor.json`, at exactly the version the set selects:

   ```json
   { "id": "arca", "version": "0.4.1", "required": true },
   { "id": "limen-fsharp", "version": "0.9.0", "required": true }
   ```

2. Plan, review, and apply the exact plan:

   ```bash
   conditor upgrade --current --check --target . \
     --resolved-set <echelon-registry>/channels/echelon-current/linux-x64.json \
     --resolved-set-sha256 d6a26a57b1566ee9641d3d408b790d797845075ff683b52b8a574487e718841e
   conditor upgrade --current --target . --resolved-set ... --resolved-set-sha256 ... --authorize <plan digest>
   ```

   The plan lists `arca: opt in at 0.4.1` (or `arca: 0.4.0 -> 0.4.1`) under *NuGet release-asset feeds*.
   Applying it:
   - downloads both packages and proves them against the Registry digests;
   - writes `vendor/nuget/` with `arca.lock`;
   - maps `EchelonFoundry.Arca.Core` and `EchelonFoundry.Arca.GitHub` to that
     feed only, in `NuGet.config`;
   - verifies the repository, then commits the 1.12.0 authority and the lock.

   `conditor verify` proves the feed from then on.

3. Pin the packages (central package management shown):

   ```xml
   <PackageVersion Include="EchelonFoundry.Arca.Core" Version="0.4.1" />
   <PackageVersion Include="EchelonFoundry.Arca.GitHub" Version="0.4.1" />
   <!-- The IndexedDB queue and read cache (section 5a); also declare
        limen-fsharp 0.9.0 in conditor.json. -->
   <PackageVersion Include="EchelonFoundry.Arca.Limen" Version="0.4.1" />
   ```

   Reference `EchelonFoundry.Arca.Core` from the pure domain, and
   `EchelonFoundry.Arca.GitHub` from the engine that talks to storage. Both
   need `FSharp.Core` 10.1.400 or later, which Aegis 1.0.0 also needs.

Other platforms use their own channel file. The SHA-256 values at registry
`616d10e` (echelon-current 1.16.0) are:

| Platform | SHA-256 |
|---|---|
| linux-x64 | `ddcaad58026f370f99703515a5ac497ed1683b7d593efacead4bec3f244aad44` |
| linux-arm64 | `0abbb973c9c6fa61a4a0fbe42556871ca62d8bddca731ef1dccd3737fc7d9c37` |
| osx-x64 | `8352e3ea8e153167705003ab469d4c23acf62dd95c198f73e696ad3e7c5230ab` |
| osx-arm64 | `2122c95b8c1b99d7d191aaf58ede21253570504742f5751a062c6f58fde17821` |
| win-x64 | `be6d20a3fc373bac0729ad63a76efdeae24c80c630c17bb62fd9cfa8b7a809c2` |

echelon-current 1.16.0 also moves the Praxis selection to 3.10.0 (from
Arca's 3.7.2). A `conditor upgrade --current` plan shows that transition
beside Arca's; take it deliberately, or stay on the older set. The 1.12.0
linux-x64 set (`d6a26a57…`), for example, selects Arca 0.3.0.

To check provenance yourself:

```bash
gh attestation verify vendor/nuget/EchelonFoundry.Arca.Core.0.4.1.nupkg --repo kemiller2002/arca
```

## 2. Configure storage

```fsharp
open Arca
open Arca.GitHub

let location = DataLocation.create "acme" "time-data" "main" "deployments/prod"   // Result
let binding =
    { Application = AppId.create "chrona" |> Result.toOption |> Option.get
      Environment = { Kind = EnvironmentKind.Production; Name = "production" }
      Location = location |> Result.toOption |> Option.get }
let ns = Namespace.ofApplication binding                                           // Result<Namespace, LocationError>
let config = GitHubConfig.create binding.Location
```

Every part of the location comes from deployment configuration (ARCA-LOC-001).
Chrona owns `deployments/prod/chrona/` and nothing outside it. Use a separate
repository where Chrona's data needs its own permissions (DF-ARCA-2026-0002).

## 3. Records, operations and commits

- **Record.** Build a `Record` (stable `RecordId`, `RecordType` such as
  `chrona.activity`, schema version, mutability, a `Json` body) and encode it
  with `Record.encode Record.DefaultMaxBytes`.
- **Path.** `Layout.recordPath` gives its deterministic, partitioned path.
- **Operation.** Group changes into one `Operation`. `Change.Create` expects
  the record to be absent; `Change.Update` and `Change.Delete` expect the
  revision you last read. Add `OperationMetadata`: summary, actor kind and id,
  correlation id, and an **idempotency key**.
- **Commit.** `GitHubStorage.commit` lands the whole operation as one commit,
  or returns a typed `StorageFailure`:
  - `Conflicted` names each stale record and its current revision. Reload,
    rerun your domain validation, and decide.
  - `OutcomeUnknown` carries a `PendingReconciliation`. Call
    `GitHubStorage.reconcile` before trying again, and never resend blindly.
  - `Refused`, `RateLimited` (with GitHub's evidence) and `ProviderFailed`
    (with an Aegis fault code) cover the rest.
- **Whole-state conditions.** Besides each change's expected revision, an
  operation can be held to a token. In a repository other applications
  share, use `Operation.requireNamespaceToken` with the token from
  `provider.NamespaceState ns`: only a change inside your namespace makes it
  `StaleNamespaceToken`. `Operation.requireChangeToken` is the repository-wide
  fallback: any commit anywhere makes it `StaleChangeToken`. See section 3a.
- **Reads.** `GitHubStorage.read`, `list`, `changeToken` and `namespaceState`
  read by deterministic path.

### 3a. Namespace-scoped change tokens (from 0.4.0)

`provider.NamespaceState ns` returns a `NamespaceState`: the repository's
`ChangeToken` and the namespace's own `NamespaceToken`, observed at the same
commit (ARCA-CON-005, DF-ARCA-2026-0011). On GitHub the namespace token is
the Git tree SHA of the namespace root. A tree SHA is content-addressed, so a
commit by another application elsewhere in the repository leaves it
unchanged. Any change inside the namespace changes it, and that includes an
application namespace's `datasets/`.

```fsharp
let! state = provider.NamespaceState ns                                  // Result
// ... read and validate what the decision needs ...
let operation =
    Operation.create ns metadata changes                                  // Result
    |> Result.map (Operation.requireNamespaceToken state.NamespaceToken)
match! provider.Commit operation with
| Error(StorageFailure.StaleNamespaceToken(expected, actual)) -> // something in *your* namespace changed: reload, decide again
| Error(StorageFailure.Conflicted conflicts) -> // a record you touch changed
| other -> ...
```

- The commit is still one atomic commit on the branch head, with the same
  `OutcomeUnknown` and reconciliation semantics. The condition is checked at
  the exact head the commit is built on, and the ref update is
  fast-forward only.
- `NamespaceToken` and `ChangeToken` are different types, so neither can be
  passed where the other is meant.
- A queued operation keeps its namespace condition (the queue records
  `expectedNamespaceToken`, only when set). Do not downgrade below 0.4.0
  with such entries pending: an older Arca would read them without the
  condition.
- A provider without the `NamespaceToken` capability can only offer the
  repository-wide fallback: `requireChangeToken`, `Fresh.readRepositoryWide`
  and `ProviderObservation.CurrentRepository`. Both of Arca's providers
  offer it.

### 3b. Erasing immutable records for retention (from 0.4.0)

An immutable record is never changed or deleted by an ordinary write. When a
retention rule requires its content to go, erase it explicitly
(ARCA-INT-005, DF-ARCA-2026-0012):

```fsharp
// The record as last read and validated (Integrity.validate), so the
// erasure names exactly the content it removes.
let request = Erasure.request path validated DateTimeOffset.UtcNow "retention rule SIG-RET-30D"   // Result
let erasure = Erasure.operation ns metadata [ request ]                                          // Result, several records at once
match! Erasure.commit provider erasure with                // refused unless the provider declares Capability.Erase
| Ok receipt -> ...                                        // one atomic commit, with the usual trailers
| Error(StorageFailure.Conflicted _) -> ...                // the record changed since it was read
| Error(StorageFailure.IntegrityRefused(_, IntegrityRefusal.NotErasable reason)) -> ...
| Error(StorageFailure.OutcomeUnknown pending) -> ...      // reconcile, as for any commit
| Error other -> ...

match! provider.Read ns path with
| Ok(ReadOutcome.Erased erased) -> // erased.Tombstone: ErasedContentHash, ErasedRevision, ErasedAt, Reason
| ...
```

- The tombstone replaces the record at its own path, in the same commit. It
  records the erased content hash, the revision, the time and the reason,
  never the content. The commit's trailers record who erased it.
- Erasure is only for immutable records (`ErasureError.NotImmutable`). A
  mutable record is deleted with `Change.Delete`. The reason is one line of
  at most 200 characters, and must not look like a credential.
- An erased record is final. A create over it is `Conflicted`. An update,
  a delete or a second erasure is `IntegrityRefused(_, ErasedRecord)`.
- `Snapshot.take` lists tombstones in `Snapshot.Erased`, not `Objects`, so
  derived indexes never read them. `Export` and `Migration` carry them, so
  the record stays erased at a new location.
- An erasure is never queued offline (`QueueError.InvalidOperation`): send it
  online.
- **Devices.** Call `ReadCache.purgeErased cache.Store account ns [ path ]`
  so a device stops showing the record at once, rather than at its next
  revalidation.
- **What erasure does not do.** It removes the content from the **current
  tree** only. Git history, and every clone and fork, still holds it.
  Removing it permanently needs a history rewrite by the repository owner
  (for example with `git filter-repo`, followed by GitHub support to purge
  cached views). Arca never rewrites history. If a rule needs that guarantee,
  plan for that owner-run step, or keep such data out of Git.

## 4. Drive the adapter from a Limen engine

Each `GitHubStorage` operation is an `Op<'a>`. Apply it to the session to get a
`Conversation`, a value that says what to do next. Keep the session and the
continuation in engine state, and translate each step into a Limen effect:

| Conversation step | Limen effect | Answer with |
|---|---|---|
| `Send(authorized, next)` | `Http`: method, `Url`, `Headers` plus `AccessToken.authorization` when `Credential` is set, `Body`, `TimeoutMs`, `response: "text"`, `responseHeaders` = `Request.ResponseHeaders` | `HttpOutcome.Response(status, headers, body)`, `Failed`, `Cancelled`, or **`OutcomeUnknown`** exactly as the kernel reports it |
| `RequestToken next` | your token provider (Fides) | `Ok token` or a `TokenUnavailable` |
| `Wait(delay, next)` | a timer effect (the Limen `schedule` pack) | `()` |
| `Done(result, session)` | none | keep `session` for the next operation |

The adapter never touches the network, the clock or JavaScript interop itself,
so the engine stays inside Limen's boundary rules. Outside the browser,
`Conversation.run host` drives the same value with async functions, and
`GitHubStorage.provider host config` exposes Arca's provider-neutral
`StorageProvider`.

## 5. Offline writes

Offline writes are opt-in (ARCA-OFF-005). Create the queue with
`OfflineQueue.create OfflinePolicy.QueueWrites`. An application that must not
write offline uses `ReadOnlyWhenOffline` and gets a read-only degraded mode.

1. **Persistence.**
   - Take ownership with
     `LocalStorageQueue.own lock execute LocalStorageQueue.DefaultBudget ns`
     (0.2.1, DF-ARCA-2026-0009). Only one tab of the application at a time
     holds a namespace's queue:
     - `Owned store`: this tab loads, saves and synchronizes the queue.
     - `OwnedElsewhere`: another tab holds it. Tell the person, keep no
       queue here, and ask again later (for example on focus). The browser
       releases the lock when the owner closes.
     - `OwnershipUnsupported`: no Web Locks. Choose `store` (fenced, below),
       memory only, or no offline writes.
   - `lock` forwards `QueueLockRequest.Acquire name` to Limen's coordination
     pack as `acquire { name, mode: "exclusive", wait: false, steal: false }`,
     and answers `Acquired`, `Busy` or `Unsupported`. The host keeps the
     lock for the page's lifetime.
   - `execute` forwards each `LocalStorageRequest` (`Get`, `Set` or `Remove`)
     to Limen's `Storage` effect, and answers with `Success value` or
     `Failure Unavailable | QuotaExceeded`.
   - `LocalStorageQueue.store execute budget ns` is the store without the
     lock. Every save is fenced: it writes only over the text this store last
     loaded or saved. A save after another tab wrote fails as `Unavailable`,
     with nothing written. Without the lock, two saves whose read and write
     overlap across tabs are not excluded, so prefer `own`.
   - Each namespace's queue lives under `arca.queue.<app>[.<dataset>]`. The
     layout is the same as 0.2.0, so an existing queue is adopted in place.
2. **Start-up.** `Load` the queue, then apply `OfflineQueue.recover`. Any
   entry that was in flight becomes `OutcomeUnknown` and is reconciled.
3. **Writing offline.** Use `OfflineQueue.enqueue now operation queue`, then
   `Save`.
4. **Reconnecting.**
   - `OfflineSync.run provider store ns limit queue` sends entries in order,
     persisting each one before sending it.
   - The result is one of these:
     - `Idle`: all work is synchronized.
     - `Blocked entry`: an entry conflicted or was refused. Reload, revalidate,
       then `revise` or `abandon` it.
     - `Deferred reason`: try again later.
5. **Status.** Show `OfflineQueue.status queue`. Use `prune` to drop
   synchronized and abandoned entries.

The queue holds operations, not records. Reads always go to GitHub, which
stays authoritative (ARCA-OFF-006).

### 5a. The IndexedDB queue (`EchelonFoundry.Arca.Limen`, from 0.3.0)

`EchelonFoundry.Arca.Limen` keeps the queue in IndexedDB through Limen
0.8.0's store pack. It keeps the semantics `LocalStorageQueue.own` has today:

- one owner per namespace;
- the next tab takes over when the owner closes;
- nothing is sent twice;
- every save is fenced.

It hands you the same, unchanged `QueueStore` port. See DF-ARCA-2026-0010 and
DF-LIMEN-2026-0005.

**Install.** Declare `arca` 0.4.1 and `limen-fsharp` (0.9.0, which Arca 0.4.x is built against; 0.8.0 or later) in
`conditor.json`, then run `conditor upgrade --current`. Reference
`EchelonFoundry.Arca.Limen` from the engine, and pin `EchelonFoundry.Limen.Store`
and `EchelonFoundry.Limen.Contract` at the limen-fsharp version. In the host, register the store
pack with the application namespace,
`storeCapability({ namespace: "chrona" })`, next to the coordination pack.
Select `limen.store` version 2 (`Limen.Contract.Store.Contract`) in the
engine's handshake.

**Wire it.** The host supplies four executors, the only effects:

```fsharp
open Arca.Limen

let host: LimenHost =
    { Store = storeExecutor          // StoreRequest -> Async<StoreResult>: the limen.store pack
      Lock = coordinationExecutor    // CoordinationRequest -> Async<CoordinationResult>: the coordination pack
      LocalStorage = storageExecutor // LocalStorageRequest -> Async<LocalStorageOutcome>: Core's Storage effect
      Now = clock }                  // for diagnostics' times only

match! LimenQueue.own host QueueOptions.standard ns with
| QueueOpening.Owned queue ->
    // queue.Store is the QueueStore: Load, recover, OfflineSync as in section 5.
    // Show queue.Mode (IndexedDb | LocalStorage budget | MemoryOnly) wherever
    // sync state is shown; under MemoryOnly, warn before an offline write or
    // refuse offline writes (LCP-065).
    // Show queue.Notices, and (queue.Diagnostics()).Notices after each Load.
    ()
| QueueOpening.OwnedElsewhere ->
    // "Another tab holds this device's unsent changes." Offer "use this tab
    // instead": LimenQueue.takeOver host QueueOptions.standard ns.
    // While online this tab may write directly (OQ-LIMEN-IDB-001).
    ()
| QueueOpening.OwnershipUnsupported
| QueueOpening.NothingUsable _ -> () // no offline writes
```

What the composer does:

- **Store order.** It tries IndexedDB, then localStorage, then memory, and
  obtains exactly one of them.
- **Lock.** It takes the namespace's Web Lock first. The lock is the same
  `arca.queue/<app>` lock that `LocalStorageQueue.own` takes, so a tab on the
  old adapter and a tab on the new one exclude each other.
- **Takeover.** `takeOver` raises the fencing epoch. The old owner's next
  save writes nothing and fails as `Unavailable`, and its diagnostics show
  `OwnedElsewhere`.
- **In-flight entries.** The new owner reconciles whatever was in flight,
  so it is never sent twice.

**Diagnostics** (`queue.Diagnostics()`) is a value with these fields:

- `Mode`;
- `Ownership`;
- `Depth`, by state;
- `SnapshotSize` against `Budget`;
- `LastSave` and `LastSync`;
- `Persisted`;
- `Notices`;
- `LastFailure`, an `AdapterFailure` with a stable `arca.limen.*` code. Use
  `AdapterFailure.mapping` with Aegis;
- `Discarded`.

**Persistence.** The adapter asks for it after the first offline write and
never at open (OQ-LIMEN-IDB-004).

**`LocalQueueLost`.** This notice means the database was found recreated
after this device held unsent changes. Tell the person those changes are
gone.

**Sign-out** (`SharedDevicePolicy`, `SignOut.plan`). From 0.4.0, match
accounts by a **stable account id**, never a display name (ARCA-OFF-007).
Two people can share a display name, and matching by it can discard
another person's changes.

```fsharp
// When queuing: record who made the change.
let account = AccountId.ofIdentity snapshot.Identity          // GitHub numeric user id (CapabilitySnapshot)
//         or AccountId.ofActor stableActorId                  // an id the application keeps stable
let! queued = OfflineQueue.enqueueFor account now operation queue

// At sign-out:
let who = { Account = account; Legacy = None }                  // SignOutAccount
let unsent = QueueSignOut.unsentOfAccount who queue
// ... SignOut.plan policy unsent choice ...
let! discarded = owned.DiscardAccount who queue                  // Result<queue * count>
```

1. Count the account's unsent entries with `QueueSignOut.unsentOfAccount`.
2. Offer `SignOut.offered policy`.
3. Apply the plan:
   - to discard, call `queue.DiscardAccount who currentQueue`;
   - if `plan.ClearCache`, clear the account's read cache.

In-flight and outcome-unknown entries are never discarded: they may have
landed, so they are reconciled.

Entries queued before 0.4.0, or with `OfflineQueue.enqueue`, carry no
account id, and they never match. To clear them, set `Legacy = Some
identity` to the provider identity or actor they were recorded with. Do this
only while such entries may remain, and only if that value is unique to the
account. `queue.Discard account` and `QueueSignOut.unsentOf account` still
match by that display value. Keep them only for that case.

#### Moving Chrona from the localStorage queue (no entry is lost)

1. Upgrade to Arca 0.3.0 and Limen 0.8.0 (above). Keep `QueueStore`,
   `OfflineQueue` and `OfflineSync` code as it is.
2. At the composition root, replace
   `LocalStorageQueue.own lock execute budget ns` with
   `LimenQueue.own host QueueOptions.standard ns`. Map `Owned queue` to the
   old `Owned queue.Store`. `OwnedElsewhere` and `OwnershipUnsupported` keep
   their meaning.
3. `Load` as before. On the first load that owns the namespace with
   IndexedDB available, the adapter moves the localStorage snapshot
   (`arca.queue.chrona`) into IndexedDB exactly once:
   - **Copy:** a `putIf` of the decoded queue, with a SHA-256 marker of the
     localStorage text.
   - **Verify:** it reads the queue back and compares it.
   - **Retire:** it removes the localStorage key.

   The load returns the moved queue, and diagnostics carry
   `LegacyQueueAdopted n`.
4. If IndexedDB already holds entries, the move waits. Diagnostics carry
   `LegacyQueuePending n`. Once a sync drains the IndexedDB queue and you
   save it pruned, the next `Load` adopts the old queue whole. Two queues
   are never merged, and neither is dropped.
5. A corrupt or foreign localStorage queue is left untouched, and
   diagnostics carry `LegacyQueueUnreadable`.
6. The move survives any interruption: a reload, a crash, a lost lock, or a
   quota failure at the commit.
   - Before the commit, nothing changed, and the next load copies again.
   - After the commit, the marker matches, and the next load only removes
     the localStorage key.
7. Keep showing the durability mode. If IndexedDB is unusable in a browser,
   the composer stays on `LocalStorage` with the same key, so nothing moves.

### 5b. The offline-start read cache (`EchelonFoundry.Arca.Limen`, from 0.3.0)

The read cache keeps the partitions an application has read and validated
from the provider, so the app opens and shows data without GitHub. Example
partitions are activities by month, a derived index, reference data and
the roster. It never holds unsent changes, and it is never the basis of a
write. See Limen LCP-082..LCP-087 and DF-LIMEN-2026-0005 §4.

```fsharp
let! cache = IndexedDbReadCache.openCache host IndexedDbReadCache.DefaultBudget   // Result
let! queue = LimenQueue.own host { QueueOptions.standard with FreeSpace = Some cache.FreeSpace } ns

// After a provider read that the application validated (ARCA-INT-001), with
// `state` from provider.NamespaceState ns, taken before the read:
let key = ReadCache.key account ns "activities/2026-10"                             // Result
let entry = ReadCache.entry key schemaVersion now (Fresh.read state storedObjects)  // namespace-scoped
do! cache.Keep entry                                                                // never fails the read

// Opening offline: show it "as of" its token and read time.
match! cache.Show key with
| Ok(Some cached) -> // Cached.value, Cached.asOf (CachedToken.text), Cached.readAt
| Ok None -> ()      // read from the provider
| Error _ -> ()      // a corrupt entry was dropped; read from the provider

// Online: revalidate each shown partition against the namespace's state.
let! state = provider.NamespaceState ns                                             // Result
match ReadCache.revalidate (ProviderObservation.Current state) cached with
| Revalidation.Confirmed fresh -> // current; Fresh.namespaceToken fresh (or Fresh.token) may condition a write
| Revalidation.Refresh stale -> // shown stale; re-read, then cache.Keep the new entry
| Revalidation.Remove key -> // cache.Store.Remove key
| Revalidation.Unverified cached -> () // provider unreachable: still "as of"
```

- A `Cached` value's token is a `CachedToken`, not a `ChangeToken`, so it
  does not compile as a write condition. Write decisions use `Fresh` reads,
  or are queued and reconciled.
- **Token scope (from 0.4.0, ARCA-CON-005).** An entry made from
  `Fresh.read state` is namespace-scoped: revalidation compares it with the
  namespace token, so another application's commit leaves it `Confirmed`.
  Equal namespace tokens mean the namespace's content is the same at the
  current repository token, so the confirmed `Fresh` value carries both
  current tokens. The repository-wide behaviour is kept only as an explicit
  fallback: `Fresh.readRepositoryWide token` and
  `ProviderObservation.CurrentRepository token`. A namespace-scoped entry is
  never confirmed by a repository token alone. An entry written by 0.3.x
  has no scope and is read as repository-wide; its first refresh replaces
  it with a namespace-scoped one.
- **Moving Chrona from 0.3.0.** The 0.3.0 calls no longer compile, by
  design. Replace `provider.ChangeToken ns` with `provider.NamespaceState
  ns`, `Fresh.read token` with `Fresh.read state`, and
  `ProviderObservation.Current token` with `ProviderObservation.Current
  state`. Hold writes with `Operation.requireNamespaceToken
  state.NamespaceToken` instead of `requireChangeToken`, and handle
  `StorageFailure.StaleNamespaceToken` where you handle `StaleChangeToken`.
- **Sign-out.** Apply `SignOut.plan policy unsent choice`. When
  `ClearCache` is set, call `cache.Store.Clear(CacheScope.Account account)`.
  It is one `deleteRange`. Under `Ask`, when the person keeps their unsent
  changes, the cache is kept too (OQ-LIMEN-IDB-002).
- **Clear this device.** `LimenDevice.clear host` deletes both databases. It
  answers `Blocked` while another tab holds a connection.
- **Diagnostics.** `cache.Diagnostics()` reports the size against the
  budget, the evictions and the last failure.

## 6. Indexes, export and migration

- **Derived indexes.**
  - Define an index as an `IndexDefinition`: a name, a version, the record
    types it reads, and a pure projection.
  - `Derived.rebuild provider ns metadata definition` writes it under
    `derived/indexes/` only when it is out of date.
  - `Derived.check` tells you whether a stored index still matches the
    records.
- **Export.**
  - `Export.take provider ns 3 |> Async.map (Result.map Export.encode)` gives
    a canonical, hash-verified archive of everything the namespace stores.
  - Keep it as a backup. `Export.decode` verifies it before use.
- **Moving data, or changing record schemas.**
  - Never edit the configured location (ARCA-LOC-009). Build a
    `MigrationPlan` (source and target namespaces, transform, batch size).
  - Run `Migration.run sourceProvider targetProvider plan` until it returns
    `Ok`, then switch the application's configured location to the target.
  - Later, call `Migration.retire`. Put the application into read-only mode
    while it migrates.

## 7. What comes next

- **Follow-ups.** Forwarding writes between tabs is a later item if real use
  needs it (OQ-LIMEN-IDB-001). Namespace-scoped change tokens (WI-0018) are
  in 0.4.0 (section 3a).
- **Moving to nuget.org.** When the packages are on nuget.org, remove the
  `EchelonFoundry.Arca.*` mapping from `NuGet.config`. The package ids and
  versions do not change.
