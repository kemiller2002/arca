namespace Arca

open System
open System.Globalization

// ---------------------------------------------------------------------------
// The offline-start read cache (WI-0021; Limen LCP-082..LCP-086,
// DF-LIMEN-2026-0005 section 4). Pure: entries are data, freshness rules are
// total functions, and time is an input. The cache holds only what was read
// and validated from the provider, never unsent changes (ARCA-OFF-006).
// ---------------------------------------------------------------------------

/// Where one cache entry belongs: the account it was read with, the Arca
/// namespace and the partition (an activity month, the reference folder, the
/// roster, a derived index). Stored under the compound key
/// `[account, namespace, partition]` (LCP-083).
type CacheKey =
    { Account: string
      Namespace: string
      Partition: string }

/// One record as it was read from the provider.
type CachedRecord =
    { Path: string
      Content: string
      /// `sha256:` of the content's UTF-8 bytes, checked on every load.
      ContentHash: string }

/// One cached partition: what it reflects and what it holds (LCP-083).
type CacheEntry =
    { Key: CacheKey
      /// The provider's change token the read reflects (for a derived index,
      /// the source token it was built from, ARCA-MIG-001).
      ChangeToken: string
      /// The record schema version of the partition's records.
      SchemaVersion: int
      /// When the partition was read from the provider (an input).
      ReadAt: DateTimeOffset
      /// When the entry was last shown (an input), for eviction (LCP-087).
      LastUsedAt: DateTimeOffset
      Records: CachedRecord list }

/// The change token a cached entry reflects. It is shown ("as of"), and
/// compared by revalidation, but it is not a `ChangeToken`: no function that
/// builds or conditions a write accepts it (LCP-085).
type CachedToken = private CachedToken of string

/// A value from the read cache. It may be out of date: it is never the basis
/// of a write decision (LCP-085).
type Cached<'T> =
    private
        { CachedValue: 'T
          CachedKey: CacheKey
          AsOf: CachedToken
          CachedReadAt: DateTimeOffset
          IsStale: bool }

/// A value read from the provider, with the change token it was read at. A
/// write's expected base and change token come only from these.
type Fresh<'T> =
    private
        { FreshValue: 'T
          FreshToken: ChangeToken }

/// What the provider says about a cached partition when it is reachable.
[<RequireQualifiedAccess>]
type ProviderObservation =
    /// The provider's current change token for the partition's namespace.
    | Current of ChangeToken
    /// The provider no longer has the partition.
    | PartitionGone
    /// The provider could not be reached.
    | Unreachable

/// What revalidation decided for one cached partition (LCP-084).
[<RequireQualifiedAccess>]
type Revalidation<'T> =
    /// The token is equal: the cached value is current.
    | Confirmed of Fresh<'T>
    /// The token differs: refresh the partition from the provider, then
    /// replace the entry. Until then it is shown stale, never current.
    | Refresh of Cached<'T>
    /// The provider no longer has the partition: remove the entry.
    | Remove of CacheKey
    /// The provider is unreachable: the entry stays, shown as of its token.
    | Unverified of Cached<'T>

/// Why the cache refused a request.
[<RequireQualifiedAccess>]
type ReadCacheFailure =
    /// No usable storage, or the storage stopped answering.
    | Unavailable
    /// The entry does not fit the storage quota or the cache budget.
    | QuotaExceeded
    /// Another tab holds a connection, so the clear cannot run now.
    | Blocked
    /// The stored entry is not one this Arca wrote, or failed validation.
    | Corrupt of reason: string

/// What a clear covers.
[<RequireQualifiedAccess>]
type CacheScope =
    /// One account's entries, in every namespace (sign-out, LCP-086).
    | Account of account: string
    /// One account's entries in one namespace.
    | AccountNamespace of account: string * ns: string
    /// Every entry ("clear this device").
    | Everything

/// The read-cache port. An in-memory implementation is `MemoryReadCache`;
/// the IndexedDB one is in `EchelonFoundry.Arca.Limen`.
[<NoEquality; NoComparison>]
type ReadCacheStore =
    { Load: CacheKey -> Async<Result<CacheEntry option, ReadCacheFailure>>
      /// Writes a whole partition, replacing what was there.
      Save: CacheEntry -> Async<Result<unit, ReadCacheFailure>>
      Remove: CacheKey -> Async<Result<unit, ReadCacheFailure>>
      /// The keys held for an account in a namespace, in partition order.
      Partitions: string -> string -> Async<Result<CacheKey list, ReadCacheFailure>>
      Clear: CacheScope -> Async<Result<unit, ReadCacheFailure>> }

/// How an application treats a shared device at sign-out (LCP-070). The
/// application declares it; Arca never chooses one.
[<RequireQualifiedAccess>]
type SharedDevicePolicy =
    /// Offer to send, keep or discard unsent changes (the default).
    | Ask
    /// Offer to send or discard; nothing is kept for the account.
    | DiscardOnSignOut

/// What the person chose at sign-out for their unsent changes.
[<RequireQualifiedAccess>]
type SignOutChoice =
    /// Send them now, while the provider is reachable.
    | SendNow
    /// Keep them on this device for this account (only under `Ask`).
    | Keep
    /// Discard them, after a confirmation that names how many.
    | Discard

/// What a sign-out does to the account's local data (LCP-070, LCP-086).
type SignOutPlan =
    { /// Clear the account's read cache.
      ClearCache: bool
      /// Send the unsent changes before signing out.
      SendUnsent: bool
      /// Discard the unsent changes (counted, recorded in diagnostics).
      DiscardUnsent: int }

/// Why a sign-out cannot be planned yet.
[<RequireQualifiedAccess>]
type SignOutRefusal =
    /// Unsent changes exist; the person must choose among `offered`.
    | ChoiceRequired of unsent: int * offered: SignOutChoice list
    /// The choice is not offered under the policy.
    | NotOffered of SignOutChoice

[<RequireQualifiedAccess>]
module CachedToken =
    /// The token's text, to show ("as of"). Never pass it to a write.
    let text (CachedToken token) = token

[<RequireQualifiedAccess>]
module Cached =
    let value (cached: Cached<'T>) = cached.CachedValue
    let key (cached: Cached<'T>) = cached.CachedKey
    let asOf (cached: Cached<'T>) = cached.AsOf
    let readAt (cached: Cached<'T>) = cached.CachedReadAt

    /// True once revalidation found the provider moved on, or a refresh
    /// failed: it is shown stale, never current.
    let isStale (cached: Cached<'T>) = cached.IsStale

    let map (f: 'T -> 'U) (cached: Cached<'T>) : Cached<'U> =
        { CachedValue = f cached.CachedValue
          CachedKey = cached.CachedKey
          AsOf = cached.AsOf
          CachedReadAt = cached.CachedReadAt
          IsStale = cached.IsStale }

    /// Marks it stale: a refresh failed.
    let stale (cached: Cached<'T>) = { cached with IsStale = true }

[<RequireQualifiedAccess>]
module Fresh =
    /// A value the application read from the provider at `token`. This is the
    /// only way to make one; no cache operation does (LCP-085).
    let read (token: ChangeToken) (value: 'T) = { FreshValue = value; FreshToken = token }

    let value (fresh: Fresh<'T>) = fresh.FreshValue

    /// The token to condition a write on.
    let token (fresh: Fresh<'T>) = fresh.FreshToken

    let map (f: 'T -> 'U) (fresh: Fresh<'T>) : Fresh<'U> =
        { FreshValue = f fresh.FreshValue
          FreshToken = fresh.FreshToken }

[<RequireQualifiedAccess>]
module SignOut =

    /// The choices offered for unsent changes under a policy.
    let offered (policy: SharedDevicePolicy) =
        match policy with
        | SharedDevicePolicy.Ask -> [ SignOutChoice.SendNow; SignOutChoice.Keep; SignOutChoice.Discard ]
        | SharedDevicePolicy.DiscardOnSignOut -> [ SignOutChoice.SendNow; SignOutChoice.Discard ]

    /// What signing out does, given how many unsent changes the account has
    /// and what the person chose. The cache is cleared, except under `Ask`
    /// when the person keeps their unsent changes: then it is kept with them,
    /// so the account opens offline and sees them in context
    /// (OQ-LIMEN-IDB-002). Nothing is discarded or kept without a choice.
    let plan (policy: SharedDevicePolicy) (unsent: int) (choice: SignOutChoice option) =
        match unsent, choice with
        | n, _ when n <= 0 -> Ok { ClearCache = true; SendUnsent = false; DiscardUnsent = 0 }
        | n, None -> Error(SignOutRefusal.ChoiceRequired(n, offered policy))
        | _, Some chosen when not (offered policy |> List.contains chosen) -> Error(SignOutRefusal.NotOffered chosen)
        | _, Some SignOutChoice.Keep -> Ok { ClearCache = false; SendUnsent = false; DiscardUnsent = 0 }
        | _, Some SignOutChoice.SendNow -> Ok { ClearCache = true; SendUnsent = true; DiscardUnsent = 0 }
        | n, Some SignOutChoice.Discard -> Ok { ClearCache = true; SendUnsent = false; DiscardUnsent = n }

/// The read cache's rules as total functions (LCP-082..LCP-086).
[<RequireQualifiedAccess>]
module ReadCache =

    /// The cache entry format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    /// `sha256:` of a record's content, as cached entries carry it.
    let hash (content: string) = Export.hash content

    /// The namespace's identity in cache keys: the application, dataset and
    /// location, so two deployments never share entries.
    let namespaceId (ns: Namespace) =
        let app =
            match ns.Dataset with
            | None -> AppId.value ns.Application
            | Some dataset -> $"{AppId.value ns.Application}.{DatasetId.value dataset}"

        let location = ns.Location
        $"{app}@{location.Repository.Owner}/{location.Repository.Name}/{BranchName.value location.Branch}/{RelativePath.render location.BasePath}"

    /// A cache key, or why not: every part is non-empty and none looks like a
    /// credential (the account is an identity, never a token, LCP-068).
    let key (account: string) (ns: Namespace) (partition: string) =
        if String.IsNullOrWhiteSpace account then Error(ReadCacheFailure.Corrupt "the account is empty")
        elif String.IsNullOrWhiteSpace partition then Error(ReadCacheFailure.Corrupt "the partition is empty")
        elif Secrets.looksLikeCredential account || Secrets.looksLikeCredential partition then
            Error(ReadCacheFailure.Corrupt "a cache key may not carry a credential")
        else
            Ok
                { Account = account
                  Namespace = namespaceId ns
                  Partition = partition }

    /// The entry for a partition the application just read and validated from
    /// the provider (ARCA-INT-001). Only a `Fresh` read makes an entry, so the
    /// cache never holds unsent changes or anything derived from them.
    let entry (key: CacheKey) (schemaVersion: int) (readAt: DateTimeOffset) (read: Fresh<StoredObject list>) =
        let (ChangeToken token) = Fresh.token read

        { Key = key
          ChangeToken = token
          SchemaVersion = schemaVersion
          ReadAt = readAt
          LastUsedAt = readAt
          Records =
            Fresh.value read
            |> List.map (fun stored ->
                { Path = RelativePath.render stored.Path
                  Content = stored.Content
                  ContentHash = hash stored.Content }) }

    /// Checks a stored entry before it is used: it carries a token and every
    /// record's content matches its hash (LCP-083). A failing entry is
    /// Corrupt: drop it and read again, nothing is lost.
    let validate (entry: CacheEntry) =
        if String.IsNullOrWhiteSpace entry.ChangeToken then
            Error(ReadCacheFailure.Corrupt "the entry carries no change token")
        else
            match entry.Records |> List.tryFind (fun record -> hash record.Content <> record.ContentHash) with
            | Some record -> Error(ReadCacheFailure.Corrupt $"{record.Path} does not match its content hash")
            | None -> Ok entry

    /// A validated entry as a cached value, shown as of its token.
    let cached (entry: CacheEntry) : Result<Cached<CacheEntry>, ReadCacheFailure> =
        validate entry
        |> Result.map (fun entry ->
            { CachedValue = entry
              CachedKey = entry.Key
              AsOf = CachedToken entry.ChangeToken
              CachedReadAt = entry.ReadAt
              IsStale = false })

    /// The revalidation rule (LCP-084): an equal token confirms and makes the
    /// value fresh at the provider's token; a different one asks for a
    /// refresh and marks it stale; a removed partition is removed; an
    /// unreachable provider leaves it shown as of its token.
    let revalidate (observation: ProviderObservation) (cached: Cached<'T>) : Revalidation<'T> =
        match observation with
        | ProviderObservation.Current(ChangeToken current as token) when current = CachedToken.text cached.AsOf ->
            Revalidation.Confirmed(Fresh.read token cached.CachedValue)
        | ProviderObservation.Current _ -> Revalidation.Refresh(Cached.stale cached)
        | ProviderObservation.PartitionGone -> Revalidation.Remove cached.CachedKey
        | ProviderObservation.Unreachable -> Revalidation.Unverified cached

    /// The entry marked as shown at `at`, for least-recently-used eviction.
    let touch (at: DateTimeOffset) (entry: CacheEntry) = { entry with LastUsedAt = at }

    /// Whether a key falls inside a clear's scope.
    let inScope (scope: CacheScope) (key: CacheKey) =
        match scope with
        | CacheScope.Everything -> true
        | CacheScope.Account account -> key.Account = account
        | CacheScope.AccountNamespace(account, ns) -> key.Account = account && key.Namespace = ns

    /// Loads, validates and wraps an entry. A corrupt entry is removed and
    /// reported, so the next read rebuilds it from the provider.
    let load (store: ReadCacheStore) (key: CacheKey) =
        async {
            match! store.Load key with
            | Error failure -> return Error failure
            | Ok None -> return Ok None
            | Ok(Some entry) when entry.Key <> key ->
                let! _ = store.Remove key
                return Error(ReadCacheFailure.Corrupt "the stored entry belongs to another key")
            | Ok(Some entry) ->
                match cached entry with
                | Ok value -> return Ok(Some value)
                | Error failure ->
                    let! _ = store.Remove key
                    return Error failure
        }

    // -----------------------------------------------------------------------
    // Persistence format: canonical JSON, versioned, credential-free.
    // -----------------------------------------------------------------------

    let private timestamp (at: DateTimeOffset) =
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    /// The entry as canonical text to persist. An entry that would carry
    /// anything that looks like a credential is refused (LCP-068).
    let encode (entry: CacheEntry) =
        let value =
            Json.objectOf
                [ "arcaCache", Json.Number(decimal Format)
                  "account", Json.String entry.Key.Account
                  "namespace", Json.String entry.Key.Namespace
                  "partition", Json.String entry.Key.Partition
                  "changeToken", Json.String entry.ChangeToken
                  "schemaVersion", Json.Number(decimal entry.SchemaVersion)
                  "readAt", Json.String(timestamp entry.ReadAt)
                  "lastUsedAt", Json.String(timestamp entry.LastUsedAt)
                  "records",
                  Json.Array(
                      entry.Records
                      |> List.map (fun record ->
                          Json.objectOf
                              [ "path", Json.String record.Path
                                "content", Json.String record.Content
                                "contentHash", Json.String record.ContentHash ])
                  ) ]

        let text = Json.canonicalText value

        if Secrets.looksLikeCredential text then
            Error(ReadCacheFailure.Corrupt "the entry would carry something that looks like a credential")
        else
            Ok text

    let private str name value =
        match Json.field name value with
        | Some(Json.String text) -> Ok text
        | _ -> Error(ReadCacheFailure.Corrupt $"{name} is missing or not text")

    let private integer name value =
        match Json.field name value with
        | Some(Json.Number number) when number = Math.Floor number -> Ok number
        | _ -> Error(ReadCacheFailure.Corrupt $"{name} is missing or not an integer")

    let private instant name value =
        str name value
        |> Result.bind (fun text ->
            match DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, parsed -> Ok(parsed.ToUniversalTime())
            | _ -> Error(ReadCacheFailure.Corrupt $"{name} is not a timestamp"))

    let private records value =
        match Json.field "records" value with
        | Some(Json.Array items) ->
            items
            |> List.fold
                (fun acc item ->
                    match acc, str "path" item, str "content" item, str "contentHash" item with
                    | Ok found, Ok path, Ok content, Ok hash -> Ok({ Path = path; Content = content; ContentHash = hash } :: found)
                    | Error error, _, _, _
                    | _, Error error, _, _
                    | _, _, Error error, _
                    | _, _, _, Error error -> Error error)
                (Ok [])
            |> Result.map List.rev
        | _ -> Error(ReadCacheFailure.Corrupt "records is missing or not a list")

    /// An entry from persisted text, refusing anything this Arca did not write.
    /// It is not yet validated: `cached` checks its token and hashes.
    let decode (text: string) =
        match Json.parse text with
        | Error error -> Error(ReadCacheFailure.Corrupt(JsonError.describe error))
        | Ok value ->
            match integer "arcaCache" value with
            | Error error -> Error error
            | Ok format when format <> decimal Format -> Error(ReadCacheFailure.Corrupt $"cache format {format}, not {Format}")
            | Ok _ ->
                match
                    str "account" value,
                    str "namespace" value,
                    str "partition" value,
                    str "changeToken" value,
                    integer "schemaVersion" value,
                    instant "readAt" value,
                    instant "lastUsedAt" value,
                    records value
                with
                | Ok account, Ok ns, Ok partition, Ok token, Ok version, Ok readAt, Ok lastUsed, Ok items ->
                    Ok
                        { Key =
                            { Account = account
                              Namespace = ns
                              Partition = partition }
                          ChangeToken = token
                          SchemaVersion = int version
                          ReadAt = readAt
                          LastUsedAt = lastUsed
                          Records = items }
                | _ -> Error(ReadCacheFailure.Corrupt "the entry's fields")

/// A read cache in memory: tests, and a tab without durable storage.
[<RequireQualifiedAccess>]
module MemoryReadCache =

    /// What a memory cache holds: each key's stored text.
    [<NoEquality; NoComparison>]
    type Cell =
        { mutable Entries: Map<CacheKey, string>
          mutable Unavailable: bool }

    let cell () = { Entries = Map.empty; Unavailable = false }

    let private guarded (cell: Cell) (body: unit -> Result<'T, ReadCacheFailure>) =
        async { return if cell.Unavailable then Error ReadCacheFailure.Unavailable else body () }

    /// A read cache over a cell.
    let over (cell: Cell) : ReadCacheStore =
        { Load =
            fun key ->
                guarded cell (fun () ->
                    match cell.Entries.TryFind key with
                    | None -> Ok None
                    | Some text -> ReadCache.decode text |> Result.map Some)
          Save =
            fun entry ->
                guarded cell (fun () ->
                    ReadCache.encode entry
                    |> Result.map (fun text -> cell.Entries <- cell.Entries.Add(entry.Key, text)))
          Remove = fun key -> guarded cell (fun () -> Ok(cell.Entries <- cell.Entries.Remove key))
          Partitions =
            fun account ns ->
                guarded cell (fun () ->
                    Ok(
                        cell.Entries
                        |> Map.toList
                        |> List.map fst
                        |> List.filter (ReadCache.inScope (CacheScope.AccountNamespace(account, ns)))
                        |> List.sortBy _.Partition
                    ))
          Clear =
            fun scope ->
                guarded cell (fun () -> Ok(cell.Entries <- cell.Entries |> Map.filter (fun key _ -> not (ReadCache.inScope scope key)))) }

    /// A read cache over a fresh cell of its own.
    let create () = over (cell ())
