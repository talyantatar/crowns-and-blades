// Minimal Paradox (Jomini/Clausewitz) script parser.
// Produces a tree: Block = { items: Item[] }, Item = { k?, op?, v, line }
// v is a string (scalar) or a Block; tagged blocks like `hsv { ... }` become { tag, items }.
import fs from 'node:fs';
import path from 'node:path';

export function tokenize(text) {
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  const toks = [];
  const n = text.length;
  let i = 0, line = 1;
  while (i < n) {
    const c = text[i];
    if (c === '\n') { line++; i++; continue; }
    if (c === ' ' || c === '\t' || c === '\r' || c === '\f' || c === '\v' || c === '﻿') { i++; continue; }
    if (c === '#') { while (i < n && text[i] !== '\n') i++; continue; }
    if (c === '{' || c === '}') { toks.push({ t: c, line }); i++; continue; }
    if (c === '"') {
      let j = i + 1; const startLine = line;
      while (j < n && text[j] !== '"') { if (text[j] === '\\') j++; if (text[j] === '\n') line++; j++; }
      toks.push({ t: 'word', v: text.slice(i + 1, j), quoted: true, line: startLine });
      i = j + 1; continue;
    }
    if (c === '=' || c === '<' || c === '>' || c === '!' || (c === '?' && text[i + 1] === '=')) {
      let op = c;
      if (text[i + 1] === '=') op += '=';
      i += op.length;
      toks.push({ t: 'op', v: op, line });
      continue;
    }
    if (c === '@' && text[i + 1] === '[') {
      let j = i + 2, depth = 1;
      while (j < n && depth > 0) { if (text[j] === '[') depth++; else if (text[j] === ']') depth--; j++; }
      toks.push({ t: 'word', v: text.slice(i, j), line });
      i = j; continue;
    }
    let j = i;
    while (j < n) {
      const d = text[j];
      if (d === ' ' || d === '\t' || d === '\r' || d === '\n' || d === '{' || d === '}' || d === '=' || d === '<' || d === '>' || d === '!' || d === '#' || d === '"') break;
      if (d === '?' && text[j + 1] === '=') break;
      j++;
    }
    if (j === i) { i++; continue; } // safety
    toks.push({ t: 'word', v: text.slice(i, j), line });
    i = j;
  }
  return toks;
}

const TAGS = new Set(['hsv', 'rgb', 'hsv360', 'LIST', 'list']);

export function parseTokens(toks) {
  let pos = 0;
  function parseBlock(topLevel) {
    const items = [];
    while (pos < toks.length) {
      const t = toks[pos];
      if (t.t === '}') { if (topLevel) { pos++; continue; } pos++; return { items }; }
      if (t.t === '{') {
        pos++;
        const b = parseBlock(false);
        items.push({ v: b, line: t.line });
        continue;
      }
      if (t.t === 'op') { pos++; continue; } // stray operator
      // word
      const nx = toks[pos + 1];
      if (nx && nx.t === 'op') {
        pos += 2;
        const vt = toks[pos];
        if (!vt) break;
        let v;
        if (vt.t === '{') { pos++; v = parseBlock(false); }
        else if (vt.t === 'word') {
          pos++;
          if (TAGS.has(vt.v) && toks[pos] && toks[pos].t === '{') {
            pos++; const b = parseBlock(false); v = { tag: vt.v, items: b.items };
          } else v = vt.v;
        } else { v = ''; }
        items.push({ k: t.v, op: nx.v, v, line: t.line });
      } else {
        pos++;
        if (TAGS.has(t.v) && nx && nx.t === '{') { pos++; const b = parseBlock(false); items.push({ v: { tag: t.v, items: b.items }, line: t.line }); }
        else items.push({ v: t.v, line: t.line });
      }
    }
    return { items };
  }
  return parseBlock(true);
}

export function parseFile(file) {
  const text = fs.readFileSync(file, 'utf8');
  return parseTokens(tokenize(text));
}

export const isBlock = (v) => v && typeof v === 'object' && Array.isArray(v.items);
export const get = (b, k) => { if (!isBlock(b)) return undefined; const it = b.items.find((x) => x.k === k); return it ? it.v : undefined; };
export const getItem = (b, k) => (isBlock(b) ? b.items.find((x) => x.k === k) : undefined);
export const getAll = (b, k) => (isBlock(b) ? b.items.filter((x) => x.k === k).map((x) => x.v) : []);
export const values = (b) => (isBlock(b) ? b.items.filter((x) => x.k === undefined && !isBlock(x.v)).map((x) => x.v) : []);

export function vars(root) {
  const m = {};
  for (const it of root.items) if (it.k && it.k.startsWith('@')) m[it.k] = it.v;
  return m;
}

export function num(v, varmap) {
  if (v === undefined) return undefined;
  if (typeof v === 'string' && v.startsWith('@') && varmap && varmap[v] !== undefined) v = varmap[v];
  if (typeof v === 'string' && /^-?\d+(\.\d+)?$/.test(v)) return Number(v);
  return v;
}

export function compact(v, depth = 0) {
  if (!isBlock(v)) return String(v);
  const parts = v.items.map((it) => {
    const val = isBlock(it.v) ? (it.v.tag ? `${it.v.tag} { ${compact(it.v, depth + 1)} }` : `{ ${compact(it.v, depth + 1)} }`) : (/\s/.test(String(it.v)) ? `"${it.v}"` : it.v);
    return it.k !== undefined ? `${it.k} ${it.op} ${val}` : `${val}`;
  });
  return parts.join(' ');
}

// Flatten a block into simple JS objects (duplicate keys -> arrays) for numeric tables
export function toObj(b, varmap) {
  if (!isBlock(b)) return num(b, varmap);
  const o = {};
  const list = [];
  for (const it of b.items) {
    if (it.k === undefined) { list.push(isBlock(it.v) ? toObj(it.v, varmap) : num(it.v, varmap)); continue; }
    const val = isBlock(it.v) ? toObj(it.v, varmap) : num(it.v, varmap);
    if (o[it.k] === undefined) o[it.k] = val;
    else if (Array.isArray(o[it.k]) && o.__multi?.[it.k]) o[it.k].push(val);
    else { o[it.k] = [o[it.k], val]; (o.__multi ??= {})[it.k] = true; }
  }
  delete o.__multi;
  if (list.length && Object.keys(o).length === 0) return list;
  if (list.length) o._values = list;
  return o;
}

// Walk conditions in a trigger block. cb(key, value, ctx) with ctx {negated, path}
export function walkTrigger(b, cb, negated = false, pathArr = []) {
  if (!isBlock(b)) return;
  for (const it of b.items) {
    if (it.k === undefined) continue;
    const k = it.k;
    let neg = negated;
    if (k === 'NOT' || k === 'NOR') neg = !negated;
    if (isBlock(it.v)) {
      cb(k, it.v, { negated, path: pathArr, op: it.op, line: it.line });
      walkTrigger(it.v, cb, neg, [...pathArr, k]);
    } else cb(k, it.v, { negated, path: pathArr, op: it.op, line: it.line });
  }
}

export function listTxt(dir) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir).filter((f) => f.endsWith('.txt')).sort().map((f) => path.join(dir, f));
}
