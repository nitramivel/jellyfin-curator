#!/usr/bin/env bash
# Builds a release zip for the Jellyfin plugin catalogue and updates
# manifest.json with the new version entry (including its MD5 checksum).
#
# Usage:   VERSION=0.1.0.0 CHANGELOG="What changed" ./build/release.sh
#
# Afterwards: create a GitHub release with tag v<VERSION> and upload the
# generated artifacts/curator_<VERSION>.zip as an asset — the manifest's
# sourceUrl points at exactly that location.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0.0}"
TARGET_ABI="${TARGET_ABI:-10.11.0.0}"
CHANGELOG="${CHANGELOG:-}"
REPO_URL="https://github.com/nitramivel/jellyfin-curator"

VERSION="$VERSION" TARGET_ABI="$TARGET_ABI" ./build/package.sh

VERSION="$VERSION" TARGET_ABI="$TARGET_ABI" CHANGELOG="$CHANGELOG" REPO_URL="$REPO_URL" \
python3 - <<'PY'
import hashlib
import json
import os
import zipfile
from datetime import datetime, timezone

version = os.environ["VERSION"]
target_abi = os.environ["TARGET_ABI"]
changelog = os.environ["CHANGELOG"]
repo_url = os.environ["REPO_URL"]

folder = f"artifacts/Curator_{version}"
zip_path = f"artifacts/curator_{version}.zip"

# Jellyfin expects the plugin files at the ROOT of the zip; the server
# creates the plugins/<Name>_<version> folder itself on install.
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for name in sorted(os.listdir(folder)):
        zf.write(os.path.join(folder, name), arcname=name)

with open(zip_path, "rb") as f:
    checksum = hashlib.md5(f.read()).hexdigest()

entry = {
    "version": version,
    "changelog": changelog,
    "targetAbi": target_abi,
    "sourceUrl": f"{repo_url}/releases/download/v{version}/curator_{version}.zip",
    "checksum": checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

manifest_path = "manifest.json"
if os.path.exists(manifest_path):
    with open(manifest_path) as f:
        manifest = json.load(f)
else:
    manifest = [
        {
            "guid": "de2b72e7-90f9-47e8-aeef-0436d71d01ac",
            "name": "Curator",
            "description": "Asks an LLM what your library has in common and builds the answers into ordered playlists surfaced as home screen rows.",
            "overview": "LLM-inferred vibe categories for your Jellyfin library.",
            "owner": "nitramivel",
            "category": "General",
            "imageUrl": "",
            "versions": [],
        }
    ]

versions = [v for v in manifest[0]["versions"] if v["version"] != version]
versions.insert(0, entry)
manifest[0]["versions"] = versions

with open(manifest_path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print(f"Zip:      {zip_path}")
print(f"MD5:      {checksum}")
print(f"Manifest: {manifest_path} updated — upload the zip to release tag v{version}")
PY
