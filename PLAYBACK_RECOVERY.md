# Playback recovery

VOD-Fork 5.0.2.0 removed the stored embedded English subtitle row when it registered external subtitles. The YouTube master still advertised that rendition. Jellyfin's EncodingHelper.GetMapArgs calls FindIndex, which counts the media stream's position among rows from the same input path. It does not copy the advertised MediaStream.Index into the command. With stored audio at 0, video advertised at 2, and only external subtitles, Jellyfin computed position 1 for video. FFmpeg input 1 was still WebVTT. The negative subtitle map removed it. The same failure occurs from a playlist or the original item.

The patch filters YouTube masters down to the selected audio and video and keeps the same layout in publication, probing, and playback. The first authenticated PlaybackInfo re-probes stored sources with an embedded subtitle, missing video, or a gap in physical indices. It saves the physical probe before attempting optional captions. Both external English tracks retain their own identifiers and authenticated delivery. The database schema, playback IDs, and playlist links remain stable. Other providers retain their HLS subtitle handling.

## FFmpeg evidence

The integration tests use Jellyfin FFmpeg 8.1.3 and Jellyfin Controller's FindIndex. The exact failing and repaired lossless commands both select position 1 as video. The logical and physical stream order now agree.

```text
Original HLS input (index,codec,type):
0,aac,audio
1,webvtt,subtitle
2,h264,video,320,180

With -map 0:1 -map 0:0 -map -0:s, original output:
0,aac,audio

Filtered HLS input:
0,aac,audio
1,h264,video,320,180

With the mapping derived from the repaired layout, lossless output:
0,h264,video,320,180
1,aac,audio
```

Tests cover remux and transcode, two quality variants, both external subtitle descriptors, audio-first and video-first layouts, and multiple audio tracks. The tests decode video frames, seek from a nonzero position, and check input and output stream metadata, presentation timestamps, packet durations, and audio/video alignment. These are local fixture results.

The proxy can place video on local HTTP while audio remains on upstream HTTPS. This explains why FFmpeg may retry a reused connection on a different scheme. The reported retry continued to open the input. Production packet timing warnings still need a fresh trace. The patch leaves upstream segment timestamps and FFmpeg retry behavior alone.

## TrueNAS update and verification

1. Back up persistent Jellyfin configuration and the installed plugin binary directory. Keep yt-dlp, Deno and FFmpeg configured as they are.
2. Install the patch from the existing fork repository and restart Jellyfin. No additional executables or media cache are needed on the server. The first play may spend one probe per affected quality. The probes use the existing serialized probe gate. Routine listing still uses flat metadata and the subtitle cache retains its configured limits. Server CPU and memory were not measured.
3. Open YouTube video jDCpW34ZHEg in Jellyfin Web. Start at the beginning, seek forward and back, and check resume around 09:18. Try a quality change and each English subtitle choice, including Off. Repeat through two playlist links and with a second video. The fixture tests do not replace these live checks.
4. For the native channel view, confirm **YouTube channel library (mixed)** names the separate mixed library and run **VOD YouTube channel library**. Its log distinguishes an unset target from completed discovery and reports channel, playlist, link, creation, update and skip counts. Discovery failures still fail the task. Verify playlist order, artwork, shared resume and synthetic number visibility after a library scan. The patch retains the previous episode presentation logic. Some native clients may still display numbered metadata.

## Infuse diagnosis

Jellyfin's native static HTTP path can forward Range and return its upstream response. If it serves VOD's internal resource, that response can be an HLS master rather than a conventional file. Some master entries can contain a server-only loopback address. A live trace is needed to establish whether Infuse takes that path. This patch does not change native negotiation, byte-range behavior, server URLs, or authentication.

Enable **Log YouTube playback negotiation (for troubleshooting)** in VOD. Try the affected video in Infuse and Jellyfin Web. Record the time, client version, local or remote connection, and whether FFmpeg started. Collect matching **VOD PlaybackInfo** and **VOD media request** entries and the FFmpeg stream map if present. The log records client family, action, audio/video and subtitle indices, direct-play flags, and presence of Range. It omits full URLs and credentials.

If no native media request appears, capture HTTP method, redacted path without query values, status, content type, Range presence and whether the host was loopback, internal, or public at the reverse proxy. Remove tokens, cookies, signed URLs, and private hostnames before sharing. Disable diagnostics after the test. Use the trace to choose native HLS or a different delivery path. A sequential remux cannot promise arbitrary byte ranges without seekable backing.

## Rollback

Stop Jellyfin and restore the backed-up binary directory and its original metadata, or choose an earlier catalog version. **5.0.1.0** is the last version you confirmed working in Jellyfin Web. The catalog also retains 5.0.2.0 and 5.1.0.0, which contain the subtitle-row change. Keep the existing database and libraries. A full configuration snapshot restore can roll back later watch progress.
