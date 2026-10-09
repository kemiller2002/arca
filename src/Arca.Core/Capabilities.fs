namespace Arca

/// The provider-neutral storage contract's capabilities (ARCA-ARCH-003).
///
/// A provider declares, per capability, whether it is available and at which
/// capability version. A capability that a provider does not declare is
/// unavailable: there is no hidden fallback, and asking for it yields a typed
/// refusal the application must handle.
[<RequireQualifiedAccess>]
type Capability =
    /// Read one object by its address.
    | ReadObject
    /// Write conditioned on expected state (ARCA-CON-001).
    | ConditionalWrite
    /// List the objects under a prefix, with paging.
    | ListPrefix
    /// Several records in one atomic write (ARCA-COMMIT-001).
    | BatchWrite
    /// A token that names the provider's current state, for cheap change detection.
    | ChangeToken
    /// A token that names one namespace's state only (ARCA-CON-005).
    | NamespaceToken
    /// A declared maximum object size.
    | MaxObjectSize
    /// At-rest encryption. Not offered in the first release (ARCA-LOC-010); the
    /// capability exists so a later provider can declare it without a format break.
    | AtRestEncryption

/// Whether a provider offers one capability.
[<RequireQualifiedAccess>]
type CapabilityState =
    /// Offered, at this capability version.
    | Available of version: int
    /// Not offered, and why. Never silently substituted.
    | Unavailable of reason: string

/// A capability the application needs but the provider does not offer.
type CapabilityRefusal =
    { Provider: string
      Capability: Capability
      Reason: string }

/// What one provider offers, under one version of the storage contract.
type ProviderCapabilities =
    { /// The provider's stable name, for example "github".
      Provider: string
      /// The version of Arca's storage contract the provider implements.
      ContractVersion: int
      /// Every capability the provider declares. An absent capability is unavailable.
      States: Map<Capability, CapabilityState>
      /// The largest object the provider stores, in bytes, when MaxObjectSize is available.
      MaxObjectBytes: int64 option }

/// Arca's provider-neutral storage contract.
[<RequireQualifiedAccess>]
module StorageContract =
    /// The version of the storage contract this Arca implements. It is recorded
    /// in every manifest (ARCA-REC-004).
    [<Literal>]
    let Version = 1

/// Total functions over a provider's declared capabilities.
[<RequireQualifiedAccess>]
module ProviderCapabilities =

    /// Every capability in the contract, in declaration order.
    let all =
        [ Capability.ReadObject
          Capability.ConditionalWrite
          Capability.ListPrefix
          Capability.BatchWrite
          Capability.ChangeToken
          Capability.NamespaceToken
          Capability.MaxObjectSize
          Capability.AtRestEncryption ]

    /// The state of one capability. A capability the provider did not declare
    /// is unavailable, never assumed.
    let state capability (capabilities: ProviderCapabilities) =
        capabilities.States
        |> Map.tryFind capability
        |> Option.defaultValue (CapabilityState.Unavailable "not declared by the provider")

    /// The capability's version, or a typed refusal naming the provider and why.
    let require capability (capabilities: ProviderCapabilities) =
        match state capability capabilities with
        | CapabilityState.Available version -> Ok version
        | CapabilityState.Unavailable reason ->
            Error
                { Provider = capabilities.Provider
                  Capability = capability
                  Reason = reason }

    /// Every capability in `needed` the provider does not offer; empty when all are offered.
    let missing needed (capabilities: ProviderCapabilities) =
        needed
        |> List.choose (fun capability ->
            match require capability capabilities with
            | Ok _ -> None
            | Error refusal -> Some refusal)
