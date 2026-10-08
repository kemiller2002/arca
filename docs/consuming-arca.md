# Consuming Arca

How an application (Chrona first) takes Arca 0.1.0 and wires it into a Limen
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

echelon-registry `main` at `385949f` selects Arca as an optional project
binding in echelon-current **1.4.0**. In the consuming repository:

1. Declare Arca in `conditor.json`, at exactly the version the set selects:

   ```json
   { "id": "arca", "version": "0.1.0", "required": true }
   ```

2. Plan, review, and apply the exact plan:

   ```bash
   conditor upgrade --current --check --target . \
     --resolved-set <echelon-registry>/channels/echelon-current/linux-x64.json \
     --resolved-set-sha256 83dce711f2f656abfdca3f935e43b82306739c45d928930690b4e38bd9d54ca0
   conditor upgrade --current --target . --resolved-set ... --resolved-set-sha256 ... --authorize <plan digest>
   ```

   The plan lists `arca: opt in at 0.1.0` under *NuGet release-asset feeds*.
   Applying it:
   - downloads both packages and proves them against the Registry digests;
   - writes `vendor/nuget/` with `arca.lock`;
   - maps `EchelonFoundry.Arca.Core` and `EchelonFoundry.Arca.GitHub` to that
     feed only, in `NuGet.config`;
   - verifies the repository, then commits the 1.4.0 authority and the lock.

   `conditor verify` proves the feed from then on.

3. Pin the packages (central package management shown):

   ```xml
   <PackageVersion Include="EchelonFoundry.Arca.Core" Version="0.1.0" />
   <PackageVersion Include="EchelonFoundry.Arca.GitHub" Version="0.1.0" />
   ```

   Reference `EchelonFoundry.Arca.Core` from the pure domain, and
   `EchelonFoundry.Arca.GitHub` from the engine that talks to storage. Both
   need `FSharp.Core` 10.1.400 or later, which Aegis 1.0.0 also needs.

Other platforms use their own channel file. The SHA-256 values at registry
`385949f` are:

| Platform | SHA-256 |
|---|---|
| linux-arm64 | `4156615039897ed35747af36810034f024d3bee76596ab5fa3ee47ab13153a23` |
| osx-x64 | `44d6835cc7409f66a4ea610212b771da09d8107f10273c86e1421136a2a16d02` |
| osx-arm64 | `c89a65aa271912749810a80bd778f5cab8140388cba245268b8a0ccdf229d1b5` |
| win-x64 | `9613ff07564977524d75cd66ca58bab3f7a795d1025f9891555f69f89b03d743` |

To check provenance yourself:

```bash
gh attestation verify vendor/nuget/EchelonFoundry.Arca.Core.0.1.0.nupkg --repo kemiller2002/arca
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
- **Reads.** `GitHubStorage.read`, `list` and `changeToken` read by
  deterministic path.

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

## 5. Pending work and what to expect

- **Slices 7 to 10.** The conformance suite with an in-memory provider for your
  tests, integrity checks on read, the offline queue with its localStorage
  store, and migration and export follow in later releases (`./praxis work ready`).
- **Moving to nuget.org.** When the packages are on nuget.org, remove the
  `EchelonFoundry.Arca.*` mapping from `NuGet.config`. The package ids and
  versions do not change.
