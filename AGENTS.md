# VOD-Fork repository instructions

- Work on the existing checkout. Direct commits to the fork's main branch are authorized. Keep upstream history and licensing.
- Read [VERSIONING.md](VERSIONING.md) before changing any version or publishing.
- Preserve the Jellyfin plugin GUID, target ABI, stable playback item IDs, and prior release files.
- Run the full Release test suite, including the pinned FFmpeg integration tests, before tagging.
- Update the changelog and installation or rollback instructions when the changes warrant it.
- Do not deploy to the user's Jellyfin server. Keep production logs and credentials out of the repository.
