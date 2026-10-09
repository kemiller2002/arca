namespace Arca.GitHub

open Arca

/// The GitHub provider's declaration under Arca's storage contract (ARCA-ARCH-003).
[<RequireQualifiedAccess>]
module Provider =

    /// The provider's stable name.
    [<Literal>]
    let Name = "github"

    /// The largest record the adapter stores, in bytes. GitHub's contents API
    /// returns content inline only up to 1 MiB, and Arca stores records, not
    /// large binaries (ARCA-REC-007).
    [<Literal>]
    let MaxObjectBytes = 1048576L

    /// What the GitHub provider offers. At-rest encryption is not offered
    /// (ARCA-LOC-010, DF-ARCA-2026-0002).
    let capabilities: ProviderCapabilities =
        { Provider = Name
          ContractVersion = StorageContract.Version
          States =
            Map.ofList
                [ Capability.ReadObject, CapabilityState.Available 1
                  Capability.ConditionalWrite, CapabilityState.Available 1
                  Capability.ListPrefix, CapabilityState.Available 1
                  Capability.BatchWrite, CapabilityState.Available 1
                  Capability.ChangeToken, CapabilityState.Available 1
                  Capability.NamespaceToken, CapabilityState.Available 1
                  Capability.MaxObjectSize, CapabilityState.Available 1
                  Capability.AtRestEncryption,
                  CapabilityState.Unavailable "at-rest encryption is deferred (DF-ARCA-2026-0002)" ]
          MaxObjectBytes = Some MaxObjectBytes }
