// Arca's IndexedDB offline queue across real browser tabs (WI-0016, Limen
// LCP-059, LCP-060). Two pages of one browser context share IndexedDB and
// Web Locks, as two tabs of one application do. Arca.Limen runs on .NET
// WebAssembly behind Limen: IndexedDB through the store pack (limen.store v2,
// namespace "chrona"), the lock through the coordination pack.
import { expect, test } from "@playwright/test";

// The pack stores the engine's database "arca-queue" as "<namespace>/arca-queue".
const DATABASE = "chrona/arca-queue";
const KEY = "arca.queue.chrona";

async function openTab(context) {
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/web/limen-queue.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: 60_000 });
  await expect(page.locator("html")).toHaveAttribute("data-capabilities", /limen\.coordination/);
  await expect(page.locator("html")).toHaveAttribute("data-capabilities", /limen\.store/);
  return { page, errors };
}

async function press(page, name, expected) {
  await page.getByRole("button", { name, exact: true }).click();
  if (expected !== undefined) await expect(page.locator("#last")).toHaveText(expected);
}

// The queue record as IndexedDB holds it, read straight from the browser.
const storedRecord = (page) =>
  page.evaluate(
    ([database, key]) =>
      new Promise((resolve, reject) => {
        const open = indexedDB.open(database);
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
          const db = open.result;
          const get = db.transaction("queues", "readonly").objectStore("queues").get(key);
          get.onerror = () => reject(get.error);
          get.onsuccess = () => {
            db.close();
            resolve(get.result ?? null);
          };
        };
      }),
    [DATABASE, KEY]
  );

const storedQueue = async (page) => {
  const record = await storedRecord(page);
  return record?.queue ? JSON.parse(record.queue) : null;
};

test("one tab owns the IndexedDB queue; when it closes the other takes over with nothing lost", async ({ browser }) => {
  const context = await browser.newContext();
  const a = await openTab(context);
  const b = await openTab(context);

  await press(a.page, "Own", "loaded");
  await expect(a.page.locator("#ownership")).toHaveText("owned");
  await expect(a.page.locator("#mode")).toHaveText("indexeddb");
  await expect(a.page.locator("#epoch")).toHaveText("1");
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
  await expect(b.page.locator("#epoch")).toHaveText("2");
  await expect(b.page.locator("#keys")).toHaveText(ownerKeys.join(","));
  await press(b.page, "Enqueue", /^enqueued /);
  await press(b.page, "Save", "saved");

  const stored = await storedQueue(b.page);
  expect(stored.entries.map((entry) => entry.sequence)).toEqual([1, 2, 3]);
  expect(stored.entries.slice(0, 2).map((entry) => entry.operation.idempotencyKey)).toEqual(ownerKeys);
  expect((await storedRecord(b.page)).epoch).toBe(2);
  expect([...a.errors, ...b.errors]).toEqual([]);
  await context.close();
});

test("a tab that takes over fences the old owner: its late save writes nothing", async ({ browser }) => {
  const context = await browser.newContext();
  const a = await openTab(context);
  const b = await openTab(context);

  await press(a.page, "Own", "loaded");
  await press(a.page, "Enqueue", /^enqueued /);
  await press(a.page, "Save", "saved");
  const kept = await a.page.locator("#keys").textContent();

  // "Use this tab instead" (OQ-LIMEN-IDB-001): b steals the lock and raises the epoch.
  await press(b.page, "Use this tab instead", "loaded");
  await expect(b.page.locator("#ownership")).toHaveText("owned");
  await expect(b.page.locator("#epoch")).toHaveText("2");
  await expect(b.page.locator("#keys")).toHaveText(kept);

  // The old owner still runs; its save is fenced and writes nothing.
  await press(a.page, "Enqueue", /^enqueued /);
  await press(a.page, "Save", "save unavailable");
  await expect(a.page.locator("#ownership")).toHaveText("fenced");
  expect((await storedQueue(a.page)).entries.map((entry) => entry.operation.idempotencyKey)).toEqual([kept]);

  await press(b.page, "Enqueue", /^enqueued /);
  await press(b.page, "Save", "saved");
  const stored = await storedQueue(b.page);
  expect(stored.entries.map((entry) => entry.sequence)).toEqual([1, 2]);
  expect(stored.entries[0].operation.idempotencyKey).toBe(kept);
  expect([...a.errors, ...b.errors]).toEqual([]);
  await context.close();
});

test("the IndexedDB read cache passes the read-cache conformance suite in this browser", async ({ browser }) => {
  const context = await browser.newContext();
  const tab = await openTab(context);
  await press(tab.page, "Cache conformance", "cache conformance run");
  // Every case runs against real IndexedDB; the faults a page cannot
  // produce (unavailable storage, an entry planted from outside: tampered,
  // tokenless, or written before token scopes) are reported unsupported,
  // never passed.
  await expect(tab.page.locator("#cache")).toHaveText("passed 11, failed 0, unsupported 4");
  expect(tab.errors).toEqual([]);
  await context.close();
});
