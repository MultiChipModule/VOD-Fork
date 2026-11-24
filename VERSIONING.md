# Versioning

Use four numeric components in `Directory.Build.props`, the authoritative version source.

| Component | Use |
| --- | --- |
| MAJOR | Breaking compatibility or architecture |
| MINOR | New features or significant functionality |
| PATCH | Backward-compatible fixes, compatibility work and minor improvements |
| REVISION | Packaging-only correction |

Use the highest category in the release, increment its component, and reset all components to its right. Ordinary functional releases have revision zero.

Before committing a release version, check the published GitHub releases, remote Git tags and Jellyfin catalog. Increment from the highest published version and confirm the result is unused in all three places. Never reissue a tag or asset with different contents. The target ABI and upstream plugin GUID are independent of release numbers.

Assembly and file versions use the build property. The packager reads it for the ZIP, SHA-256 filename, plugin metadata and manifest entry. GitHub Actions verifies that the tag matches it and the version is newer than the catalog. GitHub Actions builds, runs required tests, creates the release with verified assets, compares the hosted ZIP with the locally built ZIP, and then updates the manifest. Earlier catalog entries remain available for rollback.
