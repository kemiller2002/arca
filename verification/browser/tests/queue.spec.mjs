// Arca's offline queue across real browser tabs (WI-0024, Limen LCP-059):
// two pages of one browser context share localStorage and Web Locks, as two
// tabs of one application do. Arca runs on .NET WebAssembly behind Limen;
// localStorage is Limen Core's Storage effect and the lock is the
// coordination pack's acquire.
import { expect, test } from "@playwright/test";

const KEY = "arca.queue.chrona";

async function openTab(context) {
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/web/queue.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: 60_000 });
  await expect(page.locator("html")).toHaveAttribute("data-capabilities", /limen\.coordination/);
  return { page, errors };
}

async function press(page, name, expected) {
  await page.getByRole("button", { name, exact: true }).click();
  if (expected !== undefined) await expect(page.locator("#last")).toHaveText(expected);
}

const storedQueue = async (page) => {
  const text = await page.evaluate((key) => window.localStorage.getItem(key), KEY);
  return text === null ? null : JSON.parse(text);
};

test("one tab owns the queue; when it closes the other takes over with nothing lost", async ({ browser }) => {
  const context = await browser.newContext();
  const a = await openTab(context);
  const b = await openTab(context);

  await press(a.page, "Own", "loaded");
  await expect(a.page.locator("#ownership")).toHaveText("owned");
  await press(b.page, "Own");
  await expect(b.page.locator("#ownership")).toHaveText("owned-elsewhere");

  await press(a.page, "Enqueue", /^enqueued /);
  await press(a.page, "Save", "saved");
  await press(a.page, "Enqueue", /^enqueued /);
  await press(a.page, "Save", "saved");
  const ownerKeys = (await a.page.locator("#keys").textContent()).split(",");
  expect(ownerKeys).toHaveLength(2);

  // Closing the owner releases its Web Lock, asynchronously; the other tab
  // asks again until it takes over, as an application does when focused.
  await a.page.close();
  await expect(async () => {
    await press(b.page, "Own");
    await expect(b.page.locator("#ownership")).toHaveText("owned", { timeout: 1_000 });
  }).toPass({ timeout: 30_000 });
  await expect(b.page.locator("#last")).toHaveText("loaded");
  await expect(b.page.locator("#keys")).toHaveText(ownerKeys.join(","));
  await press(b.page, "Enqueue", /^enqueued /);
  await press(b.page, "Save", "saved");

  // A third tab is told another tab holds the queue.
  const c = await openTab(context);
  await press(c.page, "Own");
  await expect(c.page.locator("#ownership")).toHaveText("owned-elsewhere");

  const stored = await storedQueue(b.page);
  expect(stored.entries.map((entry) => entry.sequence)).toEqual([1, 2, 3]);
  expect(stored.entries.slice(0, 2).map((entry) => entry.operation.idempotencyKey)).toEqual(ownerKeys);
  expect([...a.errors, ...b.errors, ...c.errors]).toEqual([]);
  await context.close();
});

test("without a lock, a tab's stale save is refused and never overwrites the other tab's entry", async ({ browser }) => {
  const context = await browser.newContext();
  const a = await openTab(context);
  const b = await openTab(context);

  // Both tabs load the same (empty) queue, as two tabs of 0.2.0 did.
  await press(a.page, "Open without a lock", "loaded");
  await press(b.page, "Open without a lock", "loaded");

  await press(a.page, "Enqueue", /^enqueued /);
  await press(a.page, "Save", "saved");
  const kept = await a.page.locator("#keys").textContent();

  await press(b.page, "Enqueue", /^enqueued /);
  await press(b.page, "Save", "save unavailable");

  const stored = await storedQueue(a.page);
  expect(stored.entries.map((entry) => entry.operation.idempotencyKey)).toEqual([kept]);
  expect([...a.errors, ...b.errors]).toEqual([]);
  await context.close();
});
