# YouTube extraction and authentication

VOD-Fork uses yt-dlp flat listings to import channel and playlist metadata, with YouTube oEmbed for standalone public video URLs. Listings reuse the existing short and full sync schedule and update known videos in place. VOD-Fork publishes as soon as it has a public title and URL. It stores descriptions, thumbnails and dates when YouTube provides them. yt-dlp can derive approximate dates from the relative dates on a channel page. Retention uses the available dates, and entries with unknown dates remain eligible until a date becomes available.

The first playback resolves the media, probes it for Jellyfin negotiation, and fetches available English subtitles. The resolution cache coalesces concurrent requests for the same media. VOD-Fork can fill in missing metadata and discover original quality tiers from that same extraction. New tiers appear as alternate sources after the next successful source sync. Previously published quality variants remain available as before. The plugin stores no video downloads. Caption files are cached locally and served through the existing authenticated subtitle delivery.

## Configure Jellyfin

1. Set **yt-dlp path** to a current official yt-dlp build and set **JS runtime** to your configured runtime, for example `deno:/config/deno`. VOD-Fork ignores user and system yt-dlp configuration files for YouTube. Keep those settings in the plugin dashboard.
2. For anonymous extraction, leave **Optional YouTube cookies.txt path** blank. If you choose to authenticate, export a Netscape-format `cookies.txt` locally from a dedicated account you control. Treat it like a password. YouTube may challenge or restrict even an authenticated account.
3. On TrueNAS, store the file in a private directory readable by the Jellyfin process. Set the file owner to the Jellyfin service account and grant owner-only access with `chmod 600 cookies.txt`. Mount the directory read-only at a private container path, such as `/config/private`. Keep both the original file and the plugin data directory out of shares, repositories, and unprotected backups.
4. In **Dashboard → Plugins → VOD → YouTube extraction**, enter the absolute path visible inside the container, for example `/config/private/cookies.txt`. The status should show **Cookie file ready**. That confirms the format, location and file access. YouTube confirms the session only during extraction.
5. To return to anonymous operation, clear the dedicated path. To retire the credential, revoke the account session and remove the seed and VOD's private copies `cookies-youtube*.txt` and associated `.seed` files from the plugin data directory after stopping Jellyfin. The legacy cookie field remains available for other providers. This setting is independent of any future per-user OAuth.

## Pacing and restrictions

The defaults are one concurrent extraction, ten seconds between starts and at most 60 background extractions over a rolling hour. The hourly background limit does not consume playback capacity. Playback takes priority when an extraction slot opens. Existing playback sessions and valid cached resolutions can still be served.

A bot challenge or HTTP 429 suspends new YouTube extractions for one hour by default. Repeated restrictions increase the delay up to 24 hours. The cooldown survives restart. The dashboard shows the last restriction, current activity and next permitted retry. A temporary failure leaves published items in place. The budget counts extraction invocations rather than each HTTP request made internally by yt-dlp.

## Install and roll back

Use the [VOD-Fork catalog](https://raw.githubusercontent.com/MultiChipModule/VOD-Fork/main/manifest.json) under **Dashboard → Plugins → Repositories**. Install VOD-Fork 5.2.0.0 and restart Jellyfin. To roll back, stop Jellyfin and replace the installed version with 5.1.3.0 from the same catalog or the [previous release ZIP](https://github.com/MultiChipModule/VOD-Fork/releases/tag/v5.1.3.0). Keep the existing plugin data and Jellyfin user data when changing versions. Follow the backup and manual replacement steps in [PHASE1_INSTALL.md](PHASE1_INSTALL.md). If you used a legacy cookie file on 5.1.3.0, that version uses the legacy field.
