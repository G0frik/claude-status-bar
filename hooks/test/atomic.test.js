const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const os = require("os");
const path = require("path");
const cp = require("child_process");
const { writeJsonAtomic } = require("../atomic");

const tempDir = () => fs.mkdtempSync(path.join(os.tmpdir(), "csb-atomic-"));

// Another process opens `file` and keeps it open for `ms`, like the widget reading it.
function holdOpen(file, ms) {
  const child = cp.spawn(process.execPath, ["-e",
    `const fs=require("fs");const fd=fs.openSync(${JSON.stringify(file)},"r");` +
    `process.stdout.write("open\\n");setTimeout(()=>fs.closeSync(fd),${ms});`]);
  return new Promise((resolve) => child.stdout.once("data", () => resolve(child)));
}

test("writes JSON and leaves no temp file", (t) => {
  const dir = tempDir(); t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, "a.json");
  writeJsonAtomic(file, { n: 1 });
  writeJsonAtomic(file, { n: 2 });
  assert.deepEqual(JSON.parse(fs.readFileSync(file, "utf8")), { n: 2 });
  assert.deepEqual(fs.readdirSync(dir), ["a.json"]);
});

test("puts the temp file in tmpDir when given", (t) => {
  const dir = tempDir(); t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const watched = path.join(dir, "sessions.d");
  writeJsonAtomic(path.join(watched, "s1"), { ok: true }, dir);
  assert.deepEqual(fs.readdirSync(watched), ["s1"]);
});

test("retries while another process holds the target open, then succeeds", async (t) => {
  const dir = tempDir(); t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, "a.json");
  writeJsonAtomic(file, { n: 1 });

  const child = await holdOpen(file, 150);
  const started = Date.now();
  writeJsonAtomic(file, { n: 2 }); // blocks, retrying, until the handle closes (Windows)
  await new Promise((r) => child.once("exit", r));

  assert.deepEqual(JSON.parse(fs.readFileSync(file, "utf8")), { n: 2 });
  assert.ok(Date.now() - started < 1500, "gave up or took too long");
  assert.deepEqual(fs.readdirSync(dir), ["a.json"]);
});

test("gives up after about a second and cleans up its temp file", { skip: process.platform !== "win32" && "rename never blocks off Windows" }, async (t) => {
  const dir = tempDir(); t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, "a.json");
  writeJsonAtomic(file, { n: 1 });

  const child = await holdOpen(file, 3000);
  t.after(() => child.kill());
  assert.throws(() => writeJsonAtomic(file, { n: 2 }), /EPERM|EBUSY|EACCES/);
  assert.deepEqual(fs.readdirSync(dir), ["a.json"]);
});
