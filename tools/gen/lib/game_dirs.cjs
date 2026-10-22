// Locates the game installs and the CK3 script_docs dumps for the Node tools (generators, lint, data builders).
// No machine-specific defaults: environment variables win, then the Steam libraries are searched, then the default
// Steam folder under Program Files. Every lookup is read-only.
//
//   CK3_GAME_DIR         the CK3 "game" folder (…/Crusader Kings III/game); the install root is accepted too
//   BL_DIR               the Bannerlord root (…/Mount & Blade II Bannerlord); BANNERLORD_DIR is accepted too
//   CK3XBL_SCRIPT_DOCS   folder holding CK3's script_docs dumps (effects.log, triggers.log, …); default: the CK3 logs
//                        folder in the user's Documents
//
// CommonJS so both the .mjs generators (import) and the .js data builders (require) can use it.
'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

/** Repository root (this file lives in <root>/tools/gen/lib). */
const REPO_ROOT = path.resolve(__dirname, '..', '..', '..');

const CK3_FOLDER = 'Crusader Kings III';
const BL_FOLDER = 'Mount & Blade II Bannerlord';
const CK3_PROBE = path.join('game', 'common', 'culture');
const BL_PROBE = path.join('Modules', 'Native', 'SubModule.xml');
const SCRIPT_DOCS = ['effects.log', 'triggers.log', 'event_targets.log', 'event_scopes.log', 'on_actions.log', 'modifiers.log'];

const exists = (p) => { try { fs.accessSync(p); return true; } catch { return false; } };
const slash = (p) => p.replace(/\\/g, '/');

/** The on-disk spelling of an existing folder, with forward slashes (stable output on every machine). */
function canonical(p) {
  try { return slash(fs.realpathSync.native(p)); } catch { return slash(path.resolve(p)); }
}

/** One REG_SZ / REG_EXPAND_SZ value via reg.exe (Windows only), or null. */
function regValue(key, name) {
  if (process.platform !== 'win32') return null;
  const r = spawnSync('reg', ['query', key, '/v', name], { encoding: 'utf8', windowsHide: true });
  if (r.status !== 0 || !r.stdout) return null;
  for (const line of r.stdout.split(/\r?\n/)) {
    const m = line.match(/^\s+(.+?)\s{2,}(REG_SZ|REG_EXPAND_SZ)\s{2,}(.*)$/);
    if (m && m[1].toLowerCase() === name.toLowerCase()) {
      const value = m[3].trim();
      return m[2] === 'REG_EXPAND_SZ' ? value.replace(/%([^%]+)%/g, (all, v) => process.env[v] ?? all) : value;
    }
  }
  return null;
}

let steamLibrariesCache = null;

/** Steam library folders: the Steam install itself plus every entry of steamapps/libraryfolders.vdf. */
function steamLibraries() {
  if (steamLibrariesCache) return steamLibrariesCache;
  const home = os.homedir();
  const roots = [
    regValue('HKCU\\Software\\Valve\\Steam', 'SteamPath'),
    regValue('HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam', 'InstallPath'),
    regValue('HKLM\\SOFTWARE\\Valve\\Steam', 'InstallPath'),
    process.env['ProgramFiles(x86)'] && path.join(process.env['ProgramFiles(x86)'], 'Steam'),
    process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'Steam'),
    path.join(home, '.steam', 'steam'),
    path.join(home, '.local', 'share', 'Steam'),
    path.join(home, 'Library', 'Application Support', 'Steam'),
  ].filter((p) => p && exists(p));
  const libs = [];
  const add = (p) => { const c = canonical(p); if (!libs.some((l) => l.toLowerCase() === c.toLowerCase())) libs.push(c); };
  for (const root of roots) {
    add(root);
    const vdf = path.join(root, 'steamapps', 'libraryfolders.vdf');
    if (!exists(vdf)) continue;
    for (const m of fs.readFileSync(vdf, 'utf8').matchAll(/"path"\s+"((?:[^"\\]|\\.)*)"/g)) {
      const lib = m[1].replace(/\\\\/g, '\\');
      if (exists(lib)) add(lib);
    }
  }
  steamLibrariesCache = libs;
  return libs;
}

/** First Steam library holding steamapps/common/<folder>/<probe>, as a canonical path, or null. */
function findSteamApp(folder, probe) {
  for (const lib of steamLibraries()) {
    const dir = path.join(lib, 'steamapps', 'common', folder);
    if (exists(path.join(dir, probe))) return canonical(dir);
  }
  return null;
}

function notFound(what, envName, extra = '') {
  return new Error(`${what} not found. Set ${envName} to its folder${extra}.`);
}

/**
 * The CK3 "game" folder. Env CK3_GAME_DIR is used as given (the install root is accepted and mapped to its game
 * folder); otherwise the Steam libraries are searched. Returns null when not found and `required` is false.
 */
function ck3GameDir({ required = true } = {}) {
  const env = process.env.CK3_GAME_DIR;
  if (env) {
    if (!exists(path.join(env, 'common')) && exists(path.join(env, 'game', 'common'))) return slash(path.join(env, 'game'));
    return env;
  }
  const root = findSteamApp(CK3_FOLDER, CK3_PROBE);
  if (root) return `${root}/game`;
  if (required) throw notFound('Crusader Kings III', 'CK3_GAME_DIR', ' (the ".../Crusader Kings III/game" folder)');
  return null;
}

/** The CK3 install root (the folder holding game/ and launcher/), or null when not found and not required. */
function ck3RootDir({ required = true } = {}) {
  const game = ck3GameDir({ required });
  return game ? slash(path.dirname(path.resolve(game))) : null;
}

/** The Bannerlord root. Env BL_DIR (or BANNERLORD_DIR) is used as given; otherwise the Steam libraries are searched. */
function bannerlordDir({ required = true } = {}) {
  const env = process.env.BL_DIR || process.env.BANNERLORD_DIR;
  if (env) return env;
  const root = findSteamApp(BL_FOLDER, BL_PROBE);
  if (root) return root;
  if (required) throw notFound('Mount & Blade II: Bannerlord', 'BL_DIR', ' (the "Mount & Blade II Bannerlord" root)');
  return null;
}

/** The user's Documents folder (honours a Documents folder moved by OneDrive or by the user). */
function documentsDir() {
  const personal = regValue('HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders', 'Personal')
    || regValue('HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders', 'Personal');
  if (personal && exists(personal)) return personal;
  return path.join(os.homedir(), 'Documents');
}

/** CK3's user folder (saves, logs, mod/): Documents/Paradox Interactive/Crusader Kings III (Linux: ~/.local/share/...). */
function ck3UserDir() {
  const linux = path.join(os.homedir(), '.local', 'share', 'Paradox Interactive', CK3_FOLDER);
  if (process.platform === 'linux' && exists(linux)) return linux;
  return path.join(documentsDir(), 'Paradox Interactive', CK3_FOLDER);
}

const SCRIPT_DOCS_HELP = [
  'CK3 script_docs dumps not found. They are generated by CK3 itself and are not part of this repository:',
  '  1. start CK3 with the launch option -debug_mode and load any game (use a throwaway game: the console',
  '     disables achievements for that playthrough);',
  '  2. open the console (the key below Esc) and run:  script_docs',
  '  3. CK3 writes effects.log, triggers.log, event_targets.log, event_scopes.log, on_actions.log and modifiers.log',
  '     to <Documents>/Paradox Interactive/Crusader Kings III/logs/.',
  'The tools read them from there, or from the folder in the environment variable CK3XBL_SCRIPT_DOCS.',
].join('\n');

/**
 * Folder with CK3's script_docs dumps: env CK3XBL_SCRIPT_DOCS, else the CK3 logs folder. Every file in `need` must be
 * present; when one is missing this throws an error explaining how to create the dumps (or returns null when not
 * `required`).
 */
function scriptDocsDir({ required = true, need = SCRIPT_DOCS } = {}) {
  const dir = process.env.CK3XBL_SCRIPT_DOCS || path.join(ck3UserDir(), 'logs');
  const missing = need.filter((f) => !exists(path.join(dir, f)));
  if (missing.length === 0) return dir;
  if (!required) return null;
  throw new Error(`${SCRIPT_DOCS_HELP}\nLooked in: ${dir} (missing: ${missing.join(', ')})`);
}

/** Path of `file` relative to the repository root, with forward slashes (for generator metadata). */
function repoRelative(file) {
  return slash(path.relative(REPO_ROOT, file));
}

module.exports = {
  REPO_ROOT, SCRIPT_DOCS, SCRIPT_DOCS_HELP,
  ck3GameDir, ck3RootDir, bannerlordDir, documentsDir, ck3UserDir, scriptDocsDir, steamLibraries, repoRelative,
};
