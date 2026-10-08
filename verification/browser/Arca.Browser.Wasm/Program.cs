using System.Runtime.InteropServices.JavaScript;

// Entry point the WebAssembly SDK requires. The module is driven only through
// the [JSExport] below; nothing runs here.
return;

/// <summary>Limen kernel side: forwards each message to the F# engine.</summary>
public partial class ArcaBrowser
{
    /// <summary>One Limen message in, the engine's reply out.</summary>
    [JSExport]
    internal static string Dispatch(string messageJson) =>
        Arca.Browser.Engine.Dispatch.handle(messageJson);
}
