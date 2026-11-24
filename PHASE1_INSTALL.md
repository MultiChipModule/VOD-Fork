# VOD-Fork 5.0.1.0 on TrueNAS

This is an in-place upgrade from upstream VOD 5.0.0.0. It retains the VOD plugin GUID, item IDs, plugin settings and database format. It targets the upstream Jellyfin 12.1.0.0 ABI and .NET 10. The reported TrueNAS Jellyfin 12.4.0 runtime and client playback still need validation on the server.

## Install the ZIP for immediate testing

1. Download `vod-fork_5.0.1.0.zip` and its `.sha256` file. Verify them together with `sha256sum -c vod-fork_5.0.1.0.zip.sha256` on a machine with that command. Extract the ZIP on your workstation or TrueNAS host. The Jellyfin container needs no additional tools.
2. In Jellyfin, turn off automatic updates for VOD and disable the upstream VOD repository if it only serves VOD. The fork shares the upstream GUID, so both repositories can otherwise offer upgrades to the same plugin.
3. Stop Jellyfin from TrueNAS **Apps → Jellyfin → Stop**. Take a snapshot or backup of its persistent `/config` dataset. Identify the host path of the mount that appears as `/config` inside the app. Preserve the upstream binary directory and back up VOD's plugin configuration and data, including `/config/plugins/Jellyfin.Plugin.VOD/vod.db` if present.
4. Move the current VOD *binary directory*, normally `VOD_5.0.0.0`, out of the `plugins` directory into your backup location. Identify it by its `Jellyfin.Plugin.VOD.dll` and `meta.json`. Leave the separate VOD data directory in place. Create `plugins/VOD_5.0.1.0/` under the host mount and copy the ZIP's `Jellyfin.Plugin.VOD.dll`, `meta.json` and `LICENSE` into it. Use the same ownership and read permissions as the previous binary directory. Keep the existing data directory writable by the Jellyfin app user (reported UID/GID 568).
5. Start Jellyfin. In **Dashboard → Plugins → My Plugins**, confirm VOD reports **5.0.1.0** and is active. If startup flags the plugin as malfunctioning or unsupported, capture the relevant server log and use the rollback below.

## Configure and verify

Open **Dashboard → Plugins → VOD**. Keep the working yt-dlp, Deno and ffmpeg paths. For the reported installation these are `/config/bin/yt-dlp`, `deno:/config/bin/deno` in the JavaScript runtime field, and `/usr/lib/jellyfin-ffmpeg/ffmpeg`. Keep any existing cookie configuration. For English-only HLS subtitles set **Preferred subtitle languages** to `en`.

| Setting | Value for the first test |
| --- | --- |
| Enable YouTube automatic English captions | On |
| Allow automatically translated English as a fallback | Off |
| YouTube caption cache lifetime | 24 hours |
| YouTube caption cache size | 64 MiB |

1. Sign in to Jellyfin Web and play **Minecraft Boogaloo Ep. 10 – The Dimension Of Doom** (`SLe8Ot4XTiM`). In the player, open the subtitles menu and select **English (Auto-generated)**. Check that text appears and still aligns after seeking.
2. Repeat with another offered quality. Compare the selected resolution, codec and audio with your usual settings. On a known creator-captioned video, expect the existing English track or a single external **English** track.
3. If testing authentication, copy the subtitle stream URL from the client’s Network panel, keep the token private, and repeat the request in a separate unauthenticated session with all token parameters and authentication headers removed. The managed caption request should return 401. A signed-in user without access to the item should receive 404. The fork leaves existing HLS subtitle routes in place.
4. If the track is absent, note the Jellyfin version, client, quality and approximate timestamp. Compare with `yt-dlp --list-subs --config-locations /config/bin/yt-dlp.conf 'https://www.youtube.com/watch?v=SLe8Ot4XTiM'` inside the running app. A provider challenge or an empty caption response can leave the video playable without subtitles.

The fallback is fetched on authenticated PlaybackInfo requests and cached as WebVTT in the plugin data folder’s `subtitles` directory. The cache prunes expired and oversized entries when saving a new file. A failed or expired provider URL can be retried after playback extraction refreshes. This build preserves YouTube’s cue timings and uses Jellyfin’s normal text subtitle conversion. No cap or format change is applied to the existing audio and video quality selection.

## Install from the catalog after publication

The fork builds, tests, and packages commits on `main`. To publish version 5.0.1.0, push tag `v5.0.1.0` from the matching commit on `main`. You can also run **Publish VOD-Fork** manually with an existing version tag. The workflow runs the full Release suite and verifies the ZIP before creating the GitHub Release. After it uploads the assets and updates `main`, add this repository in **Dashboard → Plugins → Repositories**:

```text
https://raw.githubusercontent.com/MultiChipModule/VOD-Fork/main/manifest.json
```

Select **VOD 5.0.1.0** from the catalog and restart. The draft repository manifest stays empty until a ZIP with a matching checksum is hosted. The CI build also emits an Actions artifact for manual testing. If branch protection blocks the workflow's manifest commit, publish the generated manifest with a direct commit to the fork's `main` using the already uploaded release ZIP and its matching metadata.

## Roll back

1. Stop Jellyfin. Move `VOD_5.0.1.0` out of the plugin directory, then return the backed-up upstream binary directory to its original path. If Jellyfin had previously marked that directory `Superseded`, restore the original `meta.json` from your pre-upgrade backup.
2. Start Jellyfin and confirm VOD reports **5.0.0.0** and plays an existing video. Remove the fork catalog to avoid reinstalling the test build. Restore the pre-upgrade `/config` snapshot only if required. A full restore also rolls back any settings or watch progress written after the snapshot.
3. The fork creates a `subtitles` cache folder inside VOD data. You can leave it in place or remove just that folder while Jellyfin is stopped after rolling back.

## Root cause and local validation

Upstream `YtDlpService.ResolveAsync` printed only `manifest_url` and `url`. Both publication and playback derived subtitle renditions from the HLS master. yt-dlp could report automatic English captions separately under `automatic_captions`, with no corresponding `EXT-X-MEDIA` entry in the master. Such captions never reached Jellyfin’s media-stream metadata. This build reads the caption data in the existing YouTube extraction and adds an external WebVTT stream before PlaybackInfo is built. Existing English tracks win, followed by creator-uploaded English from yt-dlp, `en-orig`, suitable untranslated English, and optionally translated English.

The baseline artwork test `Generate_DrawsTheCaption` failed because Skia resolved the generic `sans-serif` alias to an OpenAI Sans face with zero glyphs and measured `Arte` at zero width. A direct probe of DejaVu Sans resolved 6,196 glyphs and measured the string at 107 pixels at size 44. Artwork now falls back to DejaVu Sans when the initial face has zero glyphs. The original image comparison test is unchanged.

The cloud proxy returned HTTP 403 for the live YouTube reproduction URL. The extraction template was verified with yt-dlp 2026.11.23 using offline metadata. All 988 tests passed in Release configuration, including the unchanged artwork test. Live caption timing, client subtitle selection, and target server compatibility remain part of the TrueNAS check above.
