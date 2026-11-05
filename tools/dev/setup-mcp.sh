#!/usr/bin/env bash
# Code-intelligence MCP servers for Claude Code sessions in this repository:
#   codebase-memory-mcp  a graph index of the code (search_graph, trace_path, get_code_snippet, query_graph)
#   serena               language-server symbols and narrow edits (Rust, C#, Python)
#
#   tools/dev/setup-mcp.sh [--index]    install what is missing and register both with Claude Code (user scope)
#
# Safe to run again. A cloud container starts empty, so run it from the environment's setup script (or by hand)
# and restart the Claude session afterwards: a server registered mid-session is not loaded until the next one.
# --index also builds the codebase-memory graph (about a minute and a half).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BIN="${HOME}/.local/bin"
mkdir -p "$BIN"
export PATH="$BIN:$PATH"

if [ ! -x "$BIN/codebase-memory-mcp" ]; then
  command -v codebase-memory-mcp >/dev/null 2>&1 || npm install -g codebase-memory-mcp
  # The installer refuses a binary under a directory another user owns (the global npm prefix here),
  # so it is run from a copy in ~/.local/bin.
  cp "$(npm root -g)/codebase-memory-mcp/bin/codebase-memory-mcp" "$BIN/codebase-memory-mcp"
  ( cd "$HOME" && "$BIN/codebase-memory-mcp" install -y )
fi

command -v serena >/dev/null 2>&1 || uv tool install -p 3.11 serena-agent
claude mcp get serena >/dev/null 2>&1 || serena setup claude-code

# The Serena project (its files live in .serena/, kept out of git locally).
if [ ! -f "$HERE/.serena/project.yml" ]; then
  serena project create "$HERE" --language rust --language csharp --language python
fi
grep -qx '.serena/' "$HERE/.git/info/exclude" 2>/dev/null || echo '.serena/' >> "$HERE/.git/info/exclude"

if [ "${1:-}" = "--index" ]; then
  codebase-memory-mcp cli index_repository --repo-path "$HERE"
fi
echo "ready: restart the Claude session so codebase-memory-mcp and serena are loaded"
