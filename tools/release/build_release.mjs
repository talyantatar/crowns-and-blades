#!/usr/bin/env node
// build_release.mjs - builds a Crowns & Blades release from a clean tree and writes the files players download:
//   dist/CK3xBL-Setup-<version>.exe       the installer (setup stub + payload)
//   dist/CK3xBL-<version>-manual.zip      the same files laid out for a manual install, with INSTALL-MANUAL.txt
//   dist/SHA256SUMS.txt                   checksums of both
//
// Usage: node tools/release/build_release.mjs [options]
//   --dist <dir>         output folder (default: <repo>/dist)
//   --work <dir>         build folder (default: <temp>/ck3xbl-release/<version>); it holds the staged sources, the
//                        publish output and the payload
//   --skip <a,b,...>     skip these steps          --only <a,b,...>   run only these steps
//   --until <step>       stop after this step      --list             print the steps and exit
//   --bl <dir>           Bannerlord root for the module build (default: BANNERLORD_DIR / BL_DIR, else auto-detected)
//   --ck3 <dir>          CK3 install root for the game-file check (default: auto-detected)
//   --require-installer  fail (instead of skip) when the installer pack tool is not available
//
// The version comes from tools/release/version.json only; the versions step checks that every project agrees with it.
// Run from the development tree, the sources are first exported to <work>/src with tools/release/export_public.mjs, so
// the release is built from exactly what is published. Run from the public repository, the repository itself is built.
// Nothing is written into a game install or the user's Documents; no game and no bridge is started.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { writeZip } from './lib/zip.mjs';
import { scanEntries, formatReport, detectInstalls, loadTerms, collectEntries } from './leak_scan.mjs';
import { NOTICES_FILE, NoticesError, bundledRuntimes, noticesText, sameNotices } from './make_notices.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');
const VERSION_FILE = path.join(HERE, 'version.json');
const IS_DEV_TREE = fs.existsSync(path.join(HERE, 'export_public.mjs')) && fs.existsSync(path.join(HERE, 'public_allowlist.json'));

const STEPS = [
  ['stage', 'export the public sources to <work>/src (development tree only)'],
  ['versions', 'every project carries the version of tools/release/version.json'],
  ['gen', 'node tools/gen/gen_ck3_mod.mjs --check (the CK3 mod is up to date)'],
  ['lint', 'node tools/gen/lint_ck3_mod.mjs'],
  ['test-node', 'node tools/gen/test_ck3_mod.mjs, tools/gen/knights/test_combat_traits.mjs and tools/gen/costumes/test_costumes.mjs'],
  ['bridge', 'dotnet build bridge/CK3xBL.slnx -c Release, then the self test'],
  ['module', 'dotnet build of the Bannerlord module and its self-check'],
  ['publish', 'dotnet publish of CK3xBL.exe, self-contained win-x64 single file'],
  ['payload', 'assemble <work>/payload (app/, ck3mod/, blmodule/)'],
  ['gate', 'leak scan of the payload and of every built assembly'],
  ['installer', 'build the setup stub, run its self test, pack the payload into dist/CK3xBL-Setup-<version>.exe'],
  ['manual', 'write dist/CK3xBL-<version>-manual.zip'],
  ['sums', 'write dist/SHA256SUMS.txt and scan the dist files'],
];

// The bridge/data files CK3xBL.exe loads at run time: the only ones the installer puts on a player's PC. The other files
// there are inputs of the generators in tools/gen and tools/data; they stay in the repository. The payload step checks
// that every data file the bridge's code names is on this list.
const RUNTIME_DATA = [
  'bl_banner_colors.json', 'bl_catalog.json', 'bl_ruler_kits.json', 'bl_scenes.json', 'bl_skins_human.json',
  'ck3_accolade_types.json', 'ck3_artifact_to_bl_items.json', 'ck3_catalog.json', 'ck3_combat_traits.json',
  'ck3_emblem_to_bl_icon.json', 'ck3_gene_to_bl_face.json', 'ck3_maa_index.json', 'ck3_named_colors_to_bl.json',
  'ck3_province_climate.json', 'ck3_province_terrain_index.json', 'ck3xbl_costumes.json', 'ck3xbl_equipment.json', 'mapping.json', 'melty_test_snapshot.json', 'terrain_scenes.json',
];

class StepError extends Error {}

// ------------------------------------------------------------------------------------------------ helpers

function parseArgs(argv) {
  const o = { skip: new Set(), only: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const val = () => { if (i + 1 >= argv.length) throw new StepError(`${a} needs a value`); return argv[++i]; };
    const list = () => val().split(',').map((s) => s.trim()).filter(Boolean);
    switch (a) {
      case '--dist': o.dist = path.resolve(val()); break;
      case '--work': o.work = path.resolve(val()); break;
      case '--skip': for (const s of list()) o.skip.add(s); break;
      case '--only': o.only = new Set(list()); break;
      case '--until': o.until = val(); break;
      case '--bl': o.bl = path.resolve(val()); break;
      case '--ck3': o.ck3 = path.resolve(val()); break;
      case '--require-installer': o.requireInstaller = true; break;
      case '--list': o.list = true; break;
      case '-h': case '--help': o.help = true; break;
      default: throw new StepError(`unknown option ${a}`);
    }
  }
  const names = new Set(STEPS.map(([n]) => n));
  for (const n of [...o.skip, ...(o.only || []), ...(o.until ? [o.until] : [])]) if (!names.has(n)) throw new StepError(`unknown step ${n}`);
  return o;
}

const posix = (p) => p.split(path.sep).join('/');
const rm = (p) => fs.rmSync(p, { recursive: true, force: true });
const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');

function listFiles(root, filter = () => true) {
  const out = [];
  const walk = (rel) => {
    for (const e of fs.readdirSync(path.join(root, rel), { withFileTypes: true })) {
      const r = rel ? `${rel}/${e.name}` : e.name;
      if (e.isDirectory()) walk(r);
      else if (e.isFile() && filter(r)) out.push(r);
    }
  };
  if (fs.existsSync(root)) walk('');
  return out.sort();
}

function copyFile(from, to) {
  fs.mkdirSync(path.dirname(to), { recursive: true });
  fs.copyFileSync(from, to);
}

/** Runs a command with inherited output; prints it first exactly as run. */
function run(cmd, args, { cwd, env } = {}) {
  const shown = [cmd, ...args].map((a) => (/[\s&"]/.test(a) ? `"${a}"` : a)).join(' ');
  console.log(`  > ${shown}${cwd ? `   (in ${cwd})` : ''}`);
  const r = spawnSync(cmd, args, {
    cwd, stdio: 'inherit', windowsHide: true,
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1', ...env },
  });
  if (r.error) throw new StepError(`${cmd} could not be started: ${r.error.message}`);
  if (r.status !== 0) throw new StepError(`${shown} exited with code ${r.status}`);
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
}

// ------------------------------------------------------------------------------------------------ steps

function stepStage(ctx) {
  if (!IS_DEV_TREE) {
    console.log('  public repository: building it in place');
    ctx.src = REPO;
    return;
  }
  console.log(`  development tree: exporting the public sources to ${ctx.src}`);
  run(process.execPath, [path.join(HERE, 'export_public.mjs'), ctx.src, '--force', '--quiet', ...(ctx.o.ck3 ? ['--ck3', ctx.o.ck3] : []), ...(ctx.o.bl ? ['--bl', ctx.o.bl] : [])]);
}

function stepVersions(ctx) {
  const want = ctx.version;
  const [prefix, suffix = ''] = want.split('-');
  const moduleVersion = ctx.versionInfo.module_version || `v${prefix}`;
  const checks = [];
  const read = (rel) => { const f = path.join(ctx.src, rel); return fs.existsSync(f) ? fs.readFileSync(f, 'utf8') : null; };
  const grab = (text, re) => { const m = text && text.match(re); return m ? m[1] : null; };
  const check = (what, got, expected) => checks.push({ what, got, expected, ok: got === expected });

  for (const rel of ['Directory.Build.props', 'bridge/Directory.Build.props', 'installer/src/CK3xBL.Setup.csproj']) {
    const t = read(rel);
    if (t === null) continue;
    const p = grab(t, /<VersionPrefix>([^<]*)<\/VersionPrefix>/);
    const s = grab(t, /<VersionSuffix>([^<]*)<\/VersionSuffix>/);
    if (p === null && s === null) continue;
    check(`${rel} VersionPrefix-VersionSuffix`, [p, s].filter(Boolean).join('-'), want);
  }
  const csproj = read('bannerlord_module/CK3Bridge/src/CK3Bridge.csproj');
  const fixedVersion = grab(csproj, /<Version>([^<]*)<\/Version>/);
  if (fixedVersion !== null) check('bannerlord_module/CK3Bridge/src/CK3Bridge.csproj <Version> (should come from the shared props)', fixedVersion, prefix);
  const bv = read('bridge/CK3xBL.Core/BridgeVersion.cs');
  if (bv !== null) {
    check('bridge/CK3xBL.Core/BridgeVersion.cs Mod', grab(bv, /const string Mod = "([^"]*)"/), want);
    check('bridge/CK3xBL.Core/BridgeVersion.cs Module', grab(bv, /const string Module = "([^"]*)"/), moduleVersion);
  }
  check('bannerlord_module/CK3Bridge/SubModule.xml <Version>', grab(read('bannerlord_module/CK3Bridge/SubModule.xml'), /<Version value="([^"]*)"/), moduleVersion);
  check('ck3_mod/ck3xbl/descriptor.mod version', grab(read('ck3_mod/ck3xbl/descriptor.mod'), /^version="([^"]*)"/m), want);

  for (const c of checks) console.log(`  ${c.ok ? 'ok  ' : 'BAD '} ${c.what}: ${c.got ?? '(missing)'}${c.ok ? '' : ` (expected ${c.expected})`}`);
  const bad = checks.filter((c) => !c.ok);
  if (suffix && !/^[0-9A-Za-z.-]+$/.test(suffix)) throw new StepError(`version suffix "${suffix}" is not valid`);
  if (bad.length) throw new StepError(`${bad.length} version mismatch(es) with tools/release/version.json (${want})`);
}

function stepGen(ctx) {
  run(process.execPath, ['tools/gen/gen_ck3_mod.mjs', '--check'], { cwd: ctx.src });
}

function stepLint(ctx) {
  run(process.execPath, ['tools/gen/lint_ck3_mod.mjs'], { cwd: ctx.src });
}

function stepTestNode(ctx) {
  run(process.execPath, ['tools/gen/test_ck3_mod.mjs'], { cwd: ctx.src });
  run(process.execPath, ['tools/gen/knights/test_combat_traits.mjs'], { cwd: ctx.src });
  run(process.execPath, ['tools/gen/costumes/test_costumes.mjs'], { cwd: ctx.src });
}

function msbuildCommon(ctx) {
  return ['-p:Deterministic=true', '-p:ContinuousIntegrationBuild=true', `-p:PathMap=${ctx.src}=/_/`];
}

function stepBridge(ctx) {
  run('dotnet', ['build', 'bridge/CK3xBL.slnx', '-c', 'Release', '-nologo', ...msbuildCommon(ctx)], { cwd: ctx.src });
  run('dotnet', ['run', '--project', 'bridge/CK3xBL.SelfTest/CK3xBL.SelfTest.csproj', '-c', 'Release', '--no-build'], { cwd: ctx.src });
}

function moduleProps(ctx) {
  const bl = ctx.o.bl || process.env.BANNERLORD_DIR || process.env.BL_DIR;
  return [...(bl ? [`-p:BannerlordDir=${bl}`] : []), '-p:DebugType=none', '-p:DebugSymbols=false', ...msbuildCommon(ctx)];
}

function stepModule(ctx) {
  run('dotnet', ['build', 'bannerlord_module/CK3Bridge/src/CK3Bridge.csproj', '-c', 'Release', '-nologo', ...moduleProps(ctx)], { cwd: ctx.src });
  const dll = path.join(ctx.src, 'bannerlord_module', 'CK3Bridge', 'bin', 'Win64_Shipping_Client', 'CK3Bridge.dll');
  if (!fs.existsSync(dll)) throw new StepError(`the module build did not produce ${dll}`);
  const selfcheck = 'bannerlord_module/CK3Bridge/selfcheck/CK3Bridge.SelfCheck.csproj';
  if (fs.existsSync(path.join(ctx.src, selfcheck))) {
    run('dotnet', ['build', selfcheck, '-c', 'Release', '-nologo', ...moduleProps(ctx)], { cwd: ctx.src });
    const exe = listFiles(path.join(ctx.src, 'bannerlord_module', 'CK3Bridge', 'selfcheck', 'bin'), (r) => /(^|\/)CK3Bridge\.SelfCheck\.exe$/.test(r));
    if (!exe.length) throw new StepError('the module self-check build produced no CK3Bridge.SelfCheck.exe');
    run(path.join(ctx.src, 'bannerlord_module', 'CK3Bridge', 'selfcheck', 'bin', exe[0]), [], { cwd: ctx.src });
  }
  // CK3Bridge.Naval (PROTOCOL §44) needs the War Sails assemblies to build; without them the release ships no naval module.
  const bl = ctx.o.bl || process.env.BANNERLORD_DIR || process.env.BL_DIR;
  if (bl && fs.existsSync(path.join(bl, 'Modules', 'NavalDLC', 'bin', 'Win64_Shipping_Client', 'NavalDLC.dll'))) {
    run('dotnet', ['build', 'bannerlord_module/CK3Bridge.Naval/src/CK3Bridge.Naval.csproj', '-c', 'Release', '-nologo', ...moduleProps(ctx)], { cwd: ctx.src });
  } else console.log('  CK3Bridge.Naval: War Sails not found in the Bannerlord folder; the release ships no naval module');
}

function stepPublish(ctx) {
  rm(ctx.publish);
  run('dotnet', ['publish', 'bridge/CK3xBL/CK3xBL.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-nologo',
    '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none', '-p:DebugSymbols=false', ...msbuildCommon(ctx), '-o', ctx.publish], { cwd: ctx.src });
  const files = listFiles(ctx.publish);
  console.log(`  published: ${files.join(', ')}`);
  const extra = files.filter((f) => f !== 'CK3xBL.exe');
  if (!files.includes('CK3xBL.exe')) throw new StepError('dotnet publish produced no CK3xBL.exe');
  if (extra.length) throw new StepError(`dotnet publish produced more than the single CK3xBL.exe: ${extra.join(', ')}`);
}

/** Data files of bridge/data that the code of the bridge and the module names (tests and self-checks left out). */
function dataFilesNamedInCode(src, dataFiles) {
  const names = new Map(dataFiles.map((f) => [f, new RegExp(`(?:\\b${f.replace(/\./g, '\\.')}\\b|"${f.replace(/\.json$/, '')}")`)]));
  const named = new Map();
  const notCode = /(^|\/)(bin|obj|CK3xBL\.SelfTest|selfcheck|tests?)\//i;
  for (const dir of ['bridge', 'bannerlord_module']) {
    for (const r of listFiles(path.join(src, dir), (x) => x.endsWith('.cs') && !notCode.test(`/${x}`))) {
      const text = fs.readFileSync(path.join(src, dir, r), 'utf8');
      for (const [f, re] of names) if (re.test(text) && !named.has(f)) named.set(f, `${dir}/${r}`);
    }
  }
  return named;
}

/** LICENSE and THIRD-PARTY-NOTICES.txt; the notices must be those of the runtime the published exe bundles. */
function checkLicenceFiles(ctx) {
  const licence = path.join(ctx.src, 'LICENSE');
  const notices = path.join(ctx.src, NOTICES_FILE);
  if (!fs.existsSync(licence)) throw new StepError('LICENSE is missing at the repository root');
  if (!fs.existsSync(notices)) throw new StepError(`${NOTICES_FILE} is missing at the repository root: the self-contained CK3xBL.exe redistributes the .NET runtime, whose notices must ship with it (node tools/release/make_notices.mjs writes it)`);
  let runtimes;
  let want;
  try {
    runtimes = bundledRuntimes(path.join(ctx.publish, 'CK3xBL.exe'));
    want = noticesText(runtimes);
  } catch (e) {
    if (e instanceof NoticesError) throw new StepError(e.message);
    throw e;
  }
  const label = Object.entries(runtimes).map(([k, v]) => `${k} ${v}`).join(', ');
  if (!sameNotices(fs.readFileSync(notices, 'utf8'), want)) {
    throw new StepError(`${NOTICES_FILE} does not match the runtime CK3xBL.exe bundles (${label}): run node tools/release/make_notices.mjs --from-exe ${path.join(ctx.publish, 'CK3xBL.exe')} in the development tree, then build again`);
  }
  console.log(`  ${NOTICES_FILE}: matches the bundled runtime (${label})`);
  return { licence, notices };
}

/** The installer payload (layout of installer/PAYLOAD_LAYOUT.md): app/, ck3mod/ck3xbl/, blmodule/CK3Bridge/. */
function stepPayload(ctx) {
  const P = ctx.payload;
  rm(P);
  const put = (from, rel) => { copyFile(from, path.join(P, rel)); ctx.payloadFiles.push(rel); };
  ctx.payloadFiles = [];

  const { licence, notices } = checkLicenceFiles(ctx);
  put(path.join(ctx.publish, 'CK3xBL.exe'), 'app/CK3xBL.exe');
  put(licence, 'app/LICENSE.txt');
  put(notices, `app/${NOTICES_FILE}`);

  const data = path.join(ctx.src, 'bridge', 'data');
  const dataFiles = listFiles(data, (r) => !r.includes('/') && r.endsWith('.json'));
  const missing = RUNTIME_DATA.filter((f) => !dataFiles.includes(f));
  if (missing.length) throw new StepError(`runtime data missing from bridge/data: ${missing.join(', ')}`);
  const unlisted = [...dataFilesNamedInCode(ctx.src, dataFiles)].filter(([f]) => !RUNTIME_DATA.includes(f));
  if (unlisted.length) throw new StepError(`the bridge names data files that the payload would not install (add them to RUNTIME_DATA in build_release.mjs): ${unlisted.map(([f, where]) => `${f} (${where})`).join(', ')}`);
  for (const f of RUNTIME_DATA) put(path.join(data, f), `app/data/${f}`);
  const left = dataFiles.filter((f) => !RUNTIME_DATA.includes(f));
  if (left.length) console.log(`  not installed (generator inputs): ${left.join(', ')}`);

  const mod = path.join(ctx.src, 'ck3_mod', 'ck3xbl');
  const modFiles = listFiles(mod);
  const odd = modFiles.filter((r) => /(_test|probe)/i.test(r) || r.startsWith('run/'));
  if (odd.length) throw new StepError(`the CK3 mod holds test or probe files: ${odd.join(', ')}`);
  for (const f of modFiles) put(path.join(mod, f), `ck3mod/ck3xbl/${f}`);

  const blm = path.join(ctx.src, 'bannerlord_module', 'CK3Bridge');
  put(path.join(blm, 'SubModule.xml'), 'blmodule/CK3Bridge/SubModule.xml');
  put(path.join(blm, 'bin', 'Win64_Shipping_Client', 'CK3Bridge.dll'), 'blmodule/CK3Bridge/bin/Win64_Shipping_Client/CK3Bridge.dll');
  for (const f of listFiles(path.join(blm, 'GUI'))) put(path.join(blm, 'GUI', f), `blmodule/CK3Bridge/GUI/${f}`);
  // The naval module goes with the app: the bridge installs it into Modules only where War Sails is (NavalModule.Ensure).
  const naval = path.join(ctx.src, 'bannerlord_module', 'CK3Bridge.Naval');
  const navalDll = path.join(naval, 'bin', 'Win64_Shipping_Client', 'CK3Bridge.Naval.dll');
  if (fs.existsSync(navalDll)) {
    put(path.join(naval, 'SubModule.xml'), 'app/naval/CK3Bridge.Naval/SubModule.xml');
    put(navalDll, 'app/naval/CK3Bridge.Naval/bin/Win64_Shipping_Client/CK3Bridge.Naval.dll');
  }

  const count = (pre) => ctx.payloadFiles.filter((f) => f.startsWith(pre)).length;
  console.log(`  payload: ${count('app/')} app file(s) (${count('app/data/')} data), ${count('ck3mod/')} CK3 mod file(s), ${count('blmodule/')} Bannerlord module file(s) -> ${P}`);
}

function leakOptions(ctx) {
  const { terms, source } = loadTerms(IS_DEV_TREE ? path.join(HERE, 'public_allowlist.json') : undefined);
  return {
    profile: 'release',
    devRoots: [REPO],
    devPaths: [...new Set([ctx.src, ctx.work])].filter((p) => p !== REPO),
    terms,
    installs: detectInstalls({ ck3: ctx.o.ck3, bl: ctx.o.bl || process.env.BANNERLORD_DIR || process.env.BL_DIR }),
    rulesSource: source,
  };
}

function scanAndReport(entries, opts, title) {
  const result = scanEntries(entries, opts);
  for (const line of formatReport(result, { title })) console.log(`  ${line}`);
  return result;
}

function stepGate(ctx) {
  const opts = leakOptions(ctx);
  console.log(`  project rules: ${opts.rulesSource ? path.basename(opts.rulesSource) : 'none (generic rules only)'}`);
  const payload = collectEntries(ctx.payload).map((e) => ({ rel: `payload/${e.rel}`, data: e.data }));
  // The single-file exe is compressed, so its strings cannot be read: scan the assemblies of ours that go into it, the
  // module DLL and the setup stub instead. Not scanned: the .NET runtime files (Microsoft's), intermediate and reference
  // assemblies, and the test programs, which are never shipped (they embed the local game paths they test against).
  const built = [];
  const ours = /(^|\/)(CK3xBL[\w.-]*|CK3Bridge)\.(dll|exe)$/i;
  const notShipped = /SelfTest|SelfCheck|\.Tests\b|\/(obj|ref|refint)\//i;
  const shippedBuild = (r) => /\/bin\//.test(`/${r}`) && /\/Release\/|\/Win64_Shipping_Client\//.test(`/${r}`) && ours.test(r) && !notShipped.test(`/${r}`);
  for (const dir of ['bridge', 'bannerlord_module', 'installer']) {
    for (const f of listFiles(path.join(ctx.src, dir), shippedBuild)) {
      built.push({ rel: `built/${dir}/${f}`, data: fs.readFileSync(path.join(ctx.src, dir, f)) });
    }
  }
  const pdbs = listFiles(ctx.payload, (r) => /\.pdb$/i.test(r));
  if (pdbs.length) throw new StepError(`debug symbols in the payload: ${pdbs.join(', ')}`);
  const result = scanAndReport([...payload, ...built], opts, `leak gate: ${payload.length} payload file(s), ${built.length} built assemblies`);
  if (result.failures) throw new StepError(`the leak gate failed with ${result.failures} finding(s)`);
}

/** The installer's test and pack tool (installer/tests): its self test, and
 *  `pack --stub <exe> --payload <dir> --version <v> --out <exe> [--ck3 <range>] [--bannerlord <range>] [--forbid <regex>]...`. */
function findPackProject(src) {
  const dir = path.join(src, 'installer', 'tests');
  if (!fs.existsSync(dir)) return null;
  const proj = listFiles(dir, (r) => !r.includes('/') && r.endsWith('.csproj'));
  return proj.length ? `installer/tests/${proj[0]}` : null;
}

function stepInstaller(ctx) {
  const stubProject = 'installer/src/CK3xBL.Setup.csproj';
  const packProject = findPackProject(ctx.src);
  if (!fs.existsSync(path.join(ctx.src, stubProject)) || !packProject) {
    const why = `the installer is not ready (${[!fs.existsSync(path.join(ctx.src, stubProject)) && stubProject, !packProject && 'installer/tests/*.csproj (pack tool)'].filter(Boolean).join(', ')} missing)`;
    if (ctx.o.requireInstaller) throw new StepError(why);
    console.log(`  SKIPPED: ${why}`);
    ctx.skippedInstaller = true;
    return;
  }
  run('dotnet', ['build', stubProject, '-c', 'Release', '-nologo', ...msbuildCommon(ctx)], { cwd: ctx.src });
  run('dotnet', ['build', packProject, '-c', 'Release', '-nologo', ...msbuildCommon(ctx)], { cwd: ctx.src });
  run('dotnet', ['run', '--project', packProject, '-c', 'Release', '--no-build', '--', 'selftest'], { cwd: ctx.src });
  const stubs = listFiles(path.join(ctx.src, 'installer', 'src', 'bin', 'Release'), (r) => /(^|\/)CK3xBL-Setup\.exe$/.test(r));
  if (stubs.length !== 1) throw new StepError(`expected one CK3xBL-Setup.exe under installer/src/bin/Release, found ${stubs.length}`);
  const out = path.join(ctx.dist, `CK3xBL-Setup-${ctx.version}.exe`);
  const forbid = [];
  for (const root of new Set([REPO, ctx.src, ctx.work])) forbid.push(path.resolve(root).replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\\\\/g, '[\\\\/]+'));
  const supports = ctx.versionInfo.installer || {};
  run('dotnet', ['run', '--project', packProject, '-c', 'Release', '--no-build', '--', 'pack',
    '--stub', path.join(ctx.src, 'installer', 'src', 'bin', 'Release', stubs[0]),
    '--payload', ctx.payload, '--version', ctx.version, '--out', out,
    ...(supports.supports_ck3 ? ['--ck3', supports.supports_ck3] : []), ...(supports.supports_bannerlord ? ['--bannerlord', supports.supports_bannerlord] : []),
    ...forbid.flatMap((f) => ['--forbid', f])], { cwd: ctx.src });
  if (!fs.existsSync(out)) throw new StepError(`the pack tool did not write ${out}`);
}

function installText(ctx) {
  const s = ctx.versionInfo.supported || {};
  const v = ctx.version;
  return [
    `Crowns & Blades ${v} - manual install`,
    '='.repeat(40 + v.length),
    '',
    `Built for Crusader Kings III ${s.ck3 || '1.20'} and Mount & Blade II: Bannerlord ${s.bannerlord || 'v1.4.8'} (Steam).`,
    'Other versions may break and are not blocked. You need your own copies of both games; no game files are included.',
    '',
    'The installer (CK3xBL-Setup-' + v + '.exe) does all of this for you. Use these steps only if you prefer to copy the',
    'files yourself.',
    '',
    '0. Before you extract this zip: right-click it > Properties > tick "Unblock" > OK. Windows marks downloaded files,',
    '   and Bannerlord refuses to load a marked CK3Bridge.dll.',
    '',
    '1. The bridge. Copy the folder "CK3xBL" anywhere you like, for example',
    '   %LOCALAPPDATA%\\Programs\\CK3xBL, and start CK3xBL.exe. It sits in the notification area (tray). It finds both',
    '   games itself; use the tray menu to set the game folders if it does not.',
    '',
    '2. The CK3 mod. Copy the contents of "Crusader Kings III\\mod" (the folder "ck3xbl" and the file "ck3xbl.mod") into',
    '   %USERPROFILE%\\Documents\\Paradox Interactive\\Crusader Kings III\\mod',
    '   (if your Documents folder was moved, for example by OneDrive, use that one). Then enable "Crowns & Blades',
    '   Battles" in your playset in the Paradox Launcher. If the launcher does not list it, open ck3xbl.mod in a text',
    '   editor and change path="mod/ck3xbl" to the full path of the copied ck3xbl folder, with forward slashes.',
    '',
    '3. The Bannerlord module. Copy the folder "Mount & Blade II Bannerlord\\Modules\\CK3Bridge" into the Modules folder',
    '   of your Bannerlord install, for example',
    '   C:\\Program Files (x86)\\Steam\\steamapps\\common\\Mount & Blade II Bannerlord\\Modules',
    '   You do not need to tick it in the Bannerlord launcher: the bridge starts Bannerlord with it. If you do tick it,',
    '   it stays inactive in your own campaigns.',
    '',
    '4. Start Bannerlord once by hand and click through its first-start notices, then close it.',
    '',
    'Uninstall: delete the CK3xBL folder, the ck3xbl folder and ck3xbl.mod from the CK3 mod folder, and Modules\\CK3Bridge',
    'from Bannerlord. Settings and logs are in %LOCALAPPDATA%\\ck3xbl.',
    '',
    'Licence: MIT (LICENSE.txt). Third-party notices: THIRD-PARTY-NOTICES.txt.',
    'Crusader Kings III is a trademark of Paradox Interactive AB. Mount & Blade II: Bannerlord is a trademark of',
    'TaleWorlds Entertainment. This is a free fan project, not affiliated with or endorsed by either company.',
    '',
  ].join('\r\n');
}

function stepManual(ctx) {
  const root = `CK3xBL-${ctx.version}`;
  const entries = [];
  const add = (name, data) => entries.push({ name: `${root}/${name}`, data });
  add('INSTALL-MANUAL.txt', Buffer.from(installText(ctx), 'utf8'));
  for (const f of ['app/LICENSE.txt', 'app/THIRD-PARTY-NOTICES.txt']) {
    const p = path.join(ctx.payload, f);
    if (fs.existsSync(p)) add(path.basename(f), fs.readFileSync(p));
  }
  for (const f of listFiles(path.join(ctx.payload, 'app'), (r) => r === 'CK3xBL.exe' || r.startsWith('data/'))) add(`CK3xBL/${f}`, fs.readFileSync(path.join(ctx.payload, 'app', f)));
  for (const f of listFiles(path.join(ctx.payload, 'ck3mod'))) add(`Crusader Kings III/mod/${f}`, fs.readFileSync(path.join(ctx.payload, 'ck3mod', f)));
  // The outer descriptor CK3 reads from the mod folder; setup writes it with the absolute path, the manual zip uses the
  // path relative to the CK3 user folder.
  const inner = fs.readFileSync(path.join(ctx.payload, 'ck3mod', 'ck3xbl', 'descriptor.mod'), 'utf8').replace(/^\uFEFF/, '').replace(/\s+$/, '');
  add('Crusader Kings III/mod/ck3xbl.mod', Buffer.from(`${inner.replace(/^path=.*$/m, '').trimEnd()}\npath="mod/ck3xbl"\n`, 'utf8'));
  for (const f of listFiles(path.join(ctx.payload, 'blmodule'))) add(`Mount & Blade II Bannerlord/Modules/${f}`, fs.readFileSync(path.join(ctx.payload, 'blmodule', f)));

  const date = ctx.versionInfo.release_date ? new Date(`${ctx.versionInfo.release_date}T00:00:00Z`) : undefined;
  const zip = writeZip(entries, date ? { date } : {});
  const out = path.join(ctx.dist, `CK3xBL-${ctx.version}-manual.zip`);
  fs.writeFileSync(out, zip);
  console.log(`  wrote ${out} (${entries.length} files, ${(zip.length / 1048576).toFixed(1)} MB)`);
}

function stepSums(ctx) {
  const names = fs.readdirSync(ctx.dist).filter((f) => f === `CK3xBL-Setup-${ctx.version}.exe` || f === `CK3xBL-${ctx.version}-manual.zip`).sort();
  if (!names.length) throw new StepError(`nothing to checksum in ${ctx.dist}`);
  const lines = names.map((n) => `${sha256(fs.readFileSync(path.join(ctx.dist, n)))}  ${n}`);
  fs.writeFileSync(path.join(ctx.dist, 'SHA256SUMS.txt'), `${lines.join('\n')}\n`);
  for (const l of lines) console.log(`  ${l}`);
  const result = scanAndReport(names.map((n) => ({ rel: n, data: fs.readFileSync(path.join(ctx.dist, n)) })), leakOptions(ctx), 'leak scan of the release files:');
  if (result.failures) throw new StepError(`the release files failed the leak scan with ${result.failures} finding(s)`);
}

const IMPL = {
  stage: stepStage, versions: stepVersions, gen: stepGen, lint: stepLint, 'test-node': stepTestNode, bridge: stepBridge,
  module: stepModule, publish: stepPublish, payload: stepPayload, gate: stepGate, installer: stepInstaller, manual: stepManual, sums: stepSums,
};

// ------------------------------------------------------------------------------------------------ main

function main() {
  const o = parseArgs(process.argv.slice(2));
  if (o.help) {
    console.log(fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').filter((l) => l.startsWith('//')).slice(0, 22).map((l) => l.slice(3)).join('\n'));
    return 0;
  }
  if (o.list) { for (const [n, d] of STEPS) console.log(`${n.padEnd(10)} ${d}`); return 0; }

  const versionInfo = readJson(VERSION_FILE);
  const version = versionInfo.version;
  if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version || '')) throw new StepError(`tools/release/version.json: "${version}" is not a version`);
  const work = o.work || path.join(os.tmpdir(), 'ck3xbl-release', version);
  const ctx = {
    o, version, versionInfo, work,
    src: IS_DEV_TREE ? path.join(work, 'src') : REPO,
    publish: path.join(work, 'publish'),
    payload: path.join(work, 'payload'),
    dist: o.dist || path.join(REPO, 'dist'),
    payloadFiles: [],
  };
  const rel = path.relative(REPO, work);
  if (IS_DEV_TREE && (!rel || (!rel.startsWith('..') && !path.isAbsolute(rel)))) throw new StepError('--work must be outside the development tree');
  fs.mkdirSync(ctx.dist, { recursive: true });

  console.log(`build_release: Crowns & Blades ${version}`);
  console.log(`  tree: ${IS_DEV_TREE ? 'development (exported before building)' : 'public repository'}; sources: ${ctx.src}`);
  console.log(`  work: ${work}; dist: ${ctx.dist}`);
  const started = Date.now();
  const done = [];
  let stop = false;
  for (const [name, desc] of STEPS) {
    if (stop) break;
    if (o.until === name) stop = true;
    const selected = o.only ? o.only.has(name) : !o.skip.has(name);
    if (!selected) { console.log(`\n[${name}] skipped (${desc})`); done.push([name, 'skipped']); continue; }
    console.log(`\n[${name}] ${desc}`);
    const t0 = Date.now();
    IMPL[name](ctx);
    done.push([name, name === 'installer' && ctx.skippedInstaller ? 'SKIPPED (not ready)' : `ok (${((Date.now() - t0) / 1000).toFixed(1)} s)`]);
  }
  console.log('\nsummary:');
  for (const [n, s] of done) console.log(`  ${n.padEnd(10)} ${s}`);
  const outputs = fs.existsSync(ctx.dist) ? fs.readdirSync(ctx.dist).filter((f) => f.includes(version) || f === 'SHA256SUMS.txt').sort() : [];
  for (const f of outputs) console.log(`  dist/${f}  ${fs.statSync(path.join(ctx.dist, f)).size} bytes`);
  console.log(`build_release: done in ${((Date.now() - started) / 1000).toFixed(0)} s`);
  return 0;
}

try {
  process.exitCode = main();
} catch (e) {
  if (e instanceof StepError) { console.error(`\nbuild_release: FAILED: ${e.message}`); process.exitCode = 1; } else throw e;
}
