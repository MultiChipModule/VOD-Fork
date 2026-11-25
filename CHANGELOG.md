# Changelog

## 5.2.2.0

- Accept both empty and zero expiry fields in Netscape session cookies.
- Extend regression coverage for direct native startup during cooldown and shared HLS manifests across quality variants.

## 5.2.1.0

- Stop playback negotiation and media startup when a YouTube cooldown blocks the source. Return native Jellyfin playback errors and retry headers.
- Apply configured cookies and JavaScript runtime consistently, and reject results when yt-dlp reports that the session was lost.
- Share resolutions and upstream manifests across quality variants, probe selected streams on demand, and show actionable extraction status.

## 5.2.0.0

- Publish YouTube videos from metadata-only listings and resolve and probe playable media on demand.
- Share extraction limits across synchronization and playback. Prioritize playback and persist escalating restriction cooldowns.
- Support a private, dedicated YouTube cookie file and show extraction and retry status in the administrator dashboard.

## 5.1.3.0

- Prefer a verified lower original YouTube rung when an existing pinned source exceeds the requested bitrate.
- Preserve the visible item and actual version identities in native playback reports from clients that report an alternate item.
- Extend opt-in logs for native playback reporting and external subtitle requests.

## 5.1.2.0

- Prefer equivalent original YouTube HLS sources when Jellyfin's device profile indicates that copying can avoid encoding.
- Coalesce concurrent YouTube extraction and cap cached resolutions.
- Preserve explicit quality and resume choices, and report the reason for automatic source promotion when diagnostics are enabled.

## 5.1.1.0

- Keep YouTube HLS playback and probing on an audio/video-only master. Re-probe persisted stream lists with a physical subtitle or missing indices.
- Preserve separately selectable creator-uploaded and automatic English captions.
- Add opt-in sanitized playback negotiation and native media request logs. Infuse behavior needs a client trace.
- Report native channel discovery and playlist publication counts.

## 5.1.0.0

- Add opt-in channel folders and native ordered playlists that reference existing playback items.

## 5.0.2.0

- Offer creator-uploaded and automatic English as separate normalized WebVTT tracks.

## 5.0.1.0

- Add selectable automatic English caption fallback and authenticated native subtitle delivery.
