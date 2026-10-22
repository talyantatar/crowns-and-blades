#!/usr/bin/env node
// leak_scan.mjs - checks a folder (or a release archive) before it is published: no personal data, no machine paths,
// no secrets, no reverse-engineered game code, no copied game files, no stray binaries or logs.
//
// Usage:
//   node tools/release/leak_scan.mjs <folder | file.zip | file.exe> [options]
//
// Options:
//   --profile repo|release   repo (default): a source tree, where binaries, archives and fonts are not allowed.
//                            release: build output, where binaries are allowed and scanned for strings, zip archives
//                            (also a zip appended to an exe) are opened and scanned entry by entry, and so is a .NET
//                            single-file exe (our files in it; the .NET runtime's own files are skipped).
//   --terms <json>           extra project-private rules: {"forbidden_patterns":[{pattern,flags,why,skip_files}],
//                            "warn_patterns":[...], "allow_matches":[{pattern,flags}], "forbidden_files":[{glob,why}]}.
//                            Default: public_allowlist.json next to this script, when it exists.
//   --dev-root <dir>         a development folder whose path and folder name must not appear (repeatable).
//   --dev-path <dir>         a build or export folder whose path must not appear (repeatable).
//   --ck3 <dir>, --bl <dir>  game install roots for the byte-identical check (default: auto-detected).
//   --no-install-hash        skip the byte-identical check against the game installs.
//   --require-installs       fail when no game install is found for the byte-identical check.
//   --allow-binary <glob>    allow a binary file in the repo profile (repeatable).
//   --allow-log <glob>       allow a .log file (repeatable; default: the synthetic test fixtures).
//   --max-image-mb <n>       images above this size fail (default 1).
//   --warn-mb <n>            files above this size are reported (default 2).
//   --max-lines <n>          findings printed per rule (default 25).
//   --json <file>            also write the full report as JSON.
// Exit code: 0 = no failures (warnings allowed), 1 = failures, 2 = usage error.
//
// Imported, it exports scanEntries() for the release scripts (export_public.mjs, build_release.mjs).

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { readZip, locateZip } from './lib/zip.mjs';
import { readBundle } from './lib/bundle.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));

// Some rule words are assembled at run time, so that this file (which is published and scanned too) does not match itself.
const w = (...parts) => parts.join('');

export const DEFAULTS = {
  profile: 'repo',
  maxImageMb: 1,
  warnMb: 2,
  minHashBytes: 64,
  maxLines: 25,
  allowLogs: ['bridge/CK3xBL.SelfTest/fixtures/*.log'],
  allowBinary: [],
};

// Files that never belong in the public repository or a release, whatever their content ([glob, why, only in profile]).
// Project-private file names are not listed here: they come from the "forbidden_files" of the project rules (--terms).
const FORBIDDEN_FILES = [
  ['bridge/config.json', 'per-user bridge settings (holds local game and Documents paths)'],
  ['**/bin/**', 'build output', 'repo'],
  ['**/obj/**', 'build intermediates', 'repo'],
  ['**/.vs/**', 'Visual Studio state'],
  ['**/*.user', 'IDE user settings'],
  ['**/*.pdb', 'debug symbols (carry build paths)'],
  ['**/*.ck3', 'CK3 save game'],
  ['**/.env', 'environment file (secrets)'],
  ['**/.env.*', 'environment file (secrets)'],
  ['**/*.pem', 'key material'],
  ['**/*.pfx', 'key material'],
  ['**/*.p12', 'key material'],
  ['**/*.snk', 'strong-name key'],
  ['**/id_rsa*', 'SSH key'],
  ['**/id_ed25519*', 'SSH key'],
];

const BINARY_EXT = new Set(['.dll', '.exe', '.pdb', '.so', '.dylib', '.lib', '.obj', '.o', '.a', '.bin', '.dat', '.sav', '.ck3',
  '.zip', '.7z', '.rar', '.gz', '.tgz', '.tar', '.xz', '.nupkg', '.msi', '.cab', '.pak', '.tpac']);
const IMAGE_EXT = new Set(['.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp', '.ico', '.tga', '.tif', '.tiff', '.dds', '.psd']);
const FONT_EXT = new Set(['.ttf', '.otf', '.woff', '.woff2', '.fnt']);
const MEDIA_EXT = new Set(['.wav', '.ogg', '.mp3', '.flac', '.mp4', '.webm', '.mkv', '.avi', '.mov', '.bik', '.bnk', '.wem']);
const ARCHIVE_EXT = new Set(['.zip']);
// Extensions that look like an e-mail domain's last label but are file names (e.g. "icon@2x.png").
const FILE_TLDS = new Set(['png', 'jpg', 'jpeg', 'gif', 'svg', 'dds', 'tga', 'json', 'xml', 'txt', 'md', 'cs', 'js', 'mjs', 'cjs', 'ts',
  'dll', 'exe', 'pdb', 'log', 'yml', 'yaml', 'gui', 'mod', 'csproj', 'props', 'targets', 'slnx', 'sln', 'zip', 'ico', 'html', 'css']);
// Built-in and obviously made-up profile names that tests and docs use as examples (never a real account name).
const PLACEHOLDER_USERS = /^(Public|Default|Default User|All Users|Jane|John|JaneDoe|JohnDoe|Doe|Name|YourName|Your Name|Example|Someone|Player|Alice|Bob|dev|test|tester)$/i;
const EMAIL_OK_DOMAINS = [/(^|\.)users\.noreply\.github\.com$/i, /(^|\.)example\.(com|org|net)$/i, /\.(invalid|test|localhost|example)$/i];
// Licence notices of redistributed third-party code: they credit their authors with e-mail addresses, which is expected.
// The project rules (personal names and addresses) still apply to them.
const THIRD_PARTY_NOTICES = ['**/THIRD-PARTY-NOTICES.txt', '**/ThirdPartyNotices.txt'];
// Files of the .NET runtime inside a self-contained single-file exe: Microsoft's, not ours, so not scanned for strings.
const DOTNET_RUNTIME_FILE = /^(?:System\.|Microsoft\.|mscorlib\.dll$|netstandard\.dll$|WindowsBase\.dll$|Accessibility\.dll$|PresentationCore\.dll$|PresentationFramework[\w.-]*\.dll$|PresentationUI\.dll$|ReachFramework\.dll$|UIAutomation\w*\.dll$|DirectWriteForwarder\.dll$|WindowsFormsIntegration\.dll$|PenImc_cor3\.dll$|PresentationNative_cor3\.dll$|wpfgfx_cor3\.dll$|D3DCompiler_47_cor3\.dll$|vcruntime140_cor3\.dll$|api-ms-win-[\w-]+\.dll$|ucrtbase\.dll$|clrjit\.dll$|clrgc\w*\.dll$|coreclr\.dll$|hostfxr\.dll$|hostpolicy\.dll$|mscordaccore\w*\.dll$|mscordbi\.dll$|mscorrc\.dll$|msquic\.dll$|createdump\.exe$|Microsoft\.DiaSymReader\.Native\.amd64\.dll$)/i;

/** A printable run of a binary's strings (one per line) holding the match at [start, end). */
function runAt(text, start, end) {
  const a = text.lastIndexOf('\n', start - 1) + 1;
  const b = text.indexOf('\n', end);
  return text.slice(a, b < 0 ? text.length : b);
}

/** Glob ('**', '*', '?') to an anchored regex over forward-slash paths. */
export function globToRegex(glob) {
  let re = '';
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === '*' && glob[i + 1] === '*') {
      i++;
      if (glob[i + 1] === '/') { i++; re += '(?:.*/)?'; } else re += '.*';
    } else if (c === '*') re += '[^/]*';
    else if (c === '?') re += '[^/]';
    else re += c.replace(/[.+^${}()|[\]\\]/g, '\\$&');
  }
  return new RegExp(`^${re}$`, 'i');
}
const matchesAny = (rel, globs) => globs.some((g) => globToRegex(g).test(rel));

function escapeRe(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }

/** Both spellings of a path (back and forward slashes), as one case-insensitive regex. */
function pathRegex(p) {
  const parts = path.resolve(p).split(/[\\/]+/).filter(Boolean).map(escapeRe);
  return new RegExp(parts.join('[\\\\/]+'), 'gi');
}

// ------------------------------------------------------------------------------------------------ rules over text

function buildRules({ devRoots = [], devPaths = [], terms = {} } = {}) {
  const rules = [];
  const add = (id, level, why, re, extra = {}) => rules.push({ id, level, why, re, binary: true, ...extra });

  add('user-path', 'fail', 'drive path into a Windows user profile',
    /\b[A-Za-z]:[\\/]{1,2}(?:Users|Documents and Settings)[\\/]{1,2}([^\\/\s"'<>|:*?%$`{}()[\]]+)/gi,
    { skip: (m) => PLACEHOLDER_USERS.test(m[1]) });
  add('user-path', 'fail', 'home folder path (Linux/macOS)',
    /(?<![\w.~-])\/(?:home|Users)\/([a-z_][\w.-]*)\//g,
    { skip: (m) => /^(user|username|you|yourname|me|runner|example|shared)$/i.test(m[1]) });
  const home = os.homedir();
  if (home && home.length > 3) add('user-path', 'fail', 'this machine\'s home folder', pathRegex(home));

  for (const root of devRoots) {
    add('dev-root', 'fail', 'development folder path', pathRegex(root));
    const base = path.basename(path.resolve(root));
    if (base.length >= 8) add('dev-root', 'fail', 'development folder name', new RegExp(`\\b${escapeRe(base)}\\b`, 'gi'));
  }
  for (const p of devPaths) add('dev-root', 'fail', 'build or export folder path', pathRegex(p));

  add('temp-path', 'fail', 'agent or temp working folder', new RegExp(w('scratch', 'pad'), 'gi'));
  add('temp-path', 'fail', 'agent or temp working folder', /AppData[\\/]+Local[\\/]+Temp\b/gi);

  add('email', 'fail', 'e-mail address other than a GitHub noreply address',
    /[A-Za-z0-9._%+-]+@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.([A-Za-z]{2,}))\b/g,
    {
      skip: (m) => FILE_TLDS.has(m[2].toLowerCase()) || EMAIL_OK_DOMAINS.some((re) => re.test(m[1])),
      // In a binary, an address is a real string: it sits in a printable run of 8+ characters and ends in a lowercase
      // top-level domain. Shorter or mixed-case hits are chance byte patterns (compressed data, machine code).
      binarySkip: (m, text) => m[2] !== m[2].toLowerCase() || runAt(text, m.index, m.index + m[0].length).length < 8,
      skipFiles: THIRD_PARTY_NOTICES,
    });

  const secret = (why, re) => add('secret', 'fail', why, re);
  secret('private key block', /-----BEGIN [A-Z ]*PRIVATE KEY-----/g);
  secret('GitHub token', /\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b/g);
  secret('GitHub fine-grained token', /\bgithub_pat_[A-Za-z0-9_]{22,}/g);
  secret('Anthropic API key', /\bsk-ant-[A-Za-z0-9_-]{20,}/g);
  secret('OpenAI-style API key', /\bsk-(?:proj-)?[A-Za-z0-9]{32,}\b/g);
  secret('AWS access key id', /\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/g);
  secret('Google API key', /\bAIza[0-9A-Za-z_-]{35}\b/g);
  secret('Slack token', /\bxox[abprs]-[0-9A-Za-z-]{10,}/g);
  secret('fal.ai key', /\bFAL_KEY\s*=\s*\S{8,}/g);
  secret('Steam Web API key', /\bkey=[0-9A-F]{32}\b/g);
  add('secret-like', 'warn', 'value assigned to a secret-looking name',
    /\b(?:api[_-]?key|secret|access[_-]?token|auth[_-]?token|password|passwd)\b\s*[:=]\s*["'][^"'\s]{12,}["']/gi);

  const decomp = (why, re) => add('game-code', 'fail', why, re, { binary: false });
  decomp('disassembler output header', new RegExp(w('De', 'compiled with'), 'gi'));
  decomp('disassembler tool name', new RegExp(`\\b(?:${w('IL', 'Spy')}|${w('dn', 'Spy')}|${w('dot', 'Peek')}|${w('Just', 'De', 'compile')}|${w('de4', 'dot')})\\b`, 'gi'));
  decomp(`mention of ${w('de', 'compiled')} code`, new RegExp(`\\b${w('de', 'compil')}(?:ed|er|ers|ing|e|ation)\\b`, 'gi'));
  decomp('disassembler metadata line', new RegExp(`^\\s*//\\s*(?:${w('MV', 'ID')}|Assembly location|${w('Assembly', ':')})`, 'gm'));
  decomp('compiler-generated name', new RegExp(`<>c__${w('Display', 'Class')}|\\bgoto ${w('IL', '_')}[0-9a-f]{4}\\b|\\b${w('FUN', '_')}[0-9a-f]{8}\\b`, 'g'));
  add('game-namespace', 'fail', 'C# code declared in a game namespace (copied game code?)',
    /^\s*namespace\s+(?:TaleWorlds|SandBox|StoryMode|Helpers)\b/gm, { binary: false, ext: ['.cs'] });

  add('todo', 'warn', 'open work marker', new RegExp(`\\b(?:${w('TO', 'DO')}|${w('FIX', 'ME')}|${w('HA', 'CK')}:)`, 'g'), { binary: false });
  add('machine-wording', 'warn', 'machine-specific wording', new RegExp(`\\b(?:${w('this ', 'PC')}|on my (?:PC|machine|computer))\\b`, 'gi'), { binary: false });

  for (const [list, level] of [[terms.forbidden_patterns, 'fail'], [terms.warn_patterns, 'warn']]) {
    for (const t of list || []) {
      add('project-rule', level, t.why || 'project rule', new RegExp(t.pattern, (t.flags || 'g').includes('g') ? (t.flags || 'g') : `${t.flags}g`),
        t.skip_files ? { skipFiles: t.skip_files } : {});
    }
  }
  const allow = (terms.allow_matches || []).map((a) => new RegExp(a.pattern, (a.flags || 'gi').includes('g') ? (a.flags || 'gi') : `${a.flags}g`));
  return { rules, allow };
}

/** Line number of a character offset. */
function lineAt(text, index) {
  let n = 1;
  for (let i = text.indexOf('\n'); i >= 0 && i < index; i = text.indexOf('\n', i + 1)) n++;
  return n;
}

function snippet(s) {
  const one = s.replace(/\s+/g, ' ').trim();
  return one.length > 90 ? `${one.slice(0, 87)}...` : one;
}

/** Spans of the allow_matches patterns in a text (a finding fully inside one of them is not reported). */
function allowSpans(text, allow) {
  const spans = [];
  for (const re of allow) {
    re.lastIndex = 0;
    for (let m; (m = re.exec(text));) { spans.push([m.index, m.index + m[0].length]); if (!m[0]) re.lastIndex++; }
  }
  return spans;
}

function scanText(rel, text, { rules, allow }, { binary, report }) {
  const ext = path.extname(rel).toLowerCase();
  const base = rel.split('!/').pop();
  const spans = allow.length ? allowSpans(text, allow) : [];
  for (const rule of rules) {
    if (binary && !rule.binary) continue;
    if (rule.ext && !rule.ext.includes(ext)) continue;
    if (rule.skipFiles && matchesAny(base, rule.skipFiles)) continue;
    rule.re.lastIndex = 0;
    for (let m; (m = rule.re.exec(text));) {
      if (!m[0]) { rule.re.lastIndex++; continue; }
      if (rule.skip && rule.skip(m)) continue;
      if (binary && rule.binarySkip && rule.binarySkip(m, text)) continue;
      const end = m.index + m[0].length;
      if (spans.some(([a, b]) => a <= m.index && end <= b)) continue;
      report(rule.level, rule.id, rel, binary ? null : lineAt(text, m.index), rule.why, snippet(m[0]));
    }
  }
}

/** Printable strings of a binary (ASCII and UTF-16LE runs of 5+ characters), one per line. */
function binaryStrings(buf) {
  const out = [];
  let cur = '';
  for (let i = 0; i < buf.length; i++) {
    const b = buf[i];
    if (b >= 0x20 && b < 0x7f) cur += String.fromCharCode(b);
    else { if (cur.length >= 5) out.push(cur); cur = ''; }
  }
  if (cur.length >= 5) out.push(cur);
  for (const start of [0, 1]) {
    cur = '';
    for (let i = start; i + 1 < buf.length; i += 2) {
      const c = buf[i] | (buf[i + 1] << 8);
      if (c >= 0x20 && c < 0x7f) cur += String.fromCharCode(c);
      else { if (cur.length >= 5) out.push(cur); cur = ''; }
    }
    if (cur.length >= 5) out.push(cur);
  }
  return out.join('\n');
}

const looksBinary = (buf) => buf.subarray(0, 8192).includes(0);

// ------------------------------------------------------------------------------------------------ game-install check

function walkFiles(root, onFile) {
  const stack = [root];
  while (stack.length) {
    const dir = stack.pop();
    let ents;
    try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch { continue; }
    for (const e of ents) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) stack.push(p);
      else if (e.isFile()) onFile(p);
    }
  }
}

/** Game install roots: explicit, else auto-detected with tools/gen/lib/game_dirs.cjs (when it is next to this tool). */
export function detectInstalls({ ck3, bl } = {}) {
  const roots = [];
  let helper = null;
  const helperPath = path.resolve(HERE, '..', 'gen', 'lib', 'game_dirs.cjs');
  if ((!ck3 || !bl) && fs.existsSync(helperPath)) helper = createRequire(import.meta.url)(helperPath);
  const ck3Root = ck3 || (helper && helper.ck3RootDir({ required: false }));
  const blRoot = bl || (helper && helper.bannerlordDir({ required: false }));
  if (ck3Root && fs.existsSync(ck3Root)) roots.push({ game: 'CK3', root: ck3Root });
  if (blRoot && fs.existsSync(blRoot)) roots.push({ game: 'Bannerlord', root: blRoot });
  return roots;
}

function installHashCheck(entries, installs, { minHashBytes, report }) {
  const bySize = new Map();
  for (const e of entries) {
    if (e.data.length < minHashBytes) continue;
    const list = bySize.get(e.data.length) || [];
    list.push(e);
    bySize.set(e.data.length, list);
  }
  const stats = { walked: 0, hashed: 0 };
  if (!bySize.size) return stats;
  const sha = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
  const ours = new Map();
  for (const list of bySize.values()) for (const e of list) ours.set(e, sha(e.data));
  // A previous install of our own modules lives inside the Bannerlord tree: its files are ours, not the game's. (CK3Bridge.Elephants
  // is built from Burning Empires' files on the player's machine and stays scanned.)
  const ownInstall = /^Modules\/CK3Bridge(\.Naval)?\//i;
  for (const { game, root } of installs) {
    walkFiles(root, (file) => {
      if (game === 'Bannerlord' && ownInstall.test(path.relative(root, file).replace(/\\/g, '/'))) return;
      stats.walked++;
      let size;
      try { size = fs.statSync(file).size; } catch { return; }
      const list = bySize.get(size);
      if (!list) return;
      stats.hashed++;
      let h;
      try { h = sha(fs.readFileSync(file)); } catch { return; }
      for (const e of list) {
        if (ours.get(e) === h) report('fail', 'game-file', e.rel, null, `byte-identical to a ${game} install file`, path.relative(root, file).replace(/\\/g, '/'));
      }
    });
  }
  return stats;
}

// ------------------------------------------------------------------------------------------------ scanner

/**
 * Scans in-memory entries.
 * @param {{rel: string, data: Buffer}[]} entries  paths relative to the scanned root, forward slashes
 * @param {object} options  see DEFAULTS, plus devRoots, devPaths, terms, installs ([{game, root}] or null to skip)
 * @returns {{findings: object[], failures: number, warnings: number, stats: object}}
 */
export function scanEntries(entries, options = {}) {
  const opt = { ...DEFAULTS, ...options };
  const compiled = buildRules(opt);
  const findings = [];
  const report = (level, rule, file, line, why, text) => findings.push({ level, rule, file, line, why, text });
  const all = [];
  const stats = { files: 0, bytes: 0, archives: 0, binaries: 0, runtimeFiles: 0, walked: 0, hashed: 0, installs: [] };

  const forbiddenFiles = [...FORBIDDEN_FILES, ...((opt.terms && opt.terms.forbidden_files) || []).map((f) => [f.glob, f.why || 'project-private file'])];
  const visit = (rel, data, depth) => {
    stats.files++;
    stats.bytes += data.length;
    all.push({ rel, data });
    const ext = path.extname(rel).toLowerCase();
    const base = rel.split('!/').pop();
    for (const [glob, why, profile] of forbiddenFiles) {
      if ((!profile || profile === opt.profile) && globToRegex(glob).test(base)) report('fail', 'forbidden-file', rel, null, why, base);
    }
    if (ext === '.log' && !matchesAny(base, opt.allowLogs)) report('fail', 'log-file', rel, null, 'log file (only the synthetic test fixtures may be published)', base);
    if (data.length > opt.warnMb * 1048576) report('warn', 'large-file', rel, null, `larger than ${opt.warnMb} MB`, `${(data.length / 1048576).toFixed(1)} MB`);

    const release = opt.profile === 'release';
    const allowedBinary = matchesAny(base, opt.allowBinary);
    if (IMAGE_EXT.has(ext)) {
      if (data.length > opt.maxImageMb * 1048576) report('fail', 'image', rel, null, `image larger than ${opt.maxImageMb} MB`, `${(data.length / 1048576).toFixed(1)} MB`);
      else report('warn', 'image', rel, null, 'image: check by eye that it shows no desktop, taskbar or personal data', base);
      return;
    }
    if (FONT_EXT.has(ext) && !allowedBinary) { report('fail', 'font', rel, null, 'font file (game fonts must be read from the player\'s install)', base); return; }
    if (MEDIA_EXT.has(ext) && !allowedBinary) { report('fail', 'media', rel, null, 'audio/video file', base); return; }

    // The raw bytes read for strings: an archive inside an exe (an installer payload) is compressed, so it is scanned
    // entry by entry and left out of the exe's own strings, where it would only give chance matches.
    let raw = [data];
    let zipEntries = null;
    if (release && depth < 3 && (ARCHIVE_EXT.has(ext) || ext === '.exe')) {
      try {
        zipEntries = readZip(data);
        const { start, end } = locateZip(data);
        raw = [data.subarray(0, start), data.subarray(end)];
      } catch (e) { if (ARCHIVE_EXT.has(ext)) report('fail', 'archive', rel, null, `unreadable archive: ${e.message}`, base); }
    }
    if (zipEntries) {
      stats.archives++;
      for (const z of zipEntries) visit(`${rel}!/${z.name}`, z.data, depth + 1);
      if (ext === '.zip') return;
    }
    // A .NET single-file exe: our files in it are read one by one (they may be deflated); the runtime's are Microsoft's.
    let bundle = null;
    if (release && depth < 3 && ext === '.exe' && !zipEntries) {
      try { bundle = readBundle(data); } catch (e) { report('warn', 'archive', rel, null, `single-file bundle not readable, scanned as raw bytes: ${e.message}`, base); }
    }
    if (bundle) {
      stats.archives++;
      for (const f of bundle.files) {
        if (DOTNET_RUNTIME_FILE.test(f.name)) { stats.runtimeFiles++; continue; }
        let d;
        try { d = f.read(); } catch (e) { report('fail', 'archive', `${rel}!/${f.name}`, null, `unreadable bundled file: ${e.message}`, f.name); continue; }
        visit(`${rel}!/${f.name}`, d, depth + 1);
      }
      raw = [data.subarray(0, bundle.hostEnd)];      // the apphost; the bundled files were handled above
    }
    const isBinary = BINARY_EXT.has(ext) || looksBinary(data);
    if (isBinary) {
      stats.binaries++;
      if (!release && !allowedBinary) report('fail', 'binary', rel, null, 'binary file in the source tree', base);
      scanText(rel, raw.map(binaryStrings).join('\n'), compiled, { binary: true, report });
      return;
    }
    let text = data.toString('utf8');
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    scanText(rel, text, compiled, { binary: false, report });
  };

  for (const e of [...entries].sort((a, b) => (a.rel < b.rel ? -1 : a.rel > b.rel ? 1 : 0))) visit(e.rel, e.data, 0);

  if (opt.installs && opt.installs.length) {
    stats.installs = opt.installs.map((i) => i.game);
    Object.assign(stats, installHashCheck(all, opt.installs, { minHashBytes: opt.minHashBytes, report }));
  } else if (opt.installs !== null) {
    report(opt.requireInstalls ? 'fail' : 'warn', 'game-file', '(all files)', null, 'no game install found: byte-identical check skipped (use --ck3/--bl)', '');
  }

  findings.sort((a, b) => (a.level === b.level ? 0 : a.level === 'fail' ? -1 : 1) || a.rule.localeCompare(b.rule) || a.file.localeCompare(b.file) || (a.line || 0) - (b.line || 0));
  return {
    findings,
    failures: findings.filter((f) => f.level === 'fail').length,
    warnings: findings.filter((f) => f.level === 'warn').length,
    stats,
  };
}

/** Prints a scan result; returns the console lines. */
export function formatReport(result, { maxLines = DEFAULTS.maxLines, title = '' } = {}) {
  const lines = [];
  const { stats } = result;
  if (title) lines.push(title);
  lines.push(`scanned ${stats.files} file(s), ${(stats.bytes / 1048576).toFixed(1)} MB; ${stats.binaries} binary, ${stats.archives} archive(s)${stats.runtimeFiles ? `; ${stats.runtimeFiles} bundled .NET runtime file(s) not scanned` : ''}`);
  lines.push(stats.installs.length
    ? `game-install check: ${stats.installs.join(' + ')}, ${stats.walked} install file(s) walked, ${stats.hashed} same-size file(s) hashed`
    : 'game-install check: not run');
  const groups = new Map();
  for (const f of result.findings) {
    const key = `${f.level} ${f.rule}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(f);
  }
  for (const [key, list] of groups) {
    const [level, rule] = key.split(' ');
    lines.push(`${level === 'fail' ? 'FAIL' : 'WARN'}  ${rule} (${list.length})`);
    for (const f of list.slice(0, maxLines)) {
      lines.push(`      ${f.file}${f.line ? `:${f.line}` : ''}  ${f.why}${f.text ? `: ${f.text}` : ''}`);
    }
    if (list.length > maxLines) lines.push(`      ... and ${list.length - maxLines} more`);
  }
  lines.push(`result: ${result.failures} failure(s), ${result.warnings} warning(s) -> ${result.failures ? 'FAIL' : 'PASS'}`);
  return lines;
}

/** Loads the optional project-private rules. */
export function loadTerms(file) {
  if (!file) {
    const def = path.join(HERE, 'public_allowlist.json');
    if (!fs.existsSync(def)) return { terms: {}, source: null };
    file = def;
  }
  const json = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
  return {
    terms: {
      forbidden_patterns: json.forbidden_patterns || [],
      warn_patterns: json.warn_patterns || [],
      allow_matches: json.allow_matches || [],
      forbidden_files: json.forbidden_files || [],
    },
    source: file,
  };
}

/** Entries of a folder (bin/obj and .git skipped only when asked) or of a single file. */
export function collectEntries(target, { skipDirs = ['.git'] } = {}) {
  const st = fs.statSync(target);
  if (st.isFile()) return [{ rel: path.basename(target), data: fs.readFileSync(target) }];
  const out = [];
  const stack = [''];
  while (stack.length) {
    const relDir = stack.pop();
    for (const e of fs.readdirSync(path.join(target, relDir), { withFileTypes: true })) {
      const rel = relDir ? `${relDir}/${e.name}` : e.name;
      if (e.isDirectory()) { if (!skipDirs.includes(e.name)) stack.push(rel); } else if (e.isFile()) out.push({ rel, data: fs.readFileSync(path.join(target, rel)) });
    }
  }
  return out;
}

// ------------------------------------------------------------------------------------------------ CLI

function parseArgs(argv) {
  const o = { devRoots: [], devPaths: [], allowBinary: [], allowLogs: [], target: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const val = () => { if (i + 1 >= argv.length) throw new Error(`${a} needs a value`); return argv[++i]; };
    switch (a) {
      case '--profile': o.profile = val(); break;
      case '--terms': o.termsFile = val(); break;
      case '--dev-root': o.devRoots.push(val()); break;
      case '--dev-path': o.devPaths.push(val()); break;
      case '--ck3': o.ck3 = val(); break;
      case '--bl': o.bl = val(); break;
      case '--no-install-hash': o.noInstallHash = true; break;
      case '--require-installs': o.requireInstalls = true; break;
      case '--allow-binary': o.allowBinary.push(val()); break;
      case '--allow-log': o.allowLogs.push(val()); break;
      case '--max-image-mb': o.maxImageMb = Number(val()); break;
      case '--warn-mb': o.warnMb = Number(val()); break;
      case '--max-lines': o.maxLines = Number(val()); break;
      case '--json': o.json = val(); break;
      case '-h': case '--help': o.help = true; break;
      default:
        if (a.startsWith('-')) throw new Error(`unknown option ${a}`);
        if (o.target) throw new Error('only one folder or file can be scanned');
        o.target = a;
    }
  }
  if (o.profile && !['repo', 'release'].includes(o.profile)) throw new Error('--profile must be repo or release');
  return o;
}

function main() {
  let o;
  try { o = parseArgs(process.argv.slice(2)); } catch (e) { console.error(`leak_scan: ${e.message}`); process.exit(2); }
  if (o.help || !o.target) {
    const lines = fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split(/\r?\n/).slice(1);
    console.log(lines.slice(0, lines.findIndex((l) => !l.startsWith('//'))).map((l) => l.slice(3)).join('\n'));
    process.exit(o.help ? 0 : 2);
  }
  if (!fs.existsSync(o.target)) { console.error(`leak_scan: not found: ${o.target}`); process.exit(2); }
  const { terms, source } = loadTerms(o.termsFile);
  const installs = o.noInstallHash ? null : detectInstalls({ ck3: o.ck3, bl: o.bl });
  const entries = collectEntries(o.target);
  const result = scanEntries(entries, {
    profile: o.profile || DEFAULTS.profile,
    devRoots: o.devRoots,
    devPaths: o.devPaths,
    terms,
    installs,
    requireInstalls: !!o.requireInstalls,
    allowBinary: o.allowBinary,
    allowLogs: o.allowLogs.length ? o.allowLogs : DEFAULTS.allowLogs,
    ...(o.maxImageMb ? { maxImageMb: o.maxImageMb } : {}),
    ...(o.warnMb ? { warnMb: o.warnMb } : {}),
  });
  const title = `leak_scan: ${path.resolve(o.target)} (profile ${o.profile || DEFAULTS.profile}; project rules: ${source ? path.basename(source) : 'none'})`;
  for (const line of formatReport(result, { maxLines: o.maxLines || DEFAULTS.maxLines, title })) console.log(line);
  if (o.json) fs.writeFileSync(o.json, JSON.stringify({ target: path.resolve(o.target), ...result }, null, 1));
  process.exit(result.failures ? 1 : 0);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
