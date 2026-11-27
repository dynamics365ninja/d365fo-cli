#!/usr/bin/env bash
# Publishes one platform in the layout a release ships: bin/ (the CLI), mcp/ (the MCP server)
# and, for win-x64, D365FO.Bridge/. The release archives and the npm platform packages are made
# from this folder. Usage: scripts/publish-release-layout.sh <rid> <output folder>
set -euo pipefail

rid="${1:?usage: publish-release-layout.sh <rid> <output folder>}"
out="${2:?usage: publish-release-layout.sh <rid> <output folder>}"
cd "$(dirname "$0")/.."

dotnet publish src/D365FO.Cli -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o "$out/bin"
dotnet publish src/D365FO.Mcp -c Release -r "$rid" --self-contained -o "$out/mcp"

# Beside bin/, where the CLI probes for it (../D365FO.Bridge/).
if [ "$rid" = "win-x64" ]; then
  dotnet publish src/D365FO.Bridge -c Release -o "$out/D365FO.Bridge"
fi

cp README.md CHANGELOG.md LICENSE "$out/"
