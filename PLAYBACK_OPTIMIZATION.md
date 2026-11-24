# YouTube playback optimization

## How automatic source selection works

Jellyfin negotiates each source using the actual client device profile, bandwidth, subtitle requirements and user permissions. Its source sort can still leave the requested item first even when an equally sized original in another codec would copy cleanly. VOD now adds up to two original HLS codec alternatives at the existing YouTube quality heights as sources are reconciled. It keeps the existing variant IDs. The URLs select exact codecs and heights, and the streams are probed before the plugin can prefer a source. A missing or stale stream description cannot qualify.

For automatic POST PlaybackInfo, the plugin can move a better, equivalent original to the front of Jellyfin's response. It uses the server's negotiated codecs, copy flags, video constraints, bitrate limits and subtitle delivery method. It checks the actual stream metadata to avoid a lower resolution, frame rate, HDR range, bit depth or substantial bitrate drop. Explicit source and track selections and saved resume positions keep native ordering. If the server's response offers no suitable alternative, Jellyfin continues to decide whether to copy, use hardware encoding or use software encoding. Device profiles that are unavailable or ambiguous do not produce a new source decision.

An original that Jellyfin can remux without encoding ranks first for internal HLS. Video copy with audio conversion ranks ahead of video encoding. Direct playback of ordinary compatible media remains Jellyfin's decision. The plugin does not advertise its internal HLS manifest as a new static file, add a progressive proxy, or create its own FFmpeg sessions. H.264, VP9, AV1 and HEVC are considered only when actually advertised by the source. If Jellyfin negotiates compatible AAC, or another original audio rendition, it can copy it. This update does not synthesize new audio groups from separate progressive files.

There is no new playback preference setting. Automatic selection uses the balanced behavior above. The next routine publication pass adds available alternatives. The first play can incur one-time probes for new variants. Both English subtitles stay native external WebVTT. Jellyfin can also negotiate its native text subtitle conversion according to the client profile. When choosing an explicit version or using an older client, check that client's reported Play Method before assuming it used the first source.

## Resolution caching

The cache keeps up to 64 successful YouTube resolutions in memory for up to five minutes. Known signed URL expiry shortens that interval by a safety margin. A failed master request invalidates the resolution for the next request. Requests for the same video and format share one successful extraction. Different videos and selectors are isolated and up to four YouTube extractions can run at once. Authenticated yt-dlp may serialize access to its working cookie file. Cancellation releases the extraction slot and a remaining caller may retry.

The update adds no temporary media store. Existing probe serialization, native Jellyfin FFmpeg limits, and subtitle cache limits remain in effect. Native Jellyfin controls any remux or transcode processes.

## Negotiation and media evidence

The automated test runs Jellyfin's native StreamBuilder with an H.264/AAC HLS playback profile. It produces a native H.264 delivery URL for both an original VP9 source and an equivalent original H.264 source. The first needs video encoding. The original H.264 source can copy and is preferred. A separate fixture copies 60-fps video while converting Opus to AAC and decodes the result.

Representative FFmpeg options exercised with real inputs:

```text
Before   VP9 input  -c:v libsvtav1 -preset 11 -svtav1-params lp=1 -c:a copy
After    AVC input  -c:v copy -c:a copy
Audio-only fallback -c:v copy -c:a aac
```

The benchmark used Jellyfin FFmpeg 8.1.3 and local two-second 1920×1080 60-fps inputs in a cloud sandbox with a four-CPU quota. Each concurrent worker ran one FFmpeg process. All outputs retained 1920×1080, a 60/1 video rate and AAC audio, and decoded successfully. Copy outputs contained H.264 and the software encode outputs contained AV1. The RSS column sums individual process peaks, which is an upper bound rather than a simultaneous measurement. Throughput is total media seconds divided by batch wall time. First output means the first worker wrote more than 768 bytes. Temp bytes are summed output files. The fixture generated the same short source for each batch and removed all outputs after verification.

| Sessions | Mode | Wall s | CPU s | Sum peak RSS MiB | Temporary output MiB | Media s/s | First output s | Video encoders |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | Copy | 0.045 | 0.048 | 21.6 | 6.58 | 44.00 | 0.041 | 0 |
| 1 | Software AV1 | 2.305 | 3.299 | 345.4 | 2.08 | 0.87 | 2.252 | 1 |
| 2 | Copy | 0.032 | 0.061 | 43.1 | 13.15 | 123.87 | 0.029 | 0 |
| 2 | Software AV1 | 2.557 | 6.514 | 695.3 | 4.15 | 1.56 | 2.168 | 2 |
| 4 | Copy | 0.040 | 0.123 | 86.1 | 26.30 | 200.42 | 0.028 | 0 |
| 4 | Software AV1 | 4.131 | 15.031 | 1,390.7 | 8.30 | 1.94 | 3.060 | 4 |
| 8 | Copy | 0.067 | 0.278 | 171.8 | 52.61 | 239.08 | 0.038 | 0 |
| 8 | Software AV1 | 9.078 | 35.053 | 2,763.0 | 16.61 | 1.76 | 4.684 | 8 |

This measures codec work, not client concurrency on TrueNAS. YouTube, HLS segment startup, Jellyfin session management, Quick Sync and real network buffering were outside the benchmark.

## TrueNAS update and Infuse

Back up the plugin binary directory and persistent Jellyfin configuration. Install the new version from the same catalog and restart the app. Keep your existing tools, subtitle settings and libraries. Run the usual VOD publication task so existing videos receive any additional supported versions. Test from the beginning, from a saved position and with both English subtitle tracks, including a 1080p60 or higher-quality clip. With **Log YouTube playback negotiation** enabled, inspect **VOD preferred an equivalent original source**, the source resolution cache and the native Jellyfin FFmpeg log. A real copy should show video and audio mapped as copy where both are compatible.

Infuse still requires an actual trace. The stock Jellyfin static HTTP route may proxy an internal HLS master when a client requests a static resource. The optimization leaves Infuse's request path, authentication and byte-range behavior unchanged. If playback still fails, capture the sanitized PlaybackInfo, request action, HTTP method, status, content type, Range presence, host category and whether FFmpeg started. Use [the playback recovery guide](PLAYBACK_RECOVERY.md) to collect those details. A trace is needed before adding any new delivery endpoint or making an Infuse compatibility claim.

For rollback, stop Jellyfin and restore the backed-up plugin directory and metadata or choose 5.1.1.0 from the catalog. The database schema and existing playback IDs remain compatible. Retain the original VOD libraries and cached subtitle data.
