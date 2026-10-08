// Kernel side of the Arca browser verification (ARCA-TEST-003): loads the
// published .NET WebAssembly runtime, hands each Limen message (JSON) to the
// [JSExport] shim, and starts Limen's BrowserKernel, which executes every
// Http effect with the browser's fetch. It never inspects a message.
import { BrowserKernel } from "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js";

const FRAMEWORK = "../build/wasm/wwwroot/_framework";

class WasmEngineTransport {
  #dispatch = null;

  async start() {
    const { dotnet } = await import(`${FRAMEWORK}/dotnet.js`);
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    this.#dispatch = exports.ArcaBrowser.Dispatch;
  }

  async dispatch(message) {
    return JSON.parse(this.#dispatch(JSON.stringify(message)));
  }
}

const diagnostics = {
  report(event) {
    if (event.kind === "BridgeError") {
      console.error(`[limen] bridge error during ${event.phase}: ${event.detail}`);
    } else if (event.kind === "Handshake" && event.verdict.kind === "Incompatible") {
      console.error(`[limen] incompatible: ${JSON.stringify(event.verdict.reason)}`);
    } else if (event.kind === "Handshake") {
      const negotiation = event.verdict.negotiation;
      if (negotiation?.kind === "Negotiated") {
        document.documentElement.dataset.protocol = `${negotiation.protocol.major}.${negotiation.protocol.minor}`;
      }
    }
  }
};

const kernel = new BrowserKernel(new WasmEngineTransport(), document, diagnostics, { requireHandshake: true });
await kernel.start();
document.documentElement.dataset.kernel = kernel.status;
