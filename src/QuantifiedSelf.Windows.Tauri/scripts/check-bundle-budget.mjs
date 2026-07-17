import { gzipSync } from 'node:zlib';
import { readdir, readFile, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const assetsDirectory = fileURLToPath(new URL('../dist/assets/', import.meta.url));
const budgets = [
  { label: '主 bundle', pattern: /^index-[^.]+\.js$/, rawKiB: 400, gzipKiB: 120 },
  { label: 'Dashboard route chunk', pattern: /^DashboardPage-[^.]+\.js$/, rawKiB: 32, gzipKiB: 10 },
  { label: 'Settings route chunk', pattern: /^SettingsPage-[^.]+\.js$/, rawKiB: 18, gzipKiB: 8 },
];

const assetNames = await readdir(assetsDirectory);
let failed = false;

for (const budget of budgets) {
  const matches = assetNames.filter((name) => budget.pattern.test(name));
  if (matches.length !== 1) {
    console.error(`[FAIL] ${budget.label}: 预期一个独立产物，实际 ${matches.length} 个。`);
    failed = true;
    continue;
  }

  const filePath = join(assetsDirectory, matches[0]);
  const rawBytes = (await stat(filePath)).size;
  const gzipBytes = gzipSync(await readFile(filePath)).byteLength;
  const rawLimit = budget.rawKiB * 1024;
  const gzipLimit = budget.gzipKiB * 1024;
  const passed = rawBytes <= rawLimit && gzipBytes <= gzipLimit;
  const status = passed ? 'PASS' : 'FAIL';
  console.log(
    `[${status}] ${budget.label}: ${toKiB(rawBytes)} KiB raw / ${toKiB(gzipBytes)} KiB gzip `
      + `(budget ${budget.rawKiB} / ${budget.gzipKiB} KiB)`,
  );
  failed ||= !passed;
}

if (failed) process.exitCode = 1;

function toKiB(bytes) {
  return (bytes / 1024).toFixed(2);
}
