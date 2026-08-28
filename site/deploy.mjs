#!/usr/bin/env bun
// Deploy the BrightnessSteps landing page to lima-city via FTPS in ONE curl
// session (lima-city rate-limits parallel logins → single control connection).
//
// Secrets: LIMACITY_FTP_* from env, falling back to ../../shared-config/secrets.env.
// Slug:    SLUG env var (default "brightness-steps") → /projects/<slug>/ on the
//          host, served at https://projects.patrickreinbold.com/<slug>/.
//
// Privacy: lima-city has NO ignore/htaccess filtering — only files in the
// explicit allowlist below ever leave this machine. Keep it public-safe.

import { join, normalize } from "node:path";

const ROOT = normalize(import.meta.dir);
const SLUG = (process.env.SLUG || "brightness-steps").replace(/[^a-z0-9-]/gi, "");
const REMOTE_DIR = `/projects/${SLUG}/`;
const LIVE_URL = `https://projects.patrickreinbold.com/${SLUG}/`;

const FILES = ["index.html", "icon.png", "og.png", "hit.php", "BrightnessSteps-setup.exe"];

// --- credentials: env first, then the shared secrets file --------------------
let { LIMACITY_FTP_HOST: host, LIMACITY_FTP_USER: user, LIMACITY_FTP_PASS: pass } = process.env;
if (!host || !user || !pass) {
  const shared = Bun.file(join(ROOT, "..", "..", "shared-config", "secrets.env"));
  if (await shared.exists()) {
    for (const line of (await shared.text()).split("\n")) {
      const m = line.match(/^(LIMACITY_FTP_(?:HOST|USER|PASS))=(.+)$/);
      if (!m) continue;
      const v = m[2].trim();
      if (m[1] === "LIMACITY_FTP_HOST") host ||= v;
      if (m[1] === "LIMACITY_FTP_USER") user ||= v;
      if (m[1] === "LIMACITY_FTP_PASS") pass ||= v;
    }
  }
}
if (!host || !user || !pass) {
  console.error("missing LIMACITY_FTP_* (env or ../../shared-config/secrets.env)");
  process.exit(1);
}

// hit.php carries the admin key that guards the stats endpoint, so it is built
// here from a committed template plus a secret kept outside the repository.
// Never commit the generated file - this is a public repo.
{
  const keyFile = Bun.file(join(ROOT, "..", "..", "shared-config", "brightness-steps-analytics.key"));
  if (!(await keyFile.exists())) {
    console.error("missing shared-config/brightness-steps-analytics.key");
    process.exit(1);
  }
  const key = (await keyFile.text()).trim();
  const tmpl = await Bun.file(join(ROOT, "hit.template.php")).text();
  await Bun.write(join(ROOT, "hit.php"), tmpl.replace("__ADMIN_KEY__", key));
}

for (const f of FILES) {
  const file = Bun.file(join(ROOT, f));
  if (!(await file.exists())) { console.error(`missing ${f}`); process.exit(1); }
  console.log(`  ${f}  ${(file.size / 1024).toFixed(1)} KB`);
}

console.log(`\nuploading ${FILES.length} file(s) to ftp://${host}${REMOTE_DIR} (one session)...`);
const proc = Bun.spawn([
  "curl", "--ssl-reqd", "-k", "--silent", "--show-error",
  // EPSV stalls on lima-city (451 + response timeouts, and a failed STOR
  // truncates the live file to 0 bytes) — plain PASV works reliably.
  "--disable-epsv",
  "--ftp-create-dirs",
  "--connect-timeout", "30", "--max-time", "600",
  "--retry", "3", "--retry-delay", "5", "--retry-connrefused",
  "--user", `${user}:${pass}`,
  "-T", `{${FILES.join(",")}}`,
  `ftp://${host}${REMOTE_DIR}`,
], { cwd: ROOT, stdout: "inherit", stderr: "inherit" });
if (await proc.exited !== 0) { console.error("upload FAILED"); process.exit(1); }
console.log("upload ok");

// --- verify every uploaded file, by size ------------------------------------
// A failed STOR leaves a 0-byte file behind while curl still exits 0, so
// checking only the page and the installer let a truncated image ship once.
async function check(f) {
  const local = Bun.file(join(ROOT, f)).size;
  const r = await fetch(LIVE_URL + f + "?nocache=" + Date.now());
  const remote = (await r.arrayBuffer()).byteLength;
  return { f, local, remote, ok: r.ok && remote === local };
}

function upload(files) {
  const spec = files.length > 1 ? `{${files.join(",")}}` : files[0];
  return Bun.spawn([
    "curl", "--ssl-reqd", "-k", "--silent", "--show-error", "--disable-epsv",
    "--ftp-create-dirs", "--connect-timeout", "30", "--max-time", "600",
    "--retry", "3", "--retry-delay", "5", "--retry-connrefused",
    "--user", `${user}:${pass}`, "-T", spec, `ftp://${host}${REMOTE_DIR}`,
  ], { cwd: ROOT, stdout: "inherit", stderr: "inherit" }).exited;
}

console.log("");
let results = await Promise.all(FILES.map(check));
let failed = results.filter(r => !r.ok).map(r => r.f);

// Batched uploads occasionally time out on a single file. Retry those alone
// rather than declaring the whole deploy good because curl exited 0.
if (failed.length) {
  console.log(`retrying ${failed.length} file(s) individually...`);
  for (const f of failed) { await upload([f]); }
  results = await Promise.all(FILES.map(check));
}

for (const r of results)
  console.log(`  ${r.ok ? "ok  " : "BAD "} ${r.f.padEnd(30)} ${r.remote} / ${r.local} bytes`);

const bad = results.filter(r => !r.ok).length;
if (bad) { console.error(`
${bad} file(s) did not upload intact`); process.exit(1); }

console.log(`
deployed: ${LIVE_URL}`);
