// Arca's GitHub adapter in a real browser (ARCA-TEST-003): the .NET
// WebAssembly runtime runs Arca, Limen's kernel executes every request with
// fetch, and GitHub is simulated at the network boundary with real Git blob
// SHAs, fast-forward-only ref updates and CORS, as api.github.com behaves.
import { createHash } from "node:crypto";
import { expect, test } from "@playwright/test";

const sha1 = (text) => createHash("sha1").update(text).digest("hex");
const blobSha = (content) => {
  const bytes = Buffer.from(content, "utf8");
  return sha1(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`, "utf8"), bytes]));
};

const TOKEN = "arca-browser-verification-token";
const REPO = "/repos/verify-owner/verify-data";

/** A minimal GitHub: one repository, one branch, real Git semantics where Arca depends on them. */
function simulatedGitHub() {
  const blobs = new Map();
  const trees = new Map();
  const commits = new Map();
  const treeId = (tree) => {
    const id = sha1("tree\n" + [...tree.entries()].sort().map(([p, b]) => `${p} ${b}`).join("\n"));
    trees.set(id, tree);
    return id;
  };
  const addCommit = (tree, parents, message) => {
    const tid = treeId(tree);
    const id = sha1(`commit\n${tid}|${parents.join(",")}|${message}`);
    commits.set(id, { tree, parents, message });
    return id;
  };
  const refs = new Map([["main", addCommit(new Map(), [], "Initial commit")]]);
  const isAncestor = (ancestor, descendant) =>
    descendant === ancestor || (commits.get(descendant)?.parents ?? []).some((p) => isAncestor(ancestor, p));
  return { blobs, trees, commits, refs, treeId, addCommit, isAncestor, refUpdates: [], loseNextRefUpdate: false };
}

const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, accept, content-type, x-github-api-version, if-none-match",
  "access-control-allow-methods": "GET, POST, PATCH, PUT, DELETE, OPTIONS",
  "access-control-expose-headers": "etag, retry-after, x-ratelimit-limit, x-ratelimit-remaining, x-ratelimit-reset, x-ratelimit-resource"
};

async function serveGitHub(page, github) {
  await page.route("https://api.github.com/**", async (route) => {
    const request = route.request();
    const method = request.method();
    if (method === "OPTIONS") return route.fulfill({ status: 204, headers: cors });

    const json = (status, body) =>
      route.fulfill({ status, headers: { ...cors, "content-type": "application/json", "x-ratelimit-remaining": "4999" }, body: JSON.stringify(body) });

    if (request.headers()["authorization"] !== `Bearer ${TOKEN}`) return json(401, { message: "Bad credentials" });

    const url = new URL(request.url());
    const path = decodeURIComponent(url.pathname);
    const body = request.postData() ? JSON.parse(request.postData()) : null;

    if (method === "GET" && path === "/user") return json(200, { id: 1, login: "browser", type: "User" });
    if (method === "GET" && path === REPO)
      return json(200, { id: 7, name: "verify-data", owner: { login: "verify-owner" }, visibility: "private", archived: false, permissions: { pull: true, push: true } });
    if (method === "GET" && path === `${REPO}/branches/main`) return json(200, { name: "main", protected: false });
    if (method === "GET" && path === `${REPO}/rules/branches/main`) return json(200, []);
    if (method === "GET" && path === `${REPO}/git/ref/heads/main`)
      return json(200, { ref: "refs/heads/main", object: { sha: github.refs.get("main"), type: "commit" } });
    if (method === "GET" && path.startsWith(`${REPO}/git/commits/`)) {
      const id = path.slice(`${REPO}/git/commits/`.length);
      const commit = github.commits.get(id);
      return commit
        ? json(200, { sha: id, tree: { sha: github.treeId(commit.tree) }, message: commit.message, parents: commit.parents.map((p) => ({ sha: p })) })
        : json(404, { message: "Not Found" });
    }
    if (method === "GET" && path.startsWith(`${REPO}/contents/`)) {
      const file = path.slice(`${REPO}/contents/`.length);
      const ref = url.searchParams.get("ref") ?? "main";
      const commit = github.commits.get(github.refs.get(ref) ?? ref);
      const blob = commit?.tree.get(file);
      if (!blob) return json(404, { message: "Not Found" });
      const content = github.blobs.get(blob);
      return json(200, { type: "file", path: file, sha: blob, size: Buffer.byteLength(content), encoding: "base64", content: Buffer.from(content).toString("base64") });
    }
    if (method === "POST" && path === `${REPO}/git/trees`) {
      const base = github.trees.get(body.base_tree);
      if (!base) return json(422, { message: "base_tree is not a tree" });
      const tree = new Map(base);
      for (const entry of body.tree) {
        if (typeof entry.content === "string") {
          const blob = blobSha(entry.content);
          github.blobs.set(blob, entry.content);
          tree.set(entry.path, blob);
        } else tree.delete(entry.path);
      }
      return json(201, { sha: github.treeId(tree) });
    }
    if (method === "POST" && path === `${REPO}/git/commits`) {
      const tree = github.trees.get(body.tree);
      if (!tree) return json(422, { message: "tree not found" });
      const id = github.addCommit(tree, body.parents, body.message);
      return json(201, { sha: id, tree: { sha: body.tree }, message: body.message, parents: body.parents.map((p) => ({ sha: p })) });
    }
    if (method === "PATCH" && path === `${REPO}/git/refs/heads/main`) {
      if (!github.commits.has(body.sha) || !github.isAncestor(github.refs.get("main"), body.sha))
        return json(422, { message: "Update is not a fast forward" });
      github.refs.set("main", body.sha);
      github.refUpdates.push(body.sha);
      if (github.loseNextRefUpdate) {
        github.loseNextRefUpdate = false;
        // The update landed, but the connection drops before the answer.
        return route.abort("connectionreset");
      }
      return json(200, { ref: "refs/heads/main", object: { sha: body.sha } });
    }
    return json(404, { message: "Not Found" });
  });
}

async function run(page) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running", { timeout: 60_000 });
  await page.getByRole("button", { name: "Run" }).click();
  await expect(page.locator("#status")).not.toHaveText(/^(idle|running|)$/, { timeout: 60_000 });
  return errors;
}

test("Arca commits and reads back through Limen's Http effect on .NET WebAssembly", async ({ page }) => {
  const github = simulatedGitHub();
  await serveGitHub(page, github);
  const errors = await run(page);

  await expect(page.locator("#error")).toHaveText("");
  await expect(page.locator("#status")).toHaveText("done");
  await expect(page.locator("html")).toHaveAttribute("data-protocol", /^1\.[34]$/);
  // Git's blob SHA-1, computed by Arca in WebAssembly, equals the one GitHub reports.
  await expect(page.locator("#revisionsMatch")).toHaveText("yes");
  await expect(page.locator("#contentMatches")).toHaveText("yes");
  await expect(page.locator("#recordDecodes")).toHaveText("yes");

  // One operation, one commit, published by one fast-forward ref update.
  expect(github.refUpdates).toHaveLength(1);
  const commit = github.commits.get(github.refs.get("main"));
  expect(commit.message).toMatch(/^chrona: record time in the browser\n\nArca-Format: 1\n/);
  expect(commit.message).toContain("Arca-Actor-Kind: agent\n");
  expect(commit.message).toContain("Arca-Idempotency-Key: browser-verification-0001\n");
  expect([...commit.tree.keys()]).toEqual(["apps/chrona/records/chrona.activity/A-01JBROWSER.json"]);
  await expect(page.locator("#commit")).toHaveText(github.refs.get("main"));
  expect(errors).toEqual([]);
});

test("a ref update whose answer is lost is reconciled as landed, never resent", async ({ page }) => {
  const github = simulatedGitHub();
  github.loseNextRefUpdate = true;
  await serveGitHub(page, github);
  const errors = await run(page);

  // Limen reports the dropped PATCH as OutcomeUnknown; Arca inspects GitHub,
  // finds its commit at the branch head, and reports it landed.
  await expect(page.locator("#error")).toHaveText("");
  await expect(page.locator("#status")).toHaveText("done");
  await expect(page.locator("#revisionsMatch")).toHaveText("yes");
  expect(github.refUpdates).toHaveLength(1);
  await expect(page.locator("#commit")).toHaveText(github.refs.get("main"));
  expect(errors).toEqual([]);
});
