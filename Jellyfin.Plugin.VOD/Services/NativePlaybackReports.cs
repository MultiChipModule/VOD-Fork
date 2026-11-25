using System;
using System.Linq;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.VOD.Services
{
    internal static class NativePlaybackReports
    {
        // Jellyfin distinguishes the visible item from its played version. A client reporting the
        // hidden alternate as the item creates a NowPlayingItem that catalog consumers cannot index.
        internal static bool Normalize(PlaybackProgressInfo report, Func<Guid, Video?> visible, Func<string, bool> isYouTube)
        {
            var ids = Resolve(report.ItemId, report.MediaSourceId, visible, isYouTube);
            if (ids is not { } resolved || (resolved.Primary == report.ItemId && report.Item is null))
            {
                return false;
            }

            report.ItemId = resolved.Primary;
            report.MediaSourceId = resolved.Source;
            report.Item = null; // Let the existing native handler serialize the visible item DTO.
            return true;
        }

        internal static bool Normalize(PlaybackStopInfo report, Func<Guid, Video?> visible, Func<string, bool> isYouTube)
        {
            var ids = Resolve(report.ItemId, report.MediaSourceId, visible, isYouTube);
            if (ids is not { } resolved || (resolved.Primary == report.ItemId && report.Item is null))
            {
                return false;
            }

            report.ItemId = resolved.Primary;
            report.MediaSourceId = resolved.Source;
            report.Item = null;
            return true;
        }

        internal static void NormalizeRouteArguments(IDictionary<string, object?> arguments, Guid userId,
            Func<Guid, string?, (Guid Primary, string Source)?> resolve)
        {
            if (!arguments.TryGetValue("itemId", out var itemId) || itemId is not Guid item
                || (arguments.TryGetValue("userId", out var requested) && requested is Guid selected && selected != userId))
                return;

            arguments.TryGetValue("mediaSourceId", out var mediaSource);
            if (resolve(item, mediaSource as string) is { } ids && ids.Primary != item)
            {
                arguments["itemId"] = ids.Primary;
                arguments["mediaSourceId"] = ids.Source;
            }
        }

        internal static (Guid Primary, string Source)? Resolve(Guid itemId, string? sourceId,
            Func<Guid, Video?> visible, Func<string, bool> isYouTube)
        {
            if (itemId == Guid.Empty && !Guid.TryParse(sourceId, out itemId))
                return null;

            if (visible(itemId) is not { } item
                || item.GetProviderId("VOD") is not { Length: > 0 } vodId || !isYouTube(vodId))
            {
                return null;
            }

            var primaryId = item.PrimaryVersionId ?? item.Id;
            if (visible(primaryId) is not { } primary || primary.PrimaryVersionId.HasValue
                || !string.Equals(primary.GetProviderId("VOD"), vodId, StringComparison.Ordinal))
            {
                return null;
            }

            var played = itemId;
            if (!string.IsNullOrWhiteSpace(sourceId) && !Guid.TryParse(sourceId, out played))
            {
                return null;
            }

            var versions = primary.GetAllVersions();
            if (!versions.Any(version => version.Id == itemId)
                || !versions.Any(version => version.Id == played)
                || visible(played) is not { } playedVersion
                || !string.Equals(playedVersion.GetProviderId("VOD"), vodId, StringComparison.Ordinal))
            {
                return null;
            }

            return (primaryId, played.ToString("N"));
        }
    }
}
