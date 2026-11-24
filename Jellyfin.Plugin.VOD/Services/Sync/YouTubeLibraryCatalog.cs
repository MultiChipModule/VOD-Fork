using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    internal sealed record ChannelSource(
        string Id, string Name, string? Avatar, string? Banner, IReadOnlyList<ChannelUpload> Uploads,
        IReadOnlyList<ChannelPlaylist> Playlists, IReadOnlyList<ChannelUpload>? PlaylistItems = null);

    internal sealed record ChannelUpload(VideoRecord Video, Guid ItemId, int RetentionDays);
    internal sealed record ChannelPlaylist(string Id, string Name, string? Artwork, string? Description, IReadOnlyList<string> VideoIds);
    internal sealed record ChannelNode(string Id, string Name, string? Avatar, string? Banner, IReadOnlyList<PlaylistNode> Playlists);
    internal sealed record PlaylistNode(
        string Id, string Name, string? Artwork, string? Description, IReadOnlyList<Guid> Items, bool IsUploads = false);

    internal static class YouTubeLibraryCatalog
    {
        internal static IReadOnlyList<ChannelNode> Build(
            IReadOnlyList<ChannelSource> sources, IReadOnlySet<string> excluded, DateTimeOffset now)
        {
            var uploads = sources.SelectMany(source => source.Uploads).ToHashSet();
            var retained = sources.SelectMany(source => source.Uploads.Concat(source.PlaylistItems ?? []))
                .Where(upload => upload.Video.NativeId is not null && upload.ItemId != Guid.Empty
                    && (upload.RetentionDays <= 0 || upload.Video.PublishedAt is not { } published
                        || published >= now.AddDays(-upload.RetentionDays)))
                .ToLookup(upload => upload.Video.NativeId!, StringComparer.Ordinal);

            // Prefer the upload owner's Jellyfin item. Tie-breaking by item id keeps playlist links
            // stable if a second configured source already imported the same provider video.
            var canonical = retained.ToDictionary(group => group.Key,
                group => group.OrderByDescending(uploads.Contains).ThenBy(upload => upload.ItemId).First().ItemId, StringComparer.Ordinal);

            return sources.GroupBy(source => source.Id, StringComparer.Ordinal)
                .Select(group => MakeChannel(group, canonical, retained, excluded))
                .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(channel => channel.Id, StringComparer.Ordinal).ToArray();
        }

        private static ChannelNode MakeChannel(
            IGrouping<string, ChannelSource> group, Dictionary<string, Guid> canonical,
            ILookup<string, ChannelUpload> retained, IReadOnlySet<string> excluded)
        {
            var source = group.First();
            var uploads = group.SelectMany(entry => entry.Uploads)
                .Where(upload => upload.Video.NativeId is { } id && retained[id].Contains(upload))
                .OrderByDescending(upload => upload.Video.PublishedAt)
                .ThenBy(upload => upload.Video.NativeId, StringComparer.Ordinal)
                .Select(upload => canonical[upload.Video.NativeId!]).Distinct().ToArray();
            var playlists = group.SelectMany(entry => entry.Playlists)
                .Where(playlist => !excluded.Contains(playlist.Id))
                .DistinctBy(playlist => playlist.Id, StringComparer.Ordinal)
                .Select(playlist => new PlaylistNode(playlist.Id, playlist.Name, playlist.Artwork, playlist.Description,
                    playlist.VideoIds.Where(canonical.ContainsKey).Select(id => canonical[id]).Distinct().ToArray()))
                .Where(playlist => playlist.Items.Count > 0)
                .OrderBy(playlist => playlist.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(playlist => playlist.Id, StringComparer.Ordinal);

            return new ChannelNode(group.Key, source.Name, source.Avatar, source.Banner,
                [new PlaylistNode("uploads", "Videos", null, null, uploads, IsUploads: true), .. playlists]);
        }
    }
}
