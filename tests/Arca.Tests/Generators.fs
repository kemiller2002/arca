/// FsCheck generators shared by the property tests.
module Arca.Tests.Generators

open Arca
open FsCheck
open FsCheck.FSharp

/// Strings that exercise escaping and ordering: ASCII, control characters,
/// quotes, backslashes, non-ASCII and astral (surrogate-pair) characters.
let text =
    let pieces =
        Gen.elements [ "a"; "B"; "z9"; "\""; "\\"; "\n"; "\t"; "\u0001"; "\u001f"; "é"; "日本"; "😀"; " "; "/"; " "; "" ]

    Gen.listOfLength 4 pieces |> Gen.map (String.concat "")

/// Exact decimals, including negative, zero and trailing-zero forms.
let number =
    Gen.oneof
        [ Gen.choose (-1000000, 1000000) |> Gen.map decimal
          Gen.map2 (fun (a: int) (b: int) -> decimal a / decimal (max 1 (abs b))) (Gen.choose (-100000, 100000)) (Gen.choose (1, 1000))
          Gen.elements [ 0m; -0m; 1.50m; 1.500m; 0.1m; 79228162514264337593543950335m; -79228162514264337593543950335m ] ]

/// Arbitrary JSON values, nested up to the given size.
let rec json size =
    let leaf =
        Gen.oneof
            [ Gen.constant Json.Null
              Gen.elements [ true; false ] |> Gen.map Json.Bool
              number |> Gen.map Json.Number
              text |> Gen.map Json.String ]

    if size <= 0 then
        leaf
    else
        let child = json (size / 3)

        Gen.oneof
            [ leaf
              Gen.listOfLength 3 child |> Gen.map Json.Array
              Gen.listOfLength 3 (Gen.zip text child)
              |> Gen.map (List.distinctBy fst >> Json.objectOf) ]

/// An arbitrary JSON value.
let jsonArb = Gen.sized (fun size -> json (min size 20)) |> Arb.fromGen
