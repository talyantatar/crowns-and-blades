// Helpers for reading CK3 script files that use `@name = value` constants and `@[expr]` inline math.
// The shared tokenizer (tools/gen/lib/pdx.mjs) is used read-only; it does not evaluate `@[...]`, so files are
// pre-processed here: constants are collected in file order, every `@[expr]` is replaced by its number, and `@name`
// values left in the tree are resolved by `resolveConst`.

import fs from 'node:fs';
import path from 'node:path';
import { parseText, stripBom } from '../lib/pdx.mjs';

/** Evaluates `a * -1`, `(a + b) / 2`, numbers and constant names (without '@'). Throws on anything else. */
export function evalExpr(expr, consts) {
  const toks = [];
  const re = /\s*(\d+\.\d+|\d+|\.\d+|[A-Za-z_][A-Za-z0-9_]*|[-+*/()])/y;
  let m;
  let pos = 0;
  while (pos < expr.length) {
    re.lastIndex = pos;
    m = re.exec(expr);
    if (!m) {
      if (/^\s*$/.test(expr.slice(pos))) break;
      throw new Error(`bad expression '${expr}'`);
    }
    toks.push(m[1]);
    pos = re.lastIndex;
  }
  let i = 0;
  const peek = () => toks[i];
  const next = () => toks[i++];
  function primary() {
    const t = next();
    if (t === undefined) throw new Error(`unexpected end of '${expr}'`);
    if (t === '(') {
      const v = sum();
      if (next() !== ')') throw new Error(`missing ')' in '${expr}'`);
      return v;
    }
    if (t === '-') return -primary();
    if (t === '+') return primary();
    if (/^[\d.]/.test(t)) return Number(t);
    if (!(t in consts)) throw new Error(`unknown constant '${t}' in '${expr}'`);
    return consts[t];
  }
  function product() {
    let v = primary();
    while (peek() === '*' || peek() === '/') {
      const op = next();
      const r = primary();
      v = op === '*' ? v * r : v / r;
    }
    return v;
  }
  function sum() {
    let v = product();
    while (peek() === '+' || peek() === '-') {
      const op = next();
      const r = product();
      v = op === '+' ? v + r : v - r;
    }
    return v;
  }
  const v = sum();
  if (i !== toks.length) throw new Error(`trailing tokens in '${expr}'`);
  return round6(v);
}

export function round6(x) {
  return Math.round(x * 1e6) / 1e6;
}

/** Reads a script file, evaluates its constants and inline math, and parses it. */
export function readScript(file) {
  const raw = stripBom(fs.readFileSync(file, 'utf8'));
  const consts = {};
  const lines = raw.split(/\r?\n/);
  for (const line of lines) {
    const m = /^\s*@([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([^#]+?)\s*(#.*)?$/.exec(line);
    if (!m) continue;
    const rhs = m[2];
    let v;
    if (rhs.startsWith('@[')) v = evalExpr(rhs.slice(2, rhs.lastIndexOf(']')), consts);
    else if (rhs.startsWith('@')) v = consts[rhs.slice(1)];
    else if (/^-?[\d.]+$/.test(rhs)) v = Number(rhs);
    if (v !== undefined) consts[m[1]] = v;
  }
  const text = raw.replace(/@\[([^\]]*)\]/g, (_, e) => String(evalExpr(e, consts)));
  const errors = [];
  const tree = parseText(text, errors);
  if (errors.length) throw new Error(`${file}: ${errors.map((e) => `${e.line}: ${e.message}`).join('; ')}`);
  return { tree, consts, file };
}

/** Value of a leaf: numbers become numbers, `@const` is resolved, anything else stays a string. */
export function resolveConst(value, consts) {
  if (typeof value !== 'string') return value;
  if (value.startsWith('@')) {
    const k = value.slice(1);
    if (k in consts) return consts[k];
    throw new Error(`unknown constant ${value}`);
  }
  if (/^-?\d+(\.\d+)?$/.test(value)) return Number(value);
  return value;
}

export function child(nodes, key) {
  return nodes?.find((n) => n.key === key);
}

export function children(nodes, key) {
  return (nodes || []).filter((n) => n.key === key);
}

/** `{ a = 1 b = { c = 2 } }` -> plain object (repeated keys become arrays); bare items -> array. */
export function toPlain(nodes, consts) {
  const bare = nodes.filter((n) => n.key === null);
  const keyed = nodes.filter((n) => n.key !== null);
  if (keyed.length === 0) return bare.map((n) => (n.block ? toPlain(n.block, consts) : resolveConst(n.value, consts)));
  const out = {};
  for (const n of keyed) {
    const v = n.block ? toPlain(n.block, consts) : resolveConst(n.value, consts);
    const entry = n.op && n.op !== '=' ? { op: n.op, value: v } : v;
    if (n.key in out) out[n.key] = [].concat(out[n.key], [entry]);
    else out[n.key] = entry;
  }
  if (bare.length) out._items = bare.map((n) => (n.block ? toPlain(n.block, consts) : resolveConst(n.value, consts)));
  return out;
}

/** Simple `key: "text"` reader over every English localization file (first definition wins). */
export function readLocalization(gameDir, wanted) {
  const out = {};
  const root = path.join(gameDir, 'localization', 'english');
  const files = [];
  (function walkDir(d) {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const f = path.join(d, e.name);
      if (e.isDirectory()) walkDir(f);
      else if (e.name.endsWith('.yml')) files.push(f);
    }
  })(root);
  files.sort();
  for (const f of files) {
    const text = stripBom(fs.readFileSync(f, 'utf8'));
    for (const line of text.split(/\r?\n/)) {
      const m = /^\s+([A-Za-z0-9_.\-]+):\d*\s+"(.*)"\s*(#.*)?$/.exec(line);
      if (!m) continue;
      if (wanted && !wanted.has(m[1])) continue;
      if (!(m[1] in out)) out[m[1]] = { text: m[2], file: path.relative(gameDir, f).replace(/\\/g, '/') };
    }
  }
  return out;
}

/** Line number (1-based) of the first line matching `re` in a file, or null. */
export function lineOf(file, re) {
  const lines = stripBom(fs.readFileSync(file, 'utf8')).split(/\r?\n/);
  const i = lines.findIndex((l) => re.test(l));
  return i < 0 ? null : i + 1;
}
