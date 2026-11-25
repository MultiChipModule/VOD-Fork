# YouTube extraction and authentication

VOD-Fork prefers the YouTube Data API v3 for initial channel imports, historical uploads, playlist discovery and detailed video metadata. It batches up to 50 items per API request where the endpoint supports it. Once a channel's ID is known, recent uploads are checked with conditional RSS requests every 30 minutes by default. Playlist membership refreshes every six hours. Both intervals are configurable under **YouTube metadata**. The existing publication task wakes every 15 minutes, so actual work runs on the first scheduled pass after it becomes due.

With no API key, VOD-Fork uses public RSS and oEmbed for standalone videos, plus flat listings capped at 100 entries when a source needs discovery or history. A channel URL with a stable `/channel/UC…` ID can use RSS even if flat listing is suspended. YouTube Shorts and live channel tabs still use bounded flat listings to preserve their tab membership. Truncated, failed or quota-limited responses leave published items in place. Full historical imports and complete membership for large playlists work best with an API key.

The first playback resolves the media, probes it for Jellyfin negotiation, and fetches available English subtitles. The resolution cache coalesces concurrent requests for the same media. VOD-Fork can fill in missing metadata and discover original quality tiers from that same extraction. New tiers appear as alternate sources after the next successful source sync. Previously published quality variants remain available as before. The plugin stores no video downloads. Caption files are cached locally and served through the existing authenticated subtitle delivery.

## Configure a Data API key

1. In Google Cloud, create or select a project, enable **YouTube Data API v3**, and create an API key. Restrict the key to that API. Apply an IP restriction if your Jellyfin server has a stable outbound IP.
2. Open **Dashboard → Plugins → VOD → YouTube metadata**. Paste the key into **YouTube Data API key** and select **Save API key**. The field empties and the status shows a fixed mask. The next API request checks whether Google accepts the key.
3. To replace the key, paste the new value and select **Save API key** again. Select **Clear API key** to return to RSS, oEmbed, and bounded flat listings.
4. To import a saved, enabled source immediately, open its **Detail** panel and select **Import now**. The default local budget is 10,000 units per Pacific day, with 1,000 reserved for explicit imports. Match the local budget to the project's actual allowance. The dashboard counts VOD-Fork's requests, and Google also counts other callers sharing the project.

The dashboard saves the encrypted key and its ASP.NET Core Data Protection key ring in Jellyfin's private plugin data directory. Keep that directory on a persistent volume and protect both files and backups. Use HTTPS when opening the dashboard remotely.

Existing key file configurations continue to work and show a **legacy file** status. To migrate, paste the key into the dashboard and save. Saving or clearing takes precedence over the old path. The original file remains available for rollback.

The API key is for public library metadata. The dedicated YouTube cookie file and JavaScript runtime remain the playback settings. A future per-user Google OAuth flow will have its own credentials and consent.

## Configure Jellyfin

1. Set **yt-dlp path** to a current official yt-dlp build and set **JS runtime** to your configured runtime, for example `deno:/config/deno`. VOD-Fork ignores user and system yt-dlp configuration files for YouTube. Keep those settings in the plugin dashboard. An explicitly configured runtime is the only one enabled for YouTube extraction. The public oEmbed metadata lookup for a standalone URL and the subsequent signed media and caption requests use their own URLs without account cookies.
2. For anonymous extraction, leave **Optional YouTube cookies.txt path** blank. If you choose to authenticate, export a Netscape-format `cookies.txt` locally from a dedicated account you control. Treat it like a password. YouTube may challenge or restrict even an authenticated account.
3. On TrueNAS, store the file in a private directory readable by the Jellyfin process. Set the file owner to the Jellyfin service account and grant owner-only access with `chmod 600 cookies.txt`. Mount the directory read-only at a private container path, such as `/config/private`. Keep both the original file and the plugin data directory out of shares, repositories, and unprotected backups.
4. In **Dashboard → Plugins → VOD → YouTube extraction**, enter the absolute path visible inside the container, for example `/config/private/cookies.txt`. The status should show **Signed-in cookies present**. That confirms the file contains the expected unexpired session fields and is readable. YouTube confirms the session only during extraction. If yt-dlp reports a rejected or rotated login, VOD-Fork discards that result. Export a fresh cookie file and check the dashboard before trying again.
5. To return to anonymous operation, clear the dedicated path. To retire the credential, revoke the account session and remove the seed and VOD's private copies `cookies-youtube*.txt` and associated `.seed` files from the plugin data directory after stopping Jellyfin. The legacy cookie field remains available for other providers. This setting is independent of any future per-user OAuth.

## Pacing and restrictions

The defaults are one concurrent extraction, ten seconds between starts and at most 60 background extractions over a rolling hour. The hourly background limit does not consume playback capacity. Playback takes priority when an extraction slot opens. Existing playback sessions and valid cached resolutions can still be served.

A bot challenge or HTTP 429 suspends new YouTube extractions for one hour by default. Repeated restrictions increase the delay up to 24 hours. The cooldown survives restart. New Jellyfin PlaybackInfo requests receive a native rate-limit response and new stream startups without a usable cached resolution receive HTTP 503 with a retry time. An existing native streaming job or a usable cached stream can still serve media. The dashboard shows the last restriction, active and queued work, extraction slots used since restart, and the next permitted retry. A temporary failure leaves published items in place. The budget counts extraction invocations rather than each HTTP request made internally by yt-dlp.

## Install and roll back

Use the [VOD-Fork catalog](https://raw.githubusercontent.com/MultiChipModule/VOD-Fork/refs/heads/main/manifest.json) under **Dashboard → Plugins → Repositories**. Install VOD-Fork 5.3.1.0 and restart Jellyfin. To roll back, stop Jellyfin and replace the installed version with 5.3.0.0 from the same catalog or the [previous release ZIP](https://github.com/MultiChipModule/VOD-Fork/releases/tag/v5.3.0.0). Keep the existing plugin data and Jellyfin user data when changing versions. Version 5.3.0.0 reads the legacy file setting, so it needs a valid file path for Data API access. Follow the backup and manual replacement steps in [PHASE1_INSTALL.md](PHASE1_INSTALL.md).
