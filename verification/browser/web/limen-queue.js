// Kernel side of the IndexedDB offline-queue verification (WI-0016): the same
// WASM runtime as main.js, driven through the DispatchLimenQueue export, with
// Limen's store pack (IndexedDB, namespace chrona) and coordination pack next
// to Core's Storage effect (localStorage). It never inspects a message.
import { BrowserKernel } from "../node_modules/@echelon-foundry/limen/dist/kernel/browser-kernel.js";
import { coordinationCapability } from "../node_modules/@echelon-foundry/limen/dist/capabilities/coordination/index.js";
import { storeCapability } from "../node_modules/@echelon-foundry/limen/dist/capabilities/store/index.js";

const FRAMEWORK = "../build/wasm/wwwroot/_framework";

class WasmEngineTransport {
  #dispatch = null;

  async start() {
    const { dotnet } = await import(`${FRAMEWORK}/dotnet.js`);
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    this.#dispatch = exports.ArcaBrowser.DispatchLimenQueue;
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
        document.documentElement.dataset.capabilities = negotiation.capabilities.map((c) => c.id).join(" ");
      }
    }
  }
};

const kernel = new BrowserKernel(new WasmEngineTransport(), document, diagnostics, {
  requireHandshake: true,
  capabilities: [coordinationCapability(), storeCapability({ namespace: "chrona" })]
});
await kernel.start();
document.documentElement.dataset.kernel = kernel.status;
