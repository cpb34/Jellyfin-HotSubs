#!/usr/bin/env bash
# Adds (or replaces) one version of this plugin in a Jellyfin repository manifest.json.
# Package fields (guid, name, description, category, targetAbi, ...) come from meta.json so there
# is one source of truth. "overview" is set to the release tag (e.g. v0.2) of the newest version.
#
# Usage: update-manifest.sh <manifest.json> <meta.json> <version a.b.c.d> <zip file> <zip url> <changelog> <tag>
set -euo pipefail

manifest=$1 meta=$2 version=$3 zip=$4 url=$5 changelog=$6 tag=$7

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must be a.b.c.d, got '$version'" >&2; exit 1; }
[[ $url == *.zip ]] || { echo "Jellyfin only installs URLs ending in .zip" >&2; exit 1; }
[[ -s $manifest ]] || echo '[]' > "$manifest"

# Jellyfin verifies the download with MD5 (InstallationManager.PerformPackageInstallation).
checksum=$(md5sum "$zip" | cut -d' ' -f1)
timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)

tmp=$(mktemp)
jq --slurpfile meta "$meta" \
   --arg v "$version" --arg src "$url" --arg md5 "$checksum" --arg ts "$timestamp" --arg cl "$changelog" --arg tag "$tag" '
  ($meta[0]) as $m
  | ((map(select(.guid == $m.guid)) | first) // {versions: []}) as $old
  | ([{version: $v, changelog: $cl, targetAbi: $m.targetAbi, sourceUrl: $src, checksum: $md5, timestamp: $ts}]
      + ($old.versions | map(select(.version != $v)))
      | sort_by(.version | split(".") | map(tonumber)) | reverse) as $versions
  | [{
      guid: $m.guid,
      name: $m.name,
      description: $m.description,
      # Overview names the newest version; re-releasing an older version keeps the current one.
      overview: (if $versions[0].version == $v then $tag else ($old.overview // $tag) end),
      owner: $m.owner,
      category: $m.category,
      versions: $versions
    }]
  + map(select(.guid != $m.guid))
' "$manifest" > "$tmp"
mv "$tmp" "$manifest"
echo "manifest: $version md5=$checksum"
