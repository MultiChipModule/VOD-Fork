using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services
{
    internal static class YouTubeBudgetSelection
    {
        internal sealed record Source(Guid Id, string? Path, IReadOnlyList<MediaStream> Streams);

        internal static Guid? Choose(Source current, IEnumerable<Source> alternatives, int budget,
            int? audioIndex, int? subtitleIndex)
        {
            if (budget <= 0 || !StreamDescriber.HasCurrentYouTubeLayout(current.Streams)
                || current.Streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video) is not { } video
                || TotalBitrate(current.Streams) is not { } currentRate || currentRate <= budget)
            {
                return null;
            }

            return alternatives.Where(candidate => candidate.Id != current.Id
                    && StreamDescriber.HasCurrentYouTubeLayout(candidate.Streams))
                .Select(candidate => (Source: candidate,
                    Video: candidate.Streams.First(stream => stream.Type == MediaStreamType.Video),
                    Rate: TotalBitrate(candidate.Streams)))
                .Where(candidate => candidate.Rate is > 0 && candidate.Rate <= budget
                    && MatchesOriginalVariant(candidate.Source, candidate.Video)
                    && CanUseLowerOriginal(video, candidate.Video)
                    && MatchesAudio(current.Streams, candidate.Source.Streams, audioIndex)
                    && MatchesSubtitle(current.Streams, candidate.Source.Streams, subtitleIndex))
                .OrderByDescending(candidate => candidate.Video.Height)
                .ThenByDescending(candidate => candidate.Video.ReferenceFrameRate)
                .ThenByDescending(candidate => candidate.Rate)
                .Select(candidate => (Guid?)candidate.Source.Id)
                .FirstOrDefault();
        }

        private static long? TotalBitrate(IReadOnlyList<MediaStream> streams)
        {
            var av = streams.Where(stream => !stream.IsExternal
                && stream.Type is MediaStreamType.Video or MediaStreamType.Audio).ToArray();
            return av.All(stream => stream.BitRate is > 0) ? av.Sum(stream => (long)stream.BitRate!.Value) : null;
        }

        private static bool MatchesOriginalVariant(Source source, MediaStream video)
        {
            var (height, codec) = VersionGroup.TierOf(source.Path);
            return height.HasValue && height == video.Height && codec is not null
                && string.Equals(codec, video.Codec, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanUseLowerOriginal(MediaStream original, MediaStream candidate) =>
            candidate.Height is > 0 && candidate.Height <= original.Height && candidate.Width <= original.Width
            && candidate.Codec is not null && string.Equals(candidate.Codec, original.Codec, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Profile, original.Profile, StringComparison.OrdinalIgnoreCase)
            && candidate.VideoRangeType == original.VideoRangeType && candidate.BitDepth == original.BitDepth
            && (original.Level is null ? candidate.Level is null : candidate.Level <= original.Level)
            && (original.ReferenceFrameRate is null ? candidate.ReferenceFrameRate is null
                : candidate.ReferenceFrameRate <= original.ReferenceFrameRate + 0.05f);

        private static bool MatchesAudio(IReadOnlyList<MediaStream> first, IReadOnlyList<MediaStream> second, int? index)
        {
            var original = FindAudio(first, index);
            var candidate = FindAudio(second, index);
            return original is not null && candidate is not null
                && original.Index == candidate.Index && original.IsExternal == candidate.IsExternal
                && string.Equals(original.Codec, candidate.Codec, StringComparison.OrdinalIgnoreCase)
                && string.Equals(original.Language, candidate.Language, StringComparison.OrdinalIgnoreCase)
                && string.Equals(original.Title, candidate.Title, StringComparison.Ordinal)
                && original.Channels == candidate.Channels && original.SampleRate == candidate.SampleRate;
        }

        private static MediaStream? FindAudio(IReadOnlyList<MediaStream> streams, int? index)
        {
            var audio = streams.Where(stream => stream.Type == MediaStreamType.Audio).ToArray();
            return index is >= 0 ? audio.FirstOrDefault(stream => stream.Index == index)
                : audio.FirstOrDefault(stream => stream.IsDefault) ?? audio.FirstOrDefault();
        }

        private static bool MatchesSubtitle(IReadOnlyList<MediaStream> first, IReadOnlyList<MediaStream> second, int? index)
        {
            if (index is null or < 0)
            {
                return true;
            }

            var original = first.FirstOrDefault(stream => stream.Type == MediaStreamType.Subtitle && stream.Index == index);
            return original is { IsExternal: true, Path: { Length: > 0 } }
                && second.Any(stream => stream.Type == MediaStreamType.Subtitle && stream.Index == index
                    && stream.IsExternal && string.Equals(original.Path, stream.Path, StringComparison.Ordinal));
        }
    }
}
