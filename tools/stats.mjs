#!/usr/bin/env bun
// Reads the site's counters. The admin key lives outside this repository, in
// shared-config, so it never ends up in a public commit.
import { join, normalize } from "node:path";

const ROOT = normalize(import.meta.dir);
const URL_ = "https://projects.patrickreinbold.com/brightness-steps/hit.php";

const keyFile = Bun.file(join(ROOT, "..", "..", "shared-config", "brightness-steps-analytics.key"));
if (!(await keyFile.exists())) {
  console.error("missing shared-config/brightness-steps-analytics.key");
  process.exit(1);
}
const key = (await keyFile.text()).trim();

const r = await fetch(`${URL_}?stats=${encodeURIComponent(key)}&t=${Date.now()}`);
if (!r.ok) { console.error(`stats endpoint returned HTTP ${r.status}`); process.exit(1); }
const d = await r.json();

if (!d.totals) { console.log("no data yet"); process.exit(0); }

const t = d.totals;
const pct = (n) => (t.view ? ` (${((n / t.view) * 100).toFixed(1)}% of visits)` : "");

console.log("\n  totals");
console.log(`    visits            ${t.view || 0}`);
console.log(`    installer clicks  ${t.download || 0}${pct(t.download || 0)}`);
console.log(`    portable clicks   ${t.portable || 0}${pct(t.portable || 0)}`);
console.log(`    github clicks     ${t.github || 0}${pct(t.github || 0)}`);
console.log(`    donate clicks     ${t.donate || 0}${pct(t.donate || 0)}`);

const days = Object.entries(d.days || {}).sort((a, b) => b[0].localeCompare(a[0])).slice(0, 14);
if (days.length) {
  console.log("\n  last days");
  for (const [day, c] of days) {
    const bar = "#".repeat(Math.min(40, c.view || 0));
    console.log(`    ${day}  ${String(c.view || 0).padStart(4)} visits  ${String(c.download || 0).padStart(3)} dl  ${bar}`);
  }
}

const refs = Object.entries(d.refs || {}).sort((a, b) => b[1] - a[1]).slice(0, 12);
if (refs.length) {
  console.log("\n  where they came from");
  for (const [host, n] of refs) console.log(`    ${String(n).padStart(5)}  ${host}`);
}
console.log(`\n  updated ${d.updated}\n`);
