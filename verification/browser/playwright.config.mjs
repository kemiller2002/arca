// Arca's real-browser verification (ARCA-TEST-003). Serves this directory
// (the page reaches into node_modules/ for Limen and build/ for the .NET
// WebAssembly bundle) and drives the page in Chromium and WebKit.
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

const port = 4377;
const origin = `http://127.0.0.1:${port}`;

// Use a preinstalled Chromium where the environment provides one; elsewhere
// Playwright resolves its own, so CI needs no special case.
const preinstalledChromium = "/opt/pw-browsers/chromium";
const launchOptions = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 120_000,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: { baseURL: origin, trace: "retain-on-failure" },
  // WebKit runs every page too (OQ-LIMEN-IDB-006): Playwright WebKit in CI,
  // plus a manual iPad Safari checklist per release (docs/consuming-arca.md).
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions } },
    { name: "webkit", use: { ...devices["Desktop Safari"] } }
  ],
  webServer: {
    command: `python3 -m http.server ${port} --bind 127.0.0.1`,
    url: `${origin}/web/index.html`,
    reuseExistingServer: !process.env.CI,
    timeout: 60_000
  }
});
