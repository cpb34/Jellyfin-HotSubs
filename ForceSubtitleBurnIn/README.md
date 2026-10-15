# Force Subtitle Burn-In (Jellyfin 10.11.11)

Makes the server burn every selected subtitle into the video, for every client that asks
for playback through `POST /Items/{id}/PlaybackInfo`. That covers Moonfin, jellyfin-web and
the official apps. The client never renders subtitles itself.

It is one global MVC `IActionFilter`. It never sets `context.Result`, never short-circuits,
and never catches. It works in two places.

**1. `MediaInfoController.GetPostedPlaybackInfo`, before the server decides how to play.**
- **Clears `DeviceProfile.SubtitleProfiles`** in the posted body. With no External, Embed or
  Hls profile, StreamBuilder falls back to `Encode` for every subtitle
  (`StreamBuilder.cs:1512-1519`).
  - `Encode` makes a selected subtitle ineligible for direct play (`:1297-1308`).
  - The transcode URL then carries `SubtitleStreamIndex=N&SubtitleMethod=Encode`
    (`StreamInfo.cs:960, 1111`).
  - Every subtitle stream is reported with `DeliveryMethod=Encode` and no `DeliveryUrl`
    (`MediaInfoHelper.cs:470-493`), so clients don't load sidecar files.
- **Sets `EnableSubtitlesInManifest=false`** on every transcoding profile. HLS masters then
  carry no subtitle renditions the client could pick by itself (`DynamicHlsHelper.cs:197`).
- **Disables direct play and direct stream (`enableDirectPlay=false`, `enableDirectStream=false`)
  for items that have any subtitle stream**, even when playback starts with subs off. These
  query arguments take precedence over the body (`MediaInfoController.cs:161-162`).
  - Without this, a direct-played session that has a subtitle turned on mid-playback just
    switches tracks inside the client (Media3: `supportsRuntimeTrackSelection => true`). The
    server is never asked.
  - With it, the session is a server stream (usually a cheap video-copy remux while subs are
    off). Turning a subtitle on finds no client-side track, so Moonfin re-requests
    PlaybackInfo, which burns it (`playback_manager.dart:3800-3833`).

**2. Video stream actions, as a backstop for clients that build their own stream URLs.**
- A non-static request with `subtitleStreamIndex >= 0` gets `subtitleMethod=Encode`.
- This also keeps master, main playlist and segments of one HLS session consistent.

## Install from a plugin repository (recommended)

The repository is a public GitHub repo. GitHub Actions builds the plugin, so you don't need
.NET locally.

**One-time setup**
1. Push this folder to a **public** GitHub repo, e.g. `github.com/<you>/jellyfin-force-subtitle-burnin`.
2. Repo Settings → Actions → General → Workflow permissions → **Read and write**. The
   workflow pushes `manifest.json` to the default branch, so if that branch is protected,
   let `github-actions[bot]` bypass the protection.

**Each release**
```sh
git tag -a v1.0.0 -m "Initial release"   # the tag message becomes the changelog
git push origin v1.0.0
```

`.github/workflows/release.yaml` then:
1. builds with the .NET 9 SDK and stamps the version from the tag (`v1.0.1` → `1.0.1.0`);
2. zips the plugin DLL only;
3. attaches the zip to a GitHub release;
4. runs `scripts/update-manifest.sh`, which adds the version (with its MD5 and the
   `targetAbi` from `meta.json`) to `manifest.json` and commits it.

**In Jellyfin**
1. Dashboard → Plugins → Repositories → **+**. Add
   `https://raw.githubusercontent.com/<you>/<repo>/<default-branch>/manifest.json`.
2. Go to Catalog, find **Force Subtitle Burn-In**, and click Install.
3. Restart.

After that, new tags show up as updates and are auto-installed. The server writes
`AutoUpdate=true` into `meta.json` on install (`PluginManager.cs:429`).

**What the server checks** (`InstallationManager.cs`):
- `sourceUrl` must end in `.zip` (`:518`).
- The MD5 of the download must equal `checksum` (`:536-545`).
- The zip is extracted to `plugins/<name>_<version>/` (`:525, 548, 565`).
- `meta.json` is generated from the manifest entry (`PluginManager.cs:380-430`).
- Versions whose `targetAbi` is newer than the server are hidden (`:264-266`).
- The newest compatible version is offered (`:277`).

To bump compatibility for a new server, edit `targetAbi` in `meta.json` before tagging.

**Private repo or no GitHub?** Jellyfin can't authenticate to a repository URL. Serve
`manifest.json` and the zips from any plain HTTP(S) location the server can reach, such as
nginx on your NAS. Build the zip yourself, then run
`scripts/update-manifest.sh manifest.json Jellyfin.Plugin.ForceSubtitleBurnIn/meta.json 1.0.0.0 <zip> <http url of zip> "changelog"`.

## Manual install

1. Build (needs the .NET 9 SDK and nuget.org):
   ```sh
   dotnet build Jellyfin.Plugin.ForceSubtitleBurnIn -c Release
   ```
2. Create `<jellyfin-data>/plugins/ForceSubtitleBurnIn_1.0.0.0/`. On Docker that is
   `/config/plugins/...`; on Debian it is `/var/lib/jellyfin/plugins/...`.
3. Copy these two files into it:
   - `bin/Release/net9.0/Jellyfin.Plugin.ForceSubtitleBurnIn.dll`
   - `bin/Release/net9.0/meta.json`
4. Restart Jellyfin. Check that the plugin shows as Active under Dashboard → Plugins.
5. Optional: to get the per-request override log, set this in `logging.json`:
   `"Jellyfin.Plugin.ForceSubtitleBurnIn": "Debug"`.

**Toggle.** There is no config page. Edit
`<jellyfin-data>/plugins/configurations/Jellyfin.Plugin.ForceSubtitleBurnIn.xml` and set
`<Enabled>false</Enabled>`, then restart. The file is created on first load. The filter
reads the value on every request through `IPluginManager.GetPlugin(id).Instance`, so a
config save through the API also takes effect without a restart.

## targetAbi / packages

| Item | Value | Source |
|---|---|---|
| TargetFramework | `net9.0` | `MediaBrowser.Controller.csproj:39`, plugin template csproj |
| Jellyfin.Controller / Jellyfin.Model | `10.11.11`, `ExcludeAssets=runtime` | nuget.org (10.11.11 published 2026‑06‑06) |
| targetAbi | `10.11.11.0` | `SharedVersion.cs` = 10.11.11 |

The plugin loads if `serverVersion >= targetAbi` (`PluginManager.cs:692-702`). You can
lower it to `10.11.0.0`, as the template does, to load on earlier 10.11.x servers. Nothing
here uses API added after 10.11.0, but that has not been tested on older builds.

## Rebuilding for a new server version

1. Bump both `PackageReference` versions and `TargetFramework`. 10.12/12.x targets `net10.0`.
2. Bump `targetAbi` in `meta.json`.
3. Re-check the contract below against the new tag. Run:
   `grep -n "subtitleMethod,\|subtitleStreamIndex,\|bool? @static" Jellyfin.Api/Controllers/{DynamicHlsController,VideosController}.cs`,
   then read `GetPostedPlaybackInfo` in `MediaInfoController.cs`. Check that query values
   still take precedence over the body, and that `StreamBuilder.GetSubtitleProfile` still
   falls back to `Encode`.
   If the server drifts, the filter logs a single warning per request
   (`... signature changed (...); passing through unchanged`) and does nothing.

## Contract the filter depends on (likeliest upgrade breakages)

**Controller types.** These are matched by `ControllerTypeInfo.FullName` and assembly name
`Jellyfin.Api`. `Jellyfin.Api` is not on NuGet, so `typeof()` can't be used.

| Controller | Actions (MVC `ActionName` = method name) |
|---|---|
| `Jellyfin.Api.Controllers.DynamicHlsController` | `GetMasterHlsVideoPlaylist`, `GetVariantHlsVideoPlaylist`, `GetHlsVideoSegment`, `GetLiveHlsStream` |
| `Jellyfin.Api.Controllers.VideosController` | `GetVideoStream`, `GetVideoStreamByContainer` |
| `Jellyfin.Api.Controllers.MediaInfoController` | `GetPostedPlaybackInfo` |

**Action parameters.** Names are the C# parameter names that bind as `ActionArguments` keys.

| Name | Declared type | Used for |
|---|---|---|
| `subtitleMethod` | `MediaBrowser.Model.Dlna.SubtitleDeliveryMethod?` | the value that gets overwritten (added if absent) |
| `subtitleStreamIndex` | `int?` | absent, null, or `< 0` means no subtitle, so pass through |
| `static` (`@static`) | `bool?` | `true` means direct play/stream, so pass through |

PlaybackInfo action parameters (`MediaInfoController.cs:119-135`):

| Name | Declared type | Used for |
|---|---|---|
| `itemId` | `Guid` | subtitle-stream lookup |
| `mediaSourceId` | `string?` | subtitle-stream lookup for an alternate version (falls back to body `MediaSourceId`) |
| `enableDirectPlay`, `enableDirectStream` | `bool?` | set to `false` for items with subtitle streams |
| `playbackInfoDto` | `Jellyfin.Api.Models.MediaInfoDtos.PlaybackInfoDto?` (type name check) | its `DeviceProfile` (`MediaBrowser.Model.Dlna.DeviceProfile`) is modified |

`DeviceProfile.SubtitleProfiles` (`SubtitleProfile[]`) and
`TranscodingProfile.EnableSubtitlesInManifest` (`bool`) must stay settable.

`PlaybackInfoDto` lives in `Jellyfin.Api`, which is not on NuGet. Its public `DeviceProfile` and
`MediaSourceId` properties are therefore read through MVC's `IModelMetadataProvider`
(`ModelMetadata.PropertyGetter`), the same accessor model binding uses. No non-public
members are touched. If you would rather not have even that, the alternative is a
compile-time reference to `Jellyfin.Api.dll` from your server install.

Other dependencies:
- `MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.Encode` (value 0)
- `MediaBrowser.Common.Plugins.IPluginManager.GetPlugin(Guid, Version?)` and `LocalPlugin.Instance`
- `MediaBrowser.Controller.Library.IMediaSourceManager.GetMediaStreams(MediaStreamQuery)` with `ItemId` and `Type = MediaStreamType.Subtitle`
- `MediaBrowser.Controller.Plugins.IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)`

**Not targeted, deliberately:**
- Audio actions (`GetMasterHlsAudioPlaylist`, `GetVariantHlsAudioPlaylist`, `GetHlsAudioSegment`,
  `AudioController.*`). They accept the subtitle params, but they are audio-only.
- `UniversalAudioController`. It has no subtitle params.

## Research notes (jellyfin/jellyfin @ v10.11.11, 1fbd873)

**1. Actions accepting both params**

| File:line | Action | `@static` | `subtitleStreamIndex` | `subtitleMethod` | Default if null |
|---|---|---|---|---|---|
| `DynamicHlsController.cs:168` | `GetLiveHlsStream` (`Videos/{itemId}/live.m3u8`) | 171 | 200 | 201 | External (257) |
| `DynamicHlsController.cs:412` | `GetMasterHlsVideoPlaylist` (`Videos/{itemId}/master.m3u8`, GET+HEAD) | 414 | 445 | 446 | External (502) |
| `DynamicHlsController.cs:588` | `GetMasterHlsAudioPlaylist` | 590 | 620 | 621 | External (673) |
| `DynamicHlsController.cs:758` | `GetVariantHlsVideoPlaylist` (`Videos/{itemId}/main.m3u8`) | 760 | 791 | 792 | External (847) |
| `DynamicHlsController.cs:930` | `GetVariantHlsAudioPlaylist` | 932 | 962 | 963 | External (1015) |
| `DynamicHlsController.cs:1106` | `GetHlsVideoSegment` (`Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}`) | 1113 | 1144 | 1145 | External (1202) |
| `DynamicHlsController.cs:1291` | `GetHlsAudioSegment` | 1298 | 1328 | 1329 | External (1383) |
| `VideosController.cs:316` | `GetVideoStream` (`Videos/{itemId}/stream`, GET+HEAD) | 319 | 350 | 351 | **Encode** (408) |
| `VideosController.cs:557` | `GetVideoStreamByContainer` (`Videos/{itemId}/stream.{container}`) | 560 | 591 | 592 | delegates to `GetVideoStream` (611) |
| `AudioController.cs:93/260` | `GetAudioStream[ByContainer]` | – | – | 126/293 | audio only |

All three parameters are `[FromQuery]` and declared as `bool?`, `int?`, and
`SubtitleDeliveryMethod?`.

**Transcode vs direct.**
- Progressive (`VideosController`): with `static=true`, the action returns the file or the
  remote/live stream without ffmpeg (`VideosController.cs:442-481`). Otherwise it builds an
  ffmpeg command (`:483-493`).
- HLS: `Static` flows into `BaseEncodingJobOptions.Static`
  (`MediaBrowser.Controller/MediaEncoding/BaseEncodingJobOptions.cs:70`). That forces copy
  codecs (`EncodingJobInfo.cs:208-505`).
- Video copy versus re-encode is decided later by `EncodingHelper.CanStreamCopyVideo`.

**2. Argument → ffmpeg**

1. The action copies `subtitleMethod` into the `StreamingRequestDto`/`VideoRequestDto`
   (lines in the table above).
2. `StreamingHelpers.GetStreamingState` calls `EncodingHelper.AttachMediaSourceInfo`
   (`StreamingHelpers.cs:157`). That sets `state.SubtitleStream` and
   `state.SubtitleDeliveryMethod = videoRequest.SubtitleMethod`
   (`EncodingHelper.cs:7396-7397`).
3. `ShouldEncodeSubtitle` returns true when the method is Encode (`EncodingHelper.cs:7850-7854`).
   That drives:
   - the text-subtitle filter `subtitles=f='…'` (`:1862-1921`)
   - the graphical-subtitle overlay (`:3786-3873` for software; `:4033-4082` and later for HW)
   - font extraction before ffmpeg starts (`TranscodeManager.cs:398-415`)
4. Encode also disables video stream copy (`EncodingHelper.cs:2362-2368`).

**HLS consistency.**
- The master playlist builds the `main.m3u8` URL from the raw request query string
  (`DynamicHlsHelper.cs:152, 188-189`). So the client's original `SubtitleMethod=Hls`
  propagates to main and segment URLs.
- That is why the filter also targets `GetVariantHlsVideoPlaylist` and `GetHlsVideoSegment`.
  The segment action starts the ffmpeg job. Every request in the session gets the same
  override, so the methods can't mix.
- With Encode, the master omits the subtitle rendition group
  (`DynamicHlsHelper.cs:201-205`). That holds even with `EnableSubtitlesInManifest=true`.

**3. Moonfin** (Moonfin-Client/Moonfin-Core @ 14848c6; Flutter, Media3 backend on Android)

Endpoints used:
- `POST /Items/{id}/PlaybackInfo` (`server_jellyfin/.../jellyfin_playback_api.dart:87-114`)
- For video, the server's `TranscodingUrl`, which is HLS `master.m3u8`
  (`jellyfin_media_stream_resolver.dart:445-450`; its transcoding profiles are all
  `Protocol: hls`, `device_profile_builder.dart:399-440`)
- Direct stream: the server's `DirectStreamUrl`, `/Videos/{id}/stream.{container}?Static=true`
  (`:439-444`)
- Direct play: `/Videos/{id}/stream?Static=true` (`jellyfin_playback_api.dart:138-149`)
- Audio:
  - `/Audio/{id}/universal`
  - `/Audio/{id}/stream?Static=true`
  - a self-built `/Audio/{id}/main.m3u8` for E-AC3 hybrid audio (`:262-283`)

Every video endpoint is already covered. The only self-built URL is audio-only, so no
targets were added.

**Important:** Moonfin removes `SubtitleStreamIndex` from the transcode URL whenever the
server marked the track `External` (`jellyfin_media_stream_resolver.dart:44-59, 171-186`).

**Important:** Moonfin removes `SubtitleStreamIndex` from the transcode URL whenever the
server marked the track `External` (`jellyfin_media_stream_resolver.dart:44-59, 171-186`).
That is why the stream-action filter alone could not burn text subs. PlaybackInfo had to be
handled.

Moonfin also treats a subtitle as burned in, and stops drawing its own copy, when the
transcode URL contains `subtitlemethod=encode` or the stream reports `DeliveryMethod=Encode`
(`playback_manager.dart:3464-3480`).

**4. PlaybackInfo: handled.**
- Without the change, StreamBuilder resolves text subs to `External`.
  `GetExternalSubtitleProfile` takes External before Hls and converts srt/ass→vtt
  (`StreamBuilder.cs:1540-1593`).
- The server then drops the index from the URL (`StreamInfo.cs:960`), and the client renders
  the sidecar file.
- `AlwaysBurnInSubtitleWhenTranscoding` doesn't help:
  - the per-stream `DeliveryMethod` stays `External`, so Moonfin still strips the index;
  - it does nothing for direct play.

## Behavioural consequences

- **Any item with a subtitle stream never direct-plays**, even with subs off. With subs off
  it is usually a video-copy HLS remux. That needs the user's "Allow video playback that
  requires conversion without re-encoding" permission; without it, it becomes a full
  transcode.
- **A selected subtitle always means a full video transcode.** Encode forbids video copy
  (`EncodingHelper.cs:2362-2368`).
- **Default subtitles count.** A subtitle chosen by the user's language and subtitle-mode
  preferences is burned too (`StreamBuilder.cs:546-585, 654`).
- **Users without transcoding permission.** Their server reports no transcode URL. Moonfin
  then falls back to plain direct play (`terminalDirectPlay`), and subtitles are not burned.
- **Not covered:**
  - `GET /Items/{id}/PlaybackInfo` and a POST with an empty body. The server uses a
    registered, shared profile in that case, which the plugin must not mutate.
  - `POST /LiveStreams/Open` (Live TV).
  - Clients that build static URLs without asking PlaybackInfo.
  - Subtitle streams that exist only on an alternate version the request didn't name.
- **Legacy `params` can override the stream filter.** It is parsed after the DTO is built
  (`StreamingHelpers.cs:60-62, 538-545`). No current client found uses it.

## Assumptions not verified from source / not tested here

- **The real plugin project has not been compiled here.** This environment blocks nuget.org
  and the .NET 9 SDK. What was run instead:
  - The filter, registrator and config were compiled with `TreatWarningsAsErrors` on .NET 10
    ASP.NET Core MVC, against stubs with the 10.11.11 namespaces and signatures.
  - They were exercised end-to-end (22/22 cases) against fake controllers built into an
    assembly named `Jellyfin.Api`.

  Stream cases covered: adding an absent key, overriding Hls/Embed/External, Encode being
  idempotent, skipping `static=true`, `-1`, and no index, the audio non-target, signature
  drift, `Enabled=false`, HEAD requests, and a downstream filter seeing the mutated value.

  PlaybackInfo cases covered: a real JSON body bound into a `PlaybackInfoDto` is modified
  through model metadata; direct play is disabled for an item with subs (including when the
  query says `enableDirectPlay=true`); direct play is left alone for an item without subs;
  an alternate version is resolved via the query or the body; an empty body passes through;
  `Enabled=false` passes through.
- **StreamBuilder outcome not run.** The server's actual decision for an emptied
  `SubtitleProfiles` comes from reading the source, not from running it.
- **Moonfin behaviour not run.** That covers mid-playback switching re-requesting
  PlaybackInfo, and not drawing a burned track. Both come from reading
  `playback_manager.dart` at 14848c6, not from running the app.
- **External subtitle files are assumed to be stored media streams.**
  `IMediaSourceManager.GetMediaStreams` is assumed to return them (`IsExternal=true`), as it
  does for scanned sidecar files. This was not verified for subtitles downloaded after the
  last scan.
- `BasePlugin<T>` constructor parity with the real package. This is taken from the template
  and from `MediaBrowser.Common`, not compiled.
- The `meta.json` timestamp/status format. It is deserialized with `JsonStringEnumConverter`
  (`JsonDefaults.cs:42`), which is believed fine but was not loaded by a real server.
- jellyfin-web's device profile subtitle methods for HLS were not read line-by-line. The
  "web text subs → External" claim rests on StreamBuilder's ordering rules plus web's
  sidecar handling (`playbackmanager.js:2908-2915`). Confirm it with test (e).
