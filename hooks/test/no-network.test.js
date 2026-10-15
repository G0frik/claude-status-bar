// The hook scripts that ship (hooks/*.js) must never open a network connection.

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const HOOKS = path.join(__dirname, "..");
const NETWORK_MODULES = ["http", "https", "http2", "net", "tls", "dgram", "dns", "undici"];
const shipped = fs.readdirSync(HOOKS).filter((f) => f.endsWith(".js"));

test("there are hook scripts to check", () => {
  assert.ok(shipped.includes("update.js") && shipped.includes("lifecycle.js"), shipped.join(", "));
});

for (const file of shipped) {
  test(`${file} uses no network module and no fetch`, () => {
    const source = fs.readFileSync(path.join(HOOKS, file), "utf8");
    for (const mod of NETWORK_MODULES) {
      const required = new RegExp(`(require\\(|from\\s+|import\\()\\s*["'](node:)?${mod}(/[^"']*)?["']`);
      assert.doesNotMatch(source, required, `${file} loads "${mod}"`);
    }
    assert.doesNotMatch(source, /\bfetch\s*\(/, `${file} calls fetch()`);
  });
}
