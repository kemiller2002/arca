module Arca.Tests.LibraryTests

open Xunit

[<Fact>]
let ``scaffold builds and links the library`` () =
    Assert.True Arca.Library.scaffoldReady
