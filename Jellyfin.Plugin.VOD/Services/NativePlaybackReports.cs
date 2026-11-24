using System;
using System.Linq;
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
            if (ids is not { } resolved)
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
            if (ids is not { } resolved)
            {
                return false;
            }

            report.ItemId = resolved.Primary;
            report.MediaSourceId = resolved.Source;
            report.Item = null;
            return true;
        }

        private static (Guid Primary, string Source)? Resolve(Guid itemId, string? sourceId,
            Func<Guid, Video?> visible, Func<string, bool> isYouTube)
        {
            if (visible(itemId) is not { PrimaryVersionId: { } primaryId } item
                || primaryId == itemId || item.GetProviderId("VOD") is not { Length: > 0 } vodId
                || !isYouTube(vodId) || visible(primaryId) is not { } primary
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
