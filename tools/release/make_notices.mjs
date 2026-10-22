#!/usr/bin/env node
// make_notices.mjs - writes THIRD-PARTY-NOTICES.txt at the repository root: the licence texts and third-party notices
// of the .NET runtime that the self-contained CK3xBL.exe bundles, taken unchanged from the runtime packs in the local
// NuGet cache (the packs `dotnet publish -r win-x64 --self-contained` uses).
//
// Usage: node tools/release/make_notices.mjs [--version <x.y.z>] [--from-exe <CK3xBL.exe>] [--out <file>] [--check]
//   --version <v>      runtime version (default: read from --from-exe, else the newest 10.x runtime pack in the cache)
//   --from-exe <exe>   take the versions from a published single-file CK3xBL.exe (its runtimeconfig.json)
//   --out <file>       default: THIRD-PARTY-NOTICES.txt at the repository root
//   --check            write nothing; exit 1 when the file differs from what would be written
// The cache is NUGET_PACKAGES, else ~/.nuget/packages. Exit code: 0 = written or up to date, 1 = differs (--check),
// 2 = usage error or a runtime pack is missing.
//
// Imported, it exports noticesText() and bundledRuntimes() for build_release.mjs.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { readBundle } from './lib/bundle.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');
export const NOTICES_FILE = 'THIRD-PARTY-NOTICES.txt';

// The runtime packs whose files go into CK3xBL.exe, and the files of each that must travel with it.
const PACKS = [
  {
    framework: 'Microsoft.NETCore.App',
    pack: 'microsoft.netcore.app.runtime.win-x64',
    title: '.NET runtime',
    files: [['LICENSE.TXT', 'licence'], ['THIRD-PARTY-NOTICES.TXT', 'third-party notices']],
  },
  {
    framework: 'Microsoft.WindowsDesktop.App',
    pack: 'microsoft.windowsdesktop.app.runtime.win-x64',
    title: 'Windows Desktop runtime (Windows Forms)',
    files: [['LICENSE', 'licence']],
  },
];

export class NoticesError extends Error {}

export function nugetCache() {
  return process.env.NUGET_PACKAGES || path.join(os.homedir(), '.nuget', 'packages');
}

const crlf = (s) => s.replace(/^﻿/, '').replace(/\r\n?/g, '\n').replace(/\s+$/, '').split('\n').map((l) => l.replace(/[ \t]+$/, '')).join('\r\n');

/** {framework: version} of the runtimes a self-contained single-file exe bundles, from its runtimeconfig.json. */
export function bundledRuntimes(exeFile) {
  const bundle = readBundle(fs.readFileSync(exeFile));
  if (!bundle) throw new NoticesError(`${exeFile} is not a single-file bundle`);
  const cfg = bundle.files.find((f) => f.type === 'runtimeconfig.json');
  if (!cfg) throw new NoticesError(`${exeFile} bundles no runtimeconfig.json`);
  const json = JSON.parse(cfg.read().toString('utf8').replace(/^﻿/, ''));
  const out = {};
  for (const f of json.runtimeOptions?.includedFrameworks || []) out[f.name] = f.version;
  if (!out['Microsoft.NETCore.App']) throw new NoticesError(`${exeFile} is not self-contained (no includedFrameworks in its runtimeconfig.json)`);
  return out;
}

function newestPackVersion(cache, pack, major) {
  const dir = path.join(cache, pack);
  const versions = fs.existsSync(dir) ? fs.readdirSync(dir).filter((v) => new RegExp(`^${major}\\.\\d+\\.\\d+$`).test(v)) : [];
  const num = (v) => v.split('.').map(Number);
  versions.sort((a, b) => { const [x, y] = [num(a), num(b)]; return x[0] - y[0] || x[1] - y[1] || x[2] - y[2]; });
  return versions.pop() || null;
}

/**
 * The text of THIRD-PARTY-NOTICES.txt (CRLF line ends) for the given runtime versions.
 * @param {{[framework: string]: string}} versions  e.g. {"Microsoft.NETCore.App": "10.0.12", "Microsoft.WindowsDesktop.App": "10.0.12"}
 */
export function noticesText(versions, cache = nugetCache()) {
  const parts = [];
  const used = PACKS.filter((p) => versions[p.framework]);
  for (const p of used) {
    const dir = path.join(cache, p.pack, versions[p.framework]);
    for (const [file] of p.files) {
      if (!fs.existsSync(path.join(dir, file))) throw new NoticesError(`missing ${path.join(p.pack, versions[p.framework], file)} in the NuGet cache ${cache} (run a self-contained dotnet publish once, or set NUGET_PACKAGES)`);
    }
  }
  const head = [
    'Crowns & Blades - third-party notices',
    '======================================',
    '',
    'CK3xBL.exe is a self-contained .NET application. It bundles:',
    ...used.map((p) => `  - the ${p.title} ${versions[p.framework]} (${p.framework}),`),
    'by the .NET Foundation and Contributors, under the MIT License. Their licence texts and the third-party notices of',
    'the .NET runtime follow unchanged, as published in the runtime packs on nuget.org:',
    ...used.map((p) => `  ${p.pack} ${versions[p.framework]}`),
    '',
    'Crowns & Blades itself is licensed under the MIT License: see the LICENSE (or LICENSE.txt) file next to this one.',
    '',
    'The battle gear icons of the CK3 mod are based on icons from game-icons.net by Lorc, Delapouite, Carl Olsen and Sbed,',
    'licensed under Creative Commons Attribution 3.0 (https://creativecommons.org/licenses/by/3.0/); see ck3xbl/CREDITS.md.',
  ];
  parts.push(head.join('\n'));
  for (const p of used) {
    for (const [file, what] of p.files) {
      const title = `${p.title} ${versions[p.framework]}: ${what} (${p.pack}/${file})`;
      parts.push(['', '', '-'.repeat(120), title, '-'.repeat(120), '', fs.readFileSync(path.join(cache, p.pack, versions[p.framework], file), 'utf8')].join('\n'));
    }
  }
  return `${crlf(parts.join('\n'))}\r\n`;
}

/** Same text, ignoring line-end style (a git checkout may convert them). */
export const sameNotices = (a, b) => crlf(a) === crlf(b);

function main() {
  const argv = process.argv.slice(2);
  const o = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const val = () => { if (i + 1 >= argv.length) throw new NoticesError(`${a} needs a value`); return argv[++i]; };
    switch (a) {
      case '--version': o.version = val(); break;
      case '--from-exe': o.exe = val(); break;
      case '--out': o.out = path.resolve(val()); break;
      case '--check': o.check = true; break;
      case '-h': case '--help': o.help = true; break;
      default: throw new NoticesError(`unknown option ${a}`);
    }
  }
  if (o.help) {
    console.log(fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').filter((l) => l.startsWith('//')).slice(0, 13).map((l) => l.slice(3)).join('\n'));
    return 0;
  }
  const cache = nugetCache();
  let versions;
  if (o.exe) versions = bundledRuntimes(o.exe);
  else {
    const v = o.version || newestPackVersion(cache, PACKS[0].pack, 10);
    if (!v || !/^\d+\.\d+\.\d+$/.test(v)) throw new NoticesError(`no .NET runtime pack found in ${cache}; pass --version or --from-exe`);
    versions = Object.fromEntries(PACKS.map((p) => [p.framework, v]));
  }
  const text = noticesText(versions, cache);
  const out = o.out || path.join(REPO, NOTICES_FILE);
  const old = fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : null;
  const same = old !== null && sameNotices(old, text);
  const label = Object.entries(versions).map(([k, v]) => `${k} ${v}`).join(', ');
  if (o.check) {
    console.log(same ? `${path.basename(out)} is up to date (${label})` : `${path.basename(out)} differs from the notices of ${label}: run node tools/release/make_notices.mjs${o.exe ? ' --from-exe <exe>' : ''}`);
    return same ? 0 : 1;
  }
  if (same) { console.log(`${path.basename(out)}: no changes (${label})`); return 0; }
  fs.writeFileSync(out, text);
  console.log(`wrote ${path.basename(out)} (${label}, ${text.length} bytes)`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try { process.exitCode = main(); } catch (e) {
    if (e instanceof NoticesError) { console.error(`make_notices: ${e.message}`); process.exitCode = 2; } else throw e;
  }
}
