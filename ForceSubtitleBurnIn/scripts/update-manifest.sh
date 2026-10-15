#!/usr/bin/env bash
# Adds (or replaces) one version of this plugin in a Jellyfin repository manifest.json.
# Package fields (guid, name, targetAbi, ...) come from meta.json so there is one source of truth.
#
# Usage: update-manifest.sh <manifest.json> <meta.json> <version a.b.c.d> <zip file> <zip url> <changelog>
set -euo pipefail

manifest=$1 meta=$2 version=$3 zip=$4 url=$5 changelog=$6

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must be a.b.c.d, got '$version'" >&2; exit 1; }
[[ $url == *.zip ]] || { echo "Jellyfin only installs URLs ending in .zip" >&2; exit 1; }
[[ -s $manifest ]] || echo '[]' > "$manifest"

# Jellyfin verifies the download with MD5 (InstallationManager.PerformPackageInstallation).
checksum=$(md5sum "$zip" | cut -d' ' -f1)
timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)

tmp=$(mktemp)
jq --slurpfile meta "$meta" \
   --arg v "$version" --arg src "$url" --arg md5 "$checksum" --arg ts "$timestamp" --arg cl "$changelog" '
  ($meta[0]) as $m
  | ((map(select(.guid == $m.guid)) | first) // {versions: []}) as $old
  | [{
      guid: $m.guid,
      name: $m.name,
      description: $m.description,
      overview: $m.overview,
      owner: $m.owner,
      category: $m.category,
      versions: (
        [{version: $v, changelog: $cl, targetAbi: $m.targetAbi, sourceUrl: $src, checksum: $md5, timestamp: $ts}]
        + ($old.versions | map(select(.version != $v)))
        | sort_by(.version | split(".") | map(tonumber)) | reverse
      )
    }]
  + map(select(.guid != $m.guid))
' "$manifest" > "$tmp"
mv "$tmp" "$manifest"
echo "manifest: $version md5=$checksum"
