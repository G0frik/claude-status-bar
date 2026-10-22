// Atomic JSON writes shared by the hook scripts. Readers (the widget, other hooks) never see a
// partial file: the content goes to a temp file first and is renamed into place. On Windows a
// rename onto a file that another process has open briefly fails with EPERM/EBUSY, so the rename
// is retried for up to about a second before giving up.

const fs = require("fs");
const path = require("path");

const RETRYABLE = new Set(["EPERM", "EBUSY", "EACCES"]);
const sleep = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);

// Write text to file. The temp file goes in tmpDir (default: next to file), which must be on the
// same volume; pass another directory when file's own directory is watched for entries.
// Throws if the write ultimately fails.
function writeFileAtomic(file, text, tmpDir = path.dirname(file)) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.mkdirSync(tmpDir, { recursive: true });
  const tmp = path.join(tmpDir, `${path.basename(file)}.${process.pid}.tmp`);
  fs.writeFileSync(tmp, text);
  for (let attempt = 1; ; attempt++) {
    try {
      fs.renameSync(tmp, file);
      return;
    } catch (e) {
      if (attempt >= 20 || !RETRYABLE.has(e.code)) {
        try { fs.rmSync(tmp, { force: true }); } catch {}
        throw e;
      }
      sleep(5 * attempt);
    }
  }
}

const writeJsonAtomic = (file, obj, tmpDir) => writeFileAtomic(file, JSON.stringify(obj), tmpDir);

module.exports = { writeFileAtomic, writeJsonAtomic };
