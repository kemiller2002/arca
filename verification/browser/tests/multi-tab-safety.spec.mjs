// Multi-tab safety of Arca's IndexedDB queue in a real browser (WI-0038,
// Indy-init R7-REM-001). Rehearsal 6 defect R6-D-002: without
// navigator.locks, several tabs read, merged and wrote one localStorage
// record at once, and acknowledged changes were lost on every run. These
// tests run Arca.Limen on .NET WebAssembly behind Limen, as the existing
// limen-queue spec does, and check that:
// - without Web Locks no tab owns the durable queue: ownership is refused
//   and nothing is written (fail closed, never a silent lost update);
// - under seeded random interleavings of three tabs (enqueue, save, steal,
//   close the owner, reopen) no acknowledged save is ever lost, and a fenced
//   save writes nothing;
// - acknowledged changes survive a browser restart (storage restored into a
//   new context).
import { expect, test } from "@playwright/test";

const DATABASE = "chrona/arca-queue";
const KEY = "arca.queue.chrona";

async function openTab(context) {
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/web/limen-queue.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: 60_000 });
  await expect(page.locator("html")).toHaveAttribute("data-capabilities", /limen\.store/);
  return { page, errors, closed: false };
}

const text = (page, id) => page.locator(`#${id}`).textContent();
const click = (page, name) => page.getByRole("button", { name, exact: true }).click();

const storedKeys = (page) =>
  page.evaluate(
    ([database, key]) =>
      new Promise((resolve, reject) => {
        const open = indexedDB.open(database);
        open.onerror = () => reject(open.error);
        open.onsuccess = () => {
          const db = open.result;
          if (!db.objectStoreNames.contains("queues")) {
            db.close();
            resolve(null);
            return;
          }
          const get = db.transaction("queues", "readonly").objectStore("queues").get(key);
          get.onerror = () => reject(get.error);
          get.onsuccess = () => {
            db.close();
            const record = get.result ?? null;
            resolve(record?.queue ? JSON.parse(record.queue).entries.map((entry) => entry.operation.idempotencyKey) : null);
          };
        };
      }),
    [DATABASE, KEY]
  );

const keysOf = async (page) => {
  const value = await text(page, "keys");
  return value === "" ? [] : value.split(",");
};

// Adds one entry to the tab's queue and waits until the page shows it.
async function enqueue(page) {
  const before = Number(await text(page, "entries"));
  await click(page, "Enqueue");
  await expect(page.locator("#entries")).toHaveText(String(before + 1));
}

// Saves (after an enqueue, so the result text always changes) and returns it.
async function save(page) {
  await enqueue(page);
  await click(page, "Save");
  await expect(page.locator("#last")).not.toHaveText(/^enqueued /);
  return text(page, "last");
}

// Takes the queue over ("use this tab instead") and waits for the load.
async function steal(page) {
  await enqueue(page);
  await click(page, "Use this tab instead");
  await expect(page.locator("#last")).toHaveText("loaded");
}

// Asks for ownership until this tab holds it (a closed owner's Web Lock is
// released asynchronously).
async function ownEventually(page) {
  await expect(async () => {
    await click(page, "Own");
    await expect(page.locator("#ownership")).toHaveText("owned", { timeout: 1_000 });
  }).toPass({ timeout: 30_000 });
}

// A small seeded generator (mulberry32), so a failing interleaving replays.
function random(seed) {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

test("R6-D-002, no Web Locks: no tab can own the durable queue, and nothing is written", async ({ browser }) => {
  const context = await browser.newContext();
  // As Rehearsal 6's AR-035 spec did: this browser has no Web Locks.
  await context.addInitScript(() => {
    delete Navigator.prototype.locks;
  });
  const tabs = [await openTab(context), await openTab(context), await openTab(context)];

  for (const tab of tabs) {
    expect(await tab.page.evaluate(() => "locks" in navigator)).toBe(false);
    await click(tab.page, "Own");
    await expect(tab.page.locator("#ownership")).toHaveText("unsupported");
    await click(tab.page, "Use this tab instead");
    await expect(tab.page.locator("#ownership")).toHaveText("unsupported");
  }

  // Every tab tries to save; none holds a store, so none writes.
  for (const tab of tabs) {
    await enqueue(tab.page);
    await click(tab.page, "Save");
  }
  expect(await storedKeys(tabs[0].page)).toBeNull();
  expect(tabs.flatMap((tab) => tab.errors)).toEqual([]);
  await context.close();
});

for (const seed of [1, 2, 3]) {
  test(`R6-D-002, Web Locks: no acknowledged save is lost under random three-tab interleavings (seed ${seed})`, async ({ browser }) => {
    const next = random(seed);
    const pick = (items) => items[Math.floor(next() * items.length)];
    const context = await browser.newContext();
    let tabs = [await openTab(context), await openTab(context), await openTab(context)];
    const open = () => tabs.filter((tab) => !tab.closed);
    let owner = tabs[0];
    let acknowledged = [];
    const counts = { saved: 0, fenced: 0, steals: 0, closes: 0 };

    await ownEventually(owner.page);

    for (let step = 0; step < 30; step += 1) {
      const operation = pick(["enqueue", "save", "save", "stale-save", "steal", "close-owner"]);
      if (operation === "enqueue") {
        await enqueue(pick(open()).page);
      } else if (operation === "save") {
        const result = await save(owner.page);
        expect(result, `step ${step}: the owner's save`).toBe("saved");
        const keys = await keysOf(owner.page);
        // No lost update: everything acknowledged before is still there.
        expect(keys.slice(0, acknowledged.length), `step ${step}: earlier acknowledged keys`).toEqual(acknowledged);
        acknowledged = keys;
        counts.saved += 1;
      } else if (operation === "stale-save") {
        // A tab that once owned the queue and was taken over saves late.
        const fenced = open().filter((tab) => tab !== owner && tab.held);
        if (fenced.length === 0) continue;
        const result = await save(pick(fenced).page);
        expect(result, `step ${step}: a fenced tab's save`).toBe("save unavailable");
        counts.fenced += 1;
      } else if (operation === "steal") {
        const others = open().filter((tab) => tab !== owner);
        if (others.length === 0) continue;
        const taker = pick(others);
        await steal(taker.page);
        expect(await keysOf(taker.page), `step ${step}: the new owner loads every acknowledged key`).toEqual(acknowledged);
        owner = taker;
        counts.steals += 1;
      } else if (operation === "close-owner") {
        await owner.page.close();
        owner.closed = true;
        tabs = [...tabs, await openTab(context)];
        const successor = pick(open());
        // A tab that held the queue before regains it through "use this tab
        // instead"; a tab that never did asks to own it.
        if (successor.held) {
          await steal(successor.page);
        } else {
          await ownEventually(successor.page);
          await expect(successor.page.locator("#last")).toHaveText("loaded");
        }
        expect(await keysOf(successor.page), `step ${step}: the successor loads every acknowledged key`).toEqual(acknowledged);
        owner = successor;
        counts.closes += 1;
      }
      owner.held = true;
      // What IndexedDB holds is exactly what was last acknowledged.
      expect(await storedKeys(owner.page), `step ${step} (${operation}): stored keys`).toEqual(acknowledged.length === 0 ? null : acknowledged);
    }

    test.info().annotations.push({ type: "interleaving", description: JSON.stringify({ seed, ...counts, acknowledged: acknowledged.length }) });
    expect(counts.saved).toBeGreaterThan(0);
    expect(tabs.flatMap((tab) => tab.errors)).toEqual([]);
    await context.close();
  });
}

test("R6-D-002, restart: acknowledged changes survive the browser's storage being restored into a new context", async ({ browser }) => {
  const first = await browser.newContext();
  const a = await openTab(first);
  await ownEventually(a.page);
  for (let i = 0; i < 3; i += 1) expect(await save(a.page)).toBe("saved");
  const acknowledged = await keysOf(a.page);
  const state = await first.storageState({ indexedDB: true });
  await first.close();

  const second = await browser.newContext({ storageState: state });
  const b = await openTab(second);
  await ownEventually(b.page);
  await expect(b.page.locator("#last")).toHaveText("loaded");
  expect(await keysOf(b.page)).toEqual(acknowledged);
  expect([...a.errors, ...b.errors]).toEqual([]);
  await second.close();
});
