// Minimal Paradox (Jomini) script reader shared by gen_ck3_mod.mjs and lint_ck3_mod.mjs.
// It tokenizes and parses CK3 script / GUI files into a plain tree, and reads key sets out of the
// user's CK3 install (read-only) so generated and hand-written mod code can be validated against it.

import fs from 'node:fs';
import path from 'node:path';
import gameDirs from './game_dirs.cjs';

export const BOM = '\uFEFF';

/** CK3 game folder: env CK3_GAME_DIR, else found in the Steam libraries (tools/gen/lib/game_dirs.cjs). */
export function gameDir() {
  return gameDirs.ck3GameDir();
}

/** Folder with CK3's script_docs dumps: env CK3XBL_SCRIPT_DOCS, else the CK3 logs folder (see game_dirs.cjs). */
export function scriptDocsDir(options) {
  return gameDirs.scriptDocsDir(options);
}

/** Bannerlord root: env BL_DIR, else found in the Steam libraries. */
export function bannerlordDir() {
  return gameDirs.bannerlordDir();
}

export function readText(file) {
  return fs.readFileSync(file, 'utf8');
}

export function stripBom(text) {
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

const OPERATORS = ['?=', '<=', '>=', '!=', '==', '=', '<', '>'];

/**
 * Splits script text into tokens: {type: 'word'|'string'|'op'|'open'|'close', value, line}.
 * Comments ('#' to end of line, outside strings) are dropped. Unterminated strings are reported via `errors`.
 */
export function tokenize(text, errors = []) {
  const src = stripBom(text);
  const tokens = [];
  let i = 0;
  let line = 1;
  while (i < src.length) {
    const c = src[i];
    if (c === '\n') { line++; i++; continue; }
    if (c === ' ' || c === '\t' || c === '\r') { i++; continue; }
    if (c === '#') {
      while (i < src.length && src[i] !== '\n') i++;
      continue;
    }
    if (c === '{') { tokens.push({ type: 'open', value: c, line }); i++; continue; }
    if (c === '}') { tokens.push({ type: 'close', value: c, line }); i++; continue; }
    if (c === '"') {
      const start = line;
      let j = i + 1;
      let value = '';
      while (j < src.length && src[j] !== '"') {
        if (src[j] === '\\' && j + 1 < src.length) { value += src[j + 1]; j += 2; continue; }
        if (src[j] === '\n') line++;
        value += src[j];
        j++;
      }
      if (j >= src.length) errors.push({ line: start, message: 'unterminated string' });
      tokens.push({ type: 'string', value, line: start });
      i = j + 1;
      continue;
    }
    const op = OPERATORS.find((o) => src.startsWith(o, i));
    if (op) { tokens.push({ type: 'op', value: op, line }); i += op.length; continue; }
    let j = i;
    while (j < src.length && !/[\s{}"#=<>!?]/.test(src[j])) j++;
    if (j === i) {
      // A lone '!' or '?' that is not part of an operator.
      errors.push({ line, message: `unexpected character '${c}'` });
      i++;
      continue;
    }
    tokens.push({ type: 'word', value: src.slice(i, j), line });
    i = j;
  }
  return tokens;
}

/**
 * Parses tokens into nodes. A node is {key, op, value, block, line}:
 *  - `key = value`          -> {key, op: '=', value: 'value'}
 *  - `key = { ... }`        -> {key, op: '=', block: [...children]}
 *  - bare item in a list    -> {key: null, value: 'item'} or {key: null, block: [...]} for `{ ... }` items
 * Unbalanced braces are reported via `errors` ({line, message}).
 */
export function parse(tokens, errors = []) {
  let pos = 0;
  function parseBlock(openLine) {
    const nodes = [];
    while (pos < tokens.length) {
      const tok = tokens[pos];
      if (tok.type === 'close') {
        if (openLine === null) {
          errors.push({ line: tok.line, message: 'unmatched closing brace' });
          pos++;
          continue;
        }
        pos++;
        return nodes;
      }
      if (tok.type === 'open') {
        pos++;
        nodes.push({ key: null, block: parseBlock(tok.line), line: tok.line });
        continue;
      }
      if (tok.type === 'op') {
        errors.push({ line: tok.line, message: `operator '${tok.value}' without a key` });
        pos++;
        continue;
      }
      const next = tokens[pos + 1];
      if (next && next.type === 'op') {
        const valueTok = tokens[pos + 2];
        pos += 2;
        if (!valueTok) {
          errors.push({ line: tok.line, message: `missing value after '${tok.value} ${next.value}'` });
          break;
        }
        if (valueTok.type === 'open') {
          pos++;
          nodes.push({ key: tok.value, keyType: tok.type, op: next.value, block: parseBlock(valueTok.line), line: tok.line });
        } else if (valueTok.type === 'word' || valueTok.type === 'string') {
          pos++;
          nodes.push({ key: tok.value, keyType: tok.type, op: next.value, value: valueTok.value, valueType: valueTok.type, line: tok.line });
        } else {
          errors.push({ line: valueTok.line, message: `unexpected '${valueTok.value}' after '${tok.value} ${next.value}'` });
          pos++;
        }
        continue;
      }
      pos++;
      nodes.push({ key: null, value: tok.value, valueType: tok.type, line: tok.line });
    }
    if (openLine !== null) errors.push({ line: openLine, message: 'unclosed brace opened here' });
    return nodes;
  }
  return parseBlock(null);
}

export function parseText(text, errors = []) {
  return parse(tokenize(text, errors), errors);
}

/** Lists files with one of `exts` under `dir` (recursive when `recursive`), sorted for determinism. */
export function listFiles(dir, exts, recursive = false) {
  if (!fs.existsSync(dir)) return [];
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (recursive) out.push(...listFiles(full, exts, true));
    } else if (exts.some((e) => entry.name.endsWith(e))) {
      out.push(full);
    }
  }
  return out.sort();
}

/** Top-level `key = { ... }` names of every script file in `dir` (game data is read-only). */
export function topLevelBlockKeys(dir, { recursive = false, exts = ['.txt'] } = {}) {
  const keys = new Set();
  for (const file of listFiles(dir, exts, recursive)) {
    for (const node of parseText(readText(file))) {
      if (node.key && node.block && !node.key.startsWith('@')) keys.add(node.key);
    }
  }
  return keys;
}

/** Top-level `key = ...` names of every script file in `dir` (blocks and plain values). */
export function topLevelKeys(dir, { recursive = false, exts = ['.txt'] } = {}) {
  const keys = new Set();
  for (const file of listFiles(dir, exts, recursive)) {
    for (const node of parseText(readText(file))) {
      if (node.key && !node.key.startsWith('@')) keys.add(node.key);
    }
  }
  return keys;
}

/** Visits every node depth-first; `visit(node, parents)` receives the ancestor chain (outermost first). */
export function walk(nodes, visit, parents = []) {
  for (const node of nodes) {
    visit(node, parents);
    if (node.block) walk(node.block, visit, [...parents, node]);
  }
}

/** Writes `text` with a UTF-8 BOM, only when the content changed. Returns true when the file was written. */
export function writeWithBom(file, text, { dryRun = false } = {}) {
  const content = BOM + text;
  const old = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
  if (old === content) return false;
  if (!dryRun) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, content, 'utf8');
  }
  return true;
}
