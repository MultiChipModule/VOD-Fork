# VOD-Fork implementation plan

The baseline is upstream VOD 5.0.0.0 at `6f3e91a4c8c319ff19930a6d0c13aedae5eb0815`. The project targets .NET 10 and references Jellyfin.Controller 12.1.0. Confirm binary compatibility and player behavior against the target server, reported as Jellyfin 12.4.0, before releasing.

## Phase 1 — Automatic English subtitles

Deliver a tested, installable subtitle build before changing the library hierarchy. Use `SLe8Ot4XTiM` as the manual reproduction video and use local fixtures for automated tests.

- Trace extraction, registration, and playback separately. The existing playback extraction prints the manifest and stream URLs. The HLS filter can only register subtitles that the selected manifest already advertises, so yt-dlp timed-text tracks outside that manifest have no path into this playback flow. Compare the reproduction video's selected HLS master with yt-dlp's `subtitles` and `automatic_captions` output to confirm where the track disappears.
- Reuse the existing extraction call and subtitle delivery path where feasible. Rank creator-uploaded English first, followed by original automatic English (`en-orig`), suitable untranslated automatic English (`en`), and explicit opt-in translations. Deduplicate English tracks, preserve configurable languages, and distinguish automatic captions in the player where supported.
- Register selectable text subtitles on all offered playback versions. Test WebVTT, conversion where necessary, expired URLs, unavailable captions, manual caption regressions, and direct play, remux and transcoding. Caption errors must leave video playable.
- Add bounded, expiring, atomic on-demand caching under plugin-owned storage. Expose the automatic caption, language, translation and cache controls using existing plugin configuration conventions.
- Verify the selectable English caption on Jellyfin Web and publish a reviewable build with installation and rollback steps.

## Phase 2 — Presentation

Remove generated numeric title prefixes where they appear in client displays. Preserve original YouTube titles, landscape thumbnails and available channel artwork. Check the current VOD metadata and Jellyfin client behavior before changing title or numbering fields.

## Phase 3 — Channel-first YouTube library

The required browse path is **YouTube → Channel name → Playlist name → Video**. The library root contains one entry per channel, sorted alphabetically by its original display name. Use the stable YouTube channel ID for identity, and update the title, avatar and banner in place when metadata changes.

Each channel contains `Videos` first, with all retained uploads sorted newest first. Discovered playlists follow alphabetically by original title, with their artwork and descriptions. Keep their titles unprefixed and preserve the original member order. Sync adds, removes and reorders memberships and supports per-playlist exclusions by stable playlist ID. A real playlist named `Videos` has its own ID and remains distinct from the synthetic node.

Resolve YouTube handles and supported channel and playlist URLs to stable channel IDs before publishing. Use the owning channel of each playlist for grouping. An item from another channel in a playlist retains the same identity and is not counted as an upload of the playlist owner.

### Jellyfin architecture and validation

VOD currently publishes an episode with a single `ParentId`, `SeasonId` and `SeriesId`. Jellyfin's Season view calls the Series episode query and does not use a Season's linked children. A Channel Series with one Season per playlist therefore cannot render a canonical Episode in several Seasons through the standard TV view.

Native Playlists store ordered links to item IDs, but `PlaylistManager.CreatePlaylist` places them under Jellyfin's Playlists root. Collections can link existing item IDs and accept a parent folder, but their client display and member ordering need verification. Virtual folders configure library roots and media locations. They do not model ordered, many-to-many item membership. Generic folders can include linked children on some server browse paths. Verify those paths and the client before relying on them.

Prototype the hierarchy against the target Jellyfin server with two channels and a video shared across two playlists. First test channel folders in the YouTube library with ordered playlist nodes that link to existing items. Confirm that a regular user can browse all four levels, that playlist order survives synchronization and scans, and that opening the same video from either playlist yields the same Jellyfin item ID and resume position. Test playlist entries on the library surface clients actually use, since the public playlist creation API does not expose a channel parent.

If the standard Jellyfin client cannot render ordered linked nodes in this hierarchy, implement a dedicated channel-first browse view backed by the plugin's catalog, with playback handed off to the canonical Jellyfin item. Retain a native channel-first view with canonical uploads where clients support it, and document which clients need the dedicated view to browse all playlists. If the dedicated view requires a client change, document that dependency before promising support. Publishing separate root-level series for each playlist would fail the library acceptance criteria.

Reference server code inspected at Jellyfin commit [c3686c5](https://github.com/jellyfin/jellyfin/tree/c3686c56cce70c0cedebfc632346b20f3fee6dde). Verify the selected architecture on the target server and clients before migration.

### Identity, synchronization and retention

- Keep one canonical video identity per provider and video ID. Store playlist membership separately with position. Resolve all views to the same Jellyfin playback item and preserve the existing VOD quality-version grouping.
- Reuse published item IDs during migration and renames wherever possible. Verify preservation of played state and resume data before moving existing items. Report and handle ambiguous existing duplicates before cleanup.
- Apply the configured 100-day retention policy using publication dates. Evaluate eligibility consistently for `Videos` and regular playlists. Reconcile playlist memberships separately from canonical item deletion. A removed or excluded playlist must not delete a video still retained through another source. A failed or incomplete discovery pass must not authorize pruning.

### Automated acceptance tests

| Check | Expected behavior |
| --- | --- |
| Channel grouping | Multiple playlists with the same owning channel ID produce one top-level channel. Identical display names with different IDs remain separate. |
| Channel sorting | Display names sort alphabetically with a deterministic tie-breaker. |
| Playlist sorting | The synthetic `Videos` appears first, even ahead of a playlist titled `A`. Other entries sort by original name. |
| Video ordering | `Videos` sorts by publication date descending. Other playlists keep source order across sync and reorder operations. |
| Shared identity | A video in several playlists resolves to one canonical item ID. Playback and resume state are shared across entries. |
| Rename | Updating a channel name or handle updates the display and sort position with no new channel or video IDs. |
| Retention | A fixed clock covers both sides of the 100-day boundary, shared memberships and incomplete sync. |
| Exclusion | Excluding a playlist removes its view and leaves retained shared videos playable. |

Unit and publisher tests should cover catalog behavior. An integration check on the target Jellyfin API and client must additionally verify navigation, visible ordering, artwork, and cross-playlist resume before Phase 3 is accepted.

## Phase 4 — Maintenance and deployment

Strengthen retention reconciliation and dry-run cleanup, subtitle cache maintenance, migration and rollback, reproducible build and release automation, and the fork manifest and TrueNAS documentation. Phase 3 already enforces retention in the new library views.
