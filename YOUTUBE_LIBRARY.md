# Native YouTube library

VOD-Fork 5.4.0.0 projects published YouTube sources into a Mixed Content library. Channels are native Jellyfin folders. **Videos** and the discovered public playlists are native ordered playlists that reference the same playable items.

## Configure

1. Keep your existing VOD publication libraries, source URLs, and filesystem paths. Install [VOD 5.4.0.0](https://github.com/MultiChipModule/VOD-Fork/releases/tag/v5.4.0.0) and restart Jellyfin.
2. In **Dashboard → Libraries → Add Media Library**, create a **Mixed Content** library named **YouTube**, using a new empty, writable media directory outside Jellyfin's internal data directory. If that library name is taken, choose another name. Give viewers access to both this browse library and the VOD publication libraries used by the linked items.
3. In **Dashboard → Plugins → VOD**, select the new library under **YouTube channel library (mixed)**. Save the settings. Enter any exclusions under **Excluded YouTube playlist IDs**. For reliable discovery and historical metadata, configure the existing **YouTube Data API key** under **YouTube metadata**.
4. Keep your enabled channel upload sources, for example https://www.youtube.com/@handle/videos. Use each source's **Retention days** to control the retained window. Let **VOD Listing & publication** run as scheduled. Once the sources contain published items, the scheduled pass also refreshes the channel view. To refresh just the browse view, run **VOD YouTube channel library** under **Dashboard → Scheduled Tasks**.
5. Open **YouTube** in its **Folders** view. Each channel shows **Videos** first, newest upload first. Public playlists follow alphabetically and use the original YouTube order. Empty playlists can appear when their members fall outside the retained sources.

Migration replaces pathless browse nodes with scan-stable Jellyfin folders and playlists. It reuses the underlying playback items and keeps their canonical IDs, user data and version links. Existing duplicate video imports remain available in their original libraries. Catalogue references choose one retained playback item per YouTube video. A discovered playlist links only to videos already imported through configured VOD sources. To include a missing video within your retention policy, add its channel, playlist or standalone URL as a VOD source. The browse task does not start historical imports.

Titles, artwork, and descriptions come from the provider where available. Synthetic episode and season display numbers are cleared for browsed YouTube items. Series and season identifiers continue to support Jellyfin navigation and reporting.

## Classification and client behavior

The Data API identifies active, scheduled, and archived broadcasts from video metadata. The bounded public Shorts and Streams tabs supply additional positive matches. The Data API has no authoritative Shorts field. An older Short outside the tab window, or a video the upstream cannot classify while quota or an extraction cooldown is active, can still appear. Duration alone never excludes a video.

Jellyfin library permissions apply to both browse folders and linked videos. Native folder clients and the ordered playlist endpoint can resolve canonical item IDs. Jellyfin Web, Swiftfin, and Infuse can differ in whether they expose mixed-library folders, show empty playlists, or honor server sort order. Check each intended client on the actual server. The catalog refresh uses metadata only. Playback and subtitles keep their existing on-demand paths.

## Roll back

Clear the **YouTube channel library (mixed)** setting to stop refreshes, or remove the dedicated browse library in Jellyfin. Keep the original publication libraries and their paths. Restore 5.3.1.0 from the [Jellyfin catalog](https://raw.githubusercontent.com/MultiChipModule/VOD-Fork/refs/heads/main/manifest.json) if needed.
