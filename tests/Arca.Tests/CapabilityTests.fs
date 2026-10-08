/// The provider capability model (ARCA-ARCH-003).
module Arca.Tests.CapabilityTests

open Arca
open Xunit

let private bare =
    { Provider = "test"
      ContractVersion = StorageContract.Version
      States = Map.ofList [ Capability.ReadObject, CapabilityState.Available 2 ]
      MaxObjectBytes = None }

[<Fact>]
let ``a declared capability yields its version`` () =
    Assert.Equal(Ok 2, ProviderCapabilities.require Capability.ReadObject bare)

[<Fact>]
let ``an undeclared capability is explicitly unavailable, never a hidden fallback`` () =
    match ProviderCapabilities.require Capability.ConditionalWrite bare with
    | Error refusal ->
        Assert.Equal("test", refusal.Provider)
        Assert.Equal(Capability.ConditionalWrite, refusal.Capability)
        Assert.Equal("not declared by the provider", refusal.Reason)
    | Ok _ -> failwith "an undeclared capability must be refused"

[<Fact>]
let ``missing lists every needed capability the provider lacks, in order`` () =
    let missing =
        ProviderCapabilities.missing [ Capability.BatchWrite; Capability.ReadObject; Capability.ListPrefix ] bare
        |> List.map _.Capability

    Assert.Equal<Capability list>([ Capability.BatchWrite; Capability.ListPrefix ], missing)

[<Fact>]
let ``the GitHub provider declares every capability, offering all but at-rest encryption`` () =
    let github = Arca.GitHub.Provider.capabilities
    Assert.Equal(StorageContract.Version, github.ContractVersion)

    for capability in ProviderCapabilities.all do
        Assert.True(github.States.ContainsKey capability, $"{capability} is not declared")

    let missing = ProviderCapabilities.missing ProviderCapabilities.all github |> List.map _.Capability
    Assert.Equal<Capability list>([ Capability.AtRestEncryption ], missing)
    Assert.Equal(Some 1048576L, github.MaxObjectBytes)
