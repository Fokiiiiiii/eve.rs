#!/usr/bin/env python3
"""Remove stale source comments while checking that code tokens stay unchanged."""

from __future__ import annotations

import argparse
import io
import re
import tokenize
from collections import Counter
from pathlib import Path

META = re.compile(r"owner|decision|\bQ-\d+|\b(?:TODO|FIXME|NOTE|HACK)\b|EveJS|EVE University|\bwiki\b|\b202\d[-/.]|since Version|\bagent\b|\bphase\b|\bslice\b|\bstep\b|test[- ]run|docs/|\(built\)|\(open\)|for now|\b[A-Z]{2,}-\d+\b", re.I)
WHY = re.compile(r"because|otherwise|must|never|invariant|ordering|race|deadlock|overflow|off-by-one|wire|layout|little-endian|SDE attribute|\bunit\b", re.I)
LICENSE = re.compile(r"copyright|licensed under|SPDX-License-Identifier", re.I)


def rust_cs_comments(src: str, csharp: bool) -> list[tuple[int, int, str]]:
    out = []
    i, n = 0, len(src)
    while i < n:
        start = i
        # C# raw strings, including interpolated raw strings.
        if csharp:
            m = re.match(r"\$*@{0,1}(\"{3,})", src[i:])
            if m:
                q = m.group(1)
                end = src.find(q, i + m.end())
                i = n if end < 0 else end + len(q)
                continue
            if src.startswith('@"', i) or src.startswith('$@"', i) or src.startswith('@$"', i):
                j = i + (3 if src.startswith(('$@"', '@$"'), i) else 2)
                while j < n:
                    if src.startswith('""', j): j += 2
                    elif src[j] == '"': j += 1; break
                    else: j += 1
                i = j; continue
            if src[i] == '"' or src[i] == "'":
                q = src[i]; i += 1
                while i < n:
                    if src[i] == '\\': i += 2
                    elif src[i] == q: i += 1; break
                    else: i += 1
                continue
        else:
            # Rust raw strings r###"..."### and byte/C variants.
            m = re.match(r"(?:br|cr|r)(#{0,})\"", src[i:])
            if m:
                closing = '"' + m.group(1)
                end = src.find(closing, i + m.end())
                i = n if end < 0 else end + len(closing)
                continue
            if src[i] == "'" and re.match(r"'(?:\\.|[^'\\])'", src[i:]):
                m = re.match(r"'(?:\\.|[^'\\])'", src[i:])
                i += len(m.group(0))
                continue
            if src[i] in ('"', "'"):
                if src[i] == "'":
                    i += 1
                    continue
                q = src[i]; i += 1
                while i < n:
                    if src[i] == '\\': i += 2
                    elif src[i] == q: i += 1; break
                    else: i += 1
                continue
        if src.startswith('//', i):
            end = src.find('\n', i)
            i = n if end < 0 else end
            out.append((start, i, src[start:i]))
            continue
        if src.startswith('/*', i):
            depth = 1; i += 2
            while i < n and depth:
                if src.startswith('/*', i): depth += 1; i += 2
                elif src.startswith('*/', i): depth -= 1; i += 2
                else: i += 1
            out.append((start, i, src[start:i]))
            continue
        i += 1
    return out


def python_comments(src: str) -> list[tuple[int, int, str]]:
    offsets = [0]
    offsets.extend(i + 1 for i, char in enumerate(src) if char == '\n')
    result = []
    for tok in tokenize.generate_tokens(io.StringIO(src).readline):
        if tok.type == tokenize.COMMENT:
            a = offsets[tok.start[0]-1] + tok.start[1]
            b = offsets[tok.end[0]-1] + tok.end[1]
            result.append((a, b, src[a:b]))
    return result


def clean_text(comment: str) -> str:
    return re.sub(r"^\s*(?://+|/\*+|\*+|#|<!--|-->|\*/)+\s?", "", comment, flags=re.M).replace("*/", "").strip()


def should_remove(comment: str) -> bool:
    text = clean_text(comment)
    if LICENSE.search(text) or re.search(r"\bshortcut\s*:", text, re.I): return False
    if META.search(text): return True
    if re.fullmatch(r"[\s=/_*#-]{3,}", text): return True
    lines = [x.strip() for x in text.splitlines() if x.strip()]
    prose = re.compile(r"\b(?:a|an|and|are|as|at|because|by|for|from|if|in|is|it|of|on|or|that|the|this|to|when|with)\b", re.I)
    if lines and all((x.endswith((';', '{', '}', ')')) and not re.search(r"[.!?]\s", x) and not prose.search(x)) for x in lines): return True
    return len(lines) > (3 if WHY.search(text) else 2)


def comments(src: str, lang: str):
    return python_comments(src) if lang == 'py' else rust_cs_comments(src, lang == 'cs')


def code_tokens(src: str, lang: str):
    spans = comments(src, lang)
    masked = list(src)
    for a, b, _ in spans:
        for j in range(a, b):
            if masked[j] != '\n': masked[j] = ' '
    code = ''.join(masked)
    if lang == 'py':
        return [(t.type, t.string) for t in tokenize.generate_tokens(io.StringIO(code).readline) if t.type not in (tokenize.ENCODING, tokenize.ENDMARKER, tokenize.NL, tokenize.NEWLINE, tokenize.INDENT, tokenize.DEDENT, tokenize.COMMENT)]
    return re.findall(r"(?:\w+)|(?:[^\s\w])", code, re.UNICODE)


def transform(src: str, lang: str):
    spans = comments(src, lang)
    remove = [(a,b,c) for a,b,c in spans if should_remove(c)]
    chars = list(src)
    for a,b,_ in remove:
        for j in range(a,b):
            if chars[j] != '\n': chars[j] = ' '
    result = '\n'.join(line.rstrip(' \t') for line in ''.join(chars).split('\n'))
    result = re.sub(r'\n[ \t]*\n(?:[ \t]*\n)+', '\n\n', result)
    result = re.sub(r'((?:^[ \t]*///[^\n]*\n|^[ \t]*//!.*\n)+)[ \t]*\n+', r'\1', result, flags=re.M)
    result = re.sub(r'(^[ \t]*/\*\*[\s\S]*?\*/[ \t]*\n)[ \t]*\n+', r'\1', result, flags=re.M)
    if code_tokens(src, lang) != code_tokens(result, lang):
        raise ValueError('code token stream changed')
    return result, len(spans), len(remove)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--apply', action='store_true')
    ap.add_argument('paths', nargs='+')
    args = ap.parse_args()
    totals = Counter()
    for item in args.paths:
        p = Path(item)
        files = [p] if p.is_file() else [
            x for x in p.rglob('*')
            if x.is_file()
            and x.suffix in ('.rs', '.cs', '.py')
            and not {'target', 'bin', 'obj', 'golden', 'fixtures'} & set(x.parts)
            and '.generated.' not in x.name.lower()
            and not x.name.lower().endswith('_fixture.py')
        ]
        for f in sorted(files):
            lang = {'.rs':'rs','.cs':'cs','.py':'py'}.get(f.suffix)
            if not lang: continue
            original = f.read_text(encoding='utf-8')
            updated, before, removed = transform(original, lang)
            totals['files'] += 1; totals['comments'] += before; totals['removed'] += removed
            if args.apply and updated != original: f.write_text(updated, encoding='utf-8')
            if removed: print(f'{f}: {before} comments, remove {removed}')
    print(f"files={totals['files']} comments={totals['comments']} removed={totals['removed']}")


if __name__ == '__main__': main()
