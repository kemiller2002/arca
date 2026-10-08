// The one-time move of the localStorage queue into IndexedDB in a real
// browser (WI-0020, Limen LCP-066, LCP-067). Arca runs on .NET WebAssembly
// behind Limen: a tab of the localStorage queue page (Arca 0.2.x's adapter)
// saves a queue; a tab of the IndexedDB page then owns the namespace and
// adopts it exactly once.
import { expect, test } from "@playwright/test";

const DATABASE = "chrona/arca-queue";
const KEY = "arca.queue.chrona";

async function openTab(context, path, capabilities) {
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto(path);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: 60_000 });
  for (const capability of capabilities) {
    await expect(page.locator("html")).toHaveAttribute("data-capabilities", capability);
  }
  return { page, errors };
}

const legacyTab = (context) => openTab(context, "/web/queue.html", [/limen\.coordination/]);
const indexedDbTab = (context) => openTab(context, "/web/limen-queue.html", [/limen\.coordination/, /limen\.store/]);

async function press(page, name, expected) {
  await page.getByRole("button", { name, exact: true }).click();
  if (expected !== undefined) await expect(page.locator("#last")).toHaveText(expected);
}

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

const legacyText = (page) => page.evaluate((key) => window.localStorage.getItem(key), KEY);

// The adopted queue opens in a tab that owns the namespace (retrying while
// the closed tab's Web Lock is released).
async function ownAfterClose(page) {
  await expect(async () => {
    await press(page, "Own");
    await expect(page.locator("#ownership")).toHaveText("owned", { timeout: 1_000 });
  }).toPass({ timeout: 30_000 });
  await expect(page.locator("#last")).toHaveText("loaded");
}

test("a queue saved by the localStorage adapter is moved into IndexedDB once, and the source retired", async ({ browser }) => {
  const context = await browser.newContext();
  const old = await legacyTab(context);
  await press(old.page, "Own", "loaded");
  await press(old.page, "Enqueue", /^enqueued /);
  await press(old.page, "Save", "saved");
  await press(old.page, "Enqueue", /^enqueued /);
  await press(old.page, "Save", "saved");
  const keys = await old.page.locator("#keys").textContent();
  const text = await legacyText(old.page);
  expect(text).not.toBeNull();
  await old.page.close();

  const fresh = await indexedDbTab(context);
  await ownAfterClose(fresh.page);
  await expect(fresh.page.locator("#mode")).toHaveText("indexeddb");
  await expect(fresh.page.locator("#keys")).toHaveText(keys);

  // Copied, verified and retired: the entries are in IndexedDB only.
  expect(await legacyText(fresh.page)).toBeNull();
  const record = await storedRecord(fresh.page);
  expect(JSON.parse(record.queue).entries.map((entry) => entry.operation.idempotencyKey).join(",")).toBe(keys);
  expect(record.migrated).toMatch(/^sha256:[0-9a-f]{64}$/);

  // Order continues after the adopted entries.
  await press(fresh.page, "Enqueue", /^enqueued /);
  await press(fresh.page, "Save", "saved");
  expect(JSON.parse((await storedRecord(fresh.page)).queue).entries.map((entry) => entry.sequence)).toEqual([1, 2, 3]);
  expect([...old.errors, ...fresh.errors]).toEqual([]);
  await context.close();
});

test("a tab closed between the IndexedDB commit and the removal: the next tab removes the source only", async ({ browser }) => {
  const context = await browser.newContext();

  // A queue saved by the localStorage adapter.
  const old = await legacyTab(context);
  await press(old.page, "Own", "loaded");
  await press(old.page, "Enqueue", /^enqueued /);
  await press(old.page, "Save", "saved");
  const keys = await old.page.locator("#keys").textContent();
  const text = await legacyText(old.page);

  // What a tab closed right after the copy committed leaves behind: the
  // IndexedDB record with the queue and its migration marker, at its epoch,
  // and the localStorage key still present.
  await old.page.evaluate(
    async ([database, key, queueText]) => {
      const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(queueText));
      const marker = "sha256:" + [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
      await new Promise((resolve, reject) => {
        const open = indexedDB.open(database, 1);
        open.onupgradeneeded = () => open.result.createObjectStore("queues", { keyPath: "namespace" });
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
          const db = open.result;
          const put = db
            .transaction("queues", "readwrite")
            .objectStore("queues")
            .put({ namespace: key, epoch: 1, queue: queueText, migrated: marker });
          put.onerror = () => reject(put.error);
          put.onsuccess = () => {
            db.close();
            resolve();
          };
        };
      });
    },
    [DATABASE, KEY, text]
  );
  await old.page.close();

  const next = await indexedDbTab(context);
  await ownAfterClose(next.page);
  await expect(next.page.locator("#keys")).toHaveText(keys);
  await expect(next.page.locator("#epoch")).toHaveText("2");

  // Removed only: the queue was not copied a second time.
  expect(await legacyText(next.page)).toBeNull();
  const record = await storedRecord(next.page);
  expect(JSON.parse(record.queue).entries.map((entry) => entry.operation.idempotencyKey).join(",")).toBe(keys);
  expect(record.queue).toBe(text);
  expect([...old.errors, ...next.errors]).toEqual([]);
  await context.close();
});
