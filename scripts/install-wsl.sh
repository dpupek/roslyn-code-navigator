#!/usr/bin/env bash
set -euo pipefail

SERVER_NAME=${1:-roslyn_code_navigator}
LOG_LEVEL=${LOG_LEVEL:-Information}
ROSLYN_LOG_LEVEL=${ROSLYN_LOG_LEVEL:-Debug}

read -r -p "Path to Windows EXE (default: /mnt/c/Program Files/RoslynMcpServer/RoslynMcpServer.exe): " EXE
EXE=${EXE:-/mnt/c/Program\ Files/RoslynMcpServer/RoslynMcpServer.exe}

CONFIG="$HOME/.codex/config.toml"
mkdir -p "$(dirname "$CONFIG")"
touch "$CONFIG"

block=$(cat <<TOML
[mcp_servers.$SERVER_NAME]
command = "$EXE"
args = []
env = { DOTNET_ENVIRONMENT = "Production", LOG_LEVEL = "$LOG_LEVEL", ROSLYN_LOG_LEVEL = "$ROSLYN_LOG_LEVEL" }
startup_timeout_sec = 30
tool_timeout_sec = 120
TOML
)

python3 - "$CONFIG" "$SERVER_NAME" <<'PY'
import sys, re, os
cfg = sys.argv[1]
name = sys.argv[2]
new = sys.stdin.read()
with open(cfg,'r',encoding='utf-8') as f:
    txt = f.read()
pat = re.compile(rf'(?ms)^(\[mcp_servers\.{re.escape(name)}\][\s\S]*?)(?=^\[|\Z)')
if pat.search(txt):
    txt = pat.sub(new, txt)
else:
    if txt and not txt.endswith('\n'):
        txt += '\n'
    txt += new
with open(cfg,'w',encoding='utf-8') as f:
    f.write(txt)
print(f"Updated {cfg} with section [mcp_servers.{name}]")
PY

echo "Done. Start with: codex mcp start $SERVER_NAME"

