/// Canonical JSON (ARCA-REC-001; ARCA-TEST-002 canonical encoding round-trip).
module Arca.Tests.JsonTests

open System.Text
open Arca
open Xunit
open FsCheck.Xunit
open FsCheck.FSharp
open FsCheck

let private parse text =
    match Json.parse text with
    | Ok value -> value
    | Error error -> failwith $"{error}"

[<Fact>]
let ``keys are sorted by ordinal UTF-16 order and whitespace is removed`` () =
    let value = parse """ { "b": 1, "a": [true, null], "B": "x", "é": 0, "aa": {} } """
    Assert.Equal("""{"B":"x","a":[true,null],"aa":{},"b":1,"é":0}""", Json.canonicalText value)

[<Fact>]
let ``numbers are plain decimals with no trailing zeros and no negative zero`` () =
    Assert.Equal("[1.5,1000,0,0.001,-2]", Json.canonicalText (parse "[1.50, 1000, -0, 0.0010, -2.000]"))

[<Theory>]
[<InlineData("1e3")>]
[<InlineData("1E-30")>]
[<InlineData("123456789012345678901234567890123")>]
[<InlineData("0.12345678901234567890123456789012")>]
let ``numbers that cannot be held exactly are refused, never rounded`` (text: string) =
    Assert.Equal(Error(JsonError.UnsupportedNumber text), Json.parse text)

[<Fact>]
let ``strings escape only quotes, backslashes and control characters`` () =
    let value = Json.String "q\"b\\n\n\u0001é😀\u2028/"
    Assert.Equal("\"q\\\"b\\\\n\\n\\u0001é😀\u2028/\"", Json.canonicalText value)

[<Fact>]
let ``duplicate keys, lone surrogates, trailing content and comments are refused`` () =
    Assert.Equal(Error(JsonError.DuplicateKey "a"), Json.parse """{"a":1,"a":2}""")
    Assert.Equal(Error JsonError.InvalidString, Json.parse "\"\\ud800\"")
    Assert.True(Result.isError (Json.parse "{} {}"))
    Assert.True(Result.isError (Json.parse "// x\n{}"))
    Assert.True(Result.isError (Json.parse "[1,]"))

[<Fact>]
let ``nesting beyond the limit is refused`` () =
    let deep = String.replicate 70 "[" + String.replicate 70 "]"
    Assert.True(Result.isError (Json.parse deep))

[<Fact>]
let ``the content hash is sha256 of the canonical bytes`` () =
    let value = parse """{"a":1}"""
    Assert.Equal("sha256:015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862", Json.contentHash value)

[<Property>]
let ``canonical text parses back to the same value (round trip)`` () =
    Prop.forAll Generators.jsonArb (fun value -> Json.parse (Json.canonicalText value) = Ok value)

[<Property>]
let ``canonical encoding is idempotent and recognized as canonical`` () =
    Prop.forAll Generators.jsonArb (fun value ->
        let text = Json.canonicalText value
        Json.isCanonical text && Json.canonicalText (parse text) = text)

[<Property>]
let ``member order never changes the bytes or the hash`` () =
    let members =
        Gen.listOfLength 5 (Gen.zip Generators.text (Generators.json 3))
        |> Gen.map (List.distinctBy fst)
        |> Arb.fromGen

    Prop.forAll members (fun members ->
        let forward = Json.objectOf members
        let backward = Json.objectOf (List.rev members)
        Json.canonicalBytes forward = Json.canonicalBytes backward && Json.contentHash forward = Json.contentHash backward)

[<Property>]
let ``equal numbers in different notations encode identically`` () =
    Prop.forAll (Arb.fromGen Generators.number) (fun (number: decimal) ->
        let plain = Json.numberText number
        let padded = if plain.Contains '.' then plain + "000" else plain + ".000"
        Json.canonicalText (parse padded) = plain)

[<Property>]
let ``canonical bytes are valid UTF-8 of the canonical text`` () =
    Prop.forAll Generators.jsonArb (fun value -> Encoding.UTF8.GetString(Json.canonicalBytes value) = Json.canonicalText value)
