namespace Arca.Limen

open Arca
open Arca.GitHub
open Limen.Contract.Coordination

/// The host's coordination executor: Limen's coordination pack (LCP-040),
/// forwarded unchanged. In the browser it is the engine's Limen request loop;
/// in tests, a fake.
type LockExecutor = CoordinationRequest -> Async<CoordinationResult>

/// What a lock request for a namespace's queue produced.
[<RequireQualifiedAccess>]
type QueueLock =
    /// This tab holds the lock until it releases it or closes.
    | Held of LockHandle
    /// Another tab holds it.
    | Busy
    /// This browser has no Web Locks, or the host offers no coordination pack.
    | Unsupported

/// The Web Lock that makes one tab the owner of a namespace's queue (Limen
/// LCP-059). The name is the one `LocalStorageQueue.own` takes, so a tab on
/// the localStorage queue and a tab on the IndexedDB queue exclude each
/// other too.
[<RequireQualifiedAccess>]
module QueueLock =

    /// `arca.queue/<app>` or `arca.queue/<app>/<dataset>`.
    let name (ns: Namespace) = LocalStorageQueue.lockName ns

    /// The request: exclusive, no wait. `steal` takes the lock from the tab
    /// holding it, which hears `LockLost` and is fenced by the epoch
    /// (OQ-LIMEN-IDB-001: a second tab may take over).
    let request (steal: bool) (ns: Namespace) =
        CoordinationRequest.Acquire(name ns, LockMode.Exclusive, false, steal)

    /// What the pack answered. Anything but Acquired or Busy (no pack,
    /// Unsupported, Cancelled) means ownership cannot be established.
    let outcome (result: CoordinationResult) =
        match result with
        | CoordinationResult.Acquired handle -> QueueLock.Held handle
        | CoordinationResult.Busy -> QueueLock.Busy
        | _ -> QueueLock.Unsupported

    /// Takes (or with `steal`, takes over) the namespace's lock.
    let acquire (lock: LockExecutor) (steal: bool) (ns: Namespace) =
        async {
            let! result = lock (request steal ns)
            return outcome result
        }

    /// Releases a held lock; the next contender may take it.
    let release (lock: LockExecutor) (handle: LockHandle) =
        async {
            let! _ = lock (CoordinationRequest.Release handle)
            return ()
        }
