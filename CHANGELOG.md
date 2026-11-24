# Changelog

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
