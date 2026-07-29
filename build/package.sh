#!/usr/bin/env bash
# Builds the plugin and assembles a deployable folder for a Jellyfin 10.11.x
# plugin directory (e.g. /config/plugins/Curator_<version> in the container).
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0.0}"
TARGET_ABI="${TARGET_ABI:-10.11.0.0}"
OUT="artifacts/Curator_${VERSION}"

dotnet build Jellyfin.Plugin.Curator/Jellyfin.Plugin.Curator.csproj -c Release -p:Version="${VERSION%.*}"

rm -rf "$OUT"
mkdir -p "$OUT"
cp Jellyfin.Plugin.Curator/bin/Release/net9.0/Jellyfin.Plugin.Curator.dll "$OUT/"

cat > "$OUT/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "LLM-inferred vibe categories surfaced as home screen rows.",
  "guid": "de2b72e7-90f9-47e8-aeef-0436d71d01ac",
  "name": "Curator",
  "overview": "Asks an LLM what your library has in common and builds the answers into ordered playlists.",
  "owner": "nitramivel",
  "targetAbi": "${TARGET_ABI}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": ""
}
EOF

echo "Packaged: $OUT"
