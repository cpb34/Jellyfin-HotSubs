# HotSubs

Jellyfin plugin that burns in all subtitles. Fixes transcoding issues between Moonfin for Android and overrides web client default settings.

Built for Jellyfin **10.11.11**.

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add a repository:
   - **Name:** `HotSubs`
   - **URL:** `https://raw.githubusercontent.com/cpb34/Jellyfin-HotSubs/master/manifest.json`
2. Open **Catalog**, find **HotSubs**, and click **Install**.
3. Restart Jellyfin.

## What it does

- **A subtitle is selected:** the server transcodes the video and burns that subtitle into
  it. This applies to text subtitles (SRT, ASS/SSA, WebVTT) and image subtitles (PGS, DVD,
  DVB) alike.
- **Subtitles are switched or turned on mid-playback:** the client restarts the stream at the
  current position with the new subtitle burned in.
- **The file has subtitle tracks but none is selected:** the file still streams through the
  server, usually as a lightweight remux where the video is copied rather than re-encoded.
  That is what lets a subtitle turned on later be burned in.
- **The file has no subtitle tracks at all:** it direct plays as normal.

## Things to know

- **Server load.** Showing subtitles always means a full video transcode, so make sure your
  server, or its hardware acceleration, can handle it.
- **Remux permission.** Files with subtitle tracks never direct play. For playback with
  subtitles off to use the cheap remux, the user needs the permission *"Allow video playback
  that requires conversion without re-encoding"*. Without it, those sessions are full
  transcodes too.
- **Transcode permission.** Users who aren't allowed to transcode can't get burned-in
  subtitles.
- **Not covered:**
  - Live TV.
  - Clients that start playback without sending a device profile.
