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

    /// <summary>The offline-queue page (WI-0024): one Limen message in, the engine's reply out.</summary>
    [JSExport]
    internal static string DispatchQueue(string messageJson) =>
        Arca.Browser.QueueEngine.Dispatch.handle(messageJson);

    /// <summary>The IndexedDB offline-queue page (WI-0016): one Limen message in, the engine's reply out.</summary>
    [JSExport]
    internal static string DispatchLimenQueue(string messageJson) =>
        Arca.Browser.LimenQueueEngine.Dispatch.handle(messageJson);
}
