# Contributing

Work directly on the fork's `main`. Follow [the release policy](VERSIONING.md) before tagging. Preserve the upstream license and attribution.

On Linux x64 run the full suite with the checksum-verified Jellyfin FFmpeg 8.1.3 test binaries:

```sh
vod_test_tools=$(mktemp -d)
bash scripts/install-test-ffmpeg.sh "$vod_test_tools"
VOD_TEST_FFMPEG_DIR="$vod_test_tools" dotnet test --configuration Release
```

You can also point `VOD_TEST_FFMPEG_DIR` at an existing compatible Jellyfin FFmpeg build. The integration tests generate fixtures locally and require no YouTube access. Inspect test output and run package verification before a release. Do not include unredacted server logs in issues, commits, or artifacts.
