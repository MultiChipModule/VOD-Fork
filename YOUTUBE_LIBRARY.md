# Native channel library in VOD-Fork 5.1.0.0

The channel view is opt-in. It uses ordinary Jellyfin folders and ordered playlists. Each playlist points to existing playback item IDs, so watch state, resume, playback quality and thumbnails belong to the same item across playlists. No client extensions are needed.

## Set up

1. Back up the plugin configuration and your persistent Jellyfin `/config` before upgrading. Install VOD 5.1.0.0 and restart. Keep your existing VOD publication libraries and sources in place.
2. In Jellyfin, create a **Mixed** library named **YouTube**, using a new empty directory writable by Jellyfin. If your existing publication library is already named YouTube, retain it under a different name and update VOD's original publication-library setting to that name before enabling the new view. Preserve its physical path. Give viewers access to the original playback library as well as the new browse library. This preserves existing access restrictions and item IDs.
3. In VOD's settings, set **YouTube channel library (mixed)** to the new library name. Use **Excluded YouTube playlist IDs** for comma-separated playlist IDs. Keep **Preferred subtitle languages** at `en` for English-only HLS rendering.
4. Verify the original upload sources are published. For a channel with all uploads desired, configure its actual upload source, for example `https://www.youtube.com/@handle/videos`, and set its **Retention days** to **100** if that is your preferred window. Run **VOD Listing & publication**, then **VOD YouTube channel library** in Scheduled Tasks. The channel task refreshes daily.
5. Open the new library in its folder view. Expect one folder per stable channel ID, with **Videos** first and the other playlists ordered by name. Open the same video under two playlists and verify that playback resumes at the same position. Check visible title numbers and repeat the browse test after a library scan.

## What the native view does

- The scheduled task discovers public playlists through yt-dlp and groups configured YouTube playlists by their resolved channel owner. It records native playlist entries in YouTube order. It adds no video downloads and does no per-playlist video metadata extraction. When two imported copies already exist, it prefers a retained channel upload as the shared target.
- Videos contains retained uploads already present through the channel's configured upload source. If only a playlist source was configured, Videos can include its imported entries whose uploader is that channel. Configure the upload source for complete channel coverage. A discovered playlist may omit videos outside the configured and retained playback sources.
- A failed or explicitly incomplete listing leaves the preceding native catalog in place. Exclusions and retention take effect on successful refresh. Explicit old upload dates in flat YouTube listings can skip metadata fetching. Undated entries use the existing metadata and retention flow.
- The episode field that produced numbers such as `1123001` is the synthetic date/rank `IndexNumber`. With the channel view enabled, future publication refreshes omit that field for synthetic YouTube values. Original title text, publication time, the season link, actual parsed episode numbers and playback IDs are retained. Raw date-based numbers remain in the plugin's existing SQLite records.
- The underlying publication library remains the source of playback items. Legacy copies already imported from multiple configured sources are retained. Playlist views reuse one canonical target per video, and the plugin does not merge existing user-data rows or alter which videos the legacy sources import.
- Native TV seasons cannot express many-to-many membership. A separate mixed library lets the plugin use native playlists without converting episode types or moving playback IDs. Client behavior on the production Jellyfin 12.4.0 server, including playlist navigation after a scan, native sort behavior and resumed playback across playlists, still needs live validation. Per-user account linking and per-user catalog ownership remain future work.

## Disable or roll back

Clear **YouTube channel library (mixed)** to prevent further updates. Remove only the separate browse library entry if you no longer need it. Its native playlist records contain references to original items. Keep the original VOD libraries and database. To restore numbered episode presentation, let the regular VOD publication reconcile after clearing the setting. For binary rollback, stop Jellyfin and restore the backed-up 5.0.2.0 or 5.0.1.0 directory and its original metadata. Both releases remain in the Jellyfin catalog. See [the subtitle installation guide](PHASE1_INSTALL.md) for the earlier rollback procedure.
