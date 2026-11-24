using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.VOD.Services
{
    // Jellyfin has already applied the device profile and the user's permissions by this point.
    // Only rank returned sources. Keep their IDs, paths and negotiated URLs together.
    internal static class YouTubePlaybackSelection
    {
        internal enum Delivery { DirectPlay, Remux, AudioEncode, VideoEncode, Unknown }

        private const TranscodeReason AudioReasons = TranscodeReason.AudioCodecNotSupported
            | TranscodeReason.AudioChannelsNotSupported | TranscodeReason.AudioProfileNotSupported
            | TranscodeReason.AudioSampleRateNotSupported | TranscodeReason.AudioBitDepthNotSupported
            | TranscodeReason.AudioBitrateNotSupported | TranscodeReason.SecondaryAudioNotSupported
            | TranscodeReason.UnknownAudioStreamInfo;

        internal static Delivery Classify(MediaSourceInfo source)
        {
            if (source.VideoStream is not { Codec: { Length: > 0 } } video)
            {
                return Delivery.Unknown;
            }

            // Internal VOD HLS is served by Jellyfin's native remux/transcode path. A direct-play
            // flag alone is not evidence that a client can read an internal manifest.
            if (source.SupportsDirectPlay && !IsVodHls(source, out _))
            {
                return Delivery.DirectPlay;
            }

            var queryStart = source.TranscodingUrl?.IndexOf('?', StringComparison.Ordinal) ?? -1;
            if ((!source.SupportsTranscoding && !source.SupportsDirectStream)
                || string.IsNullOrEmpty(source.TranscodingUrl)
                || queryStart < 0)
            {
                return Delivery.Unknown;
            }

            var query = QueryHelpers.ParseQuery(source.TranscodingUrl[queryStart..]);
            if (IsFalse(query, "AllowVideoStreamCopy")
                || !IncludesCodec(query, "VideoCodec", video.Codec)
                || OverCap(query, "MaxWidth", video.Width)
                || OverCap(query, "MaxHeight", video.Height)
                || OverCap(query, "MaxFramerate", video.ReferenceFrameRate)
                || OverCap(query, "VideoBitrate", video.BitRate)
                || OverCap(query, $"{video.Codec}-level", video.Level)
                || OverCap(query, $"{video.Codec}-videobitdepth", video.BitDepth)
                || IsProfileMismatch(query, video)
                || (source.TranscodeReasons & ~(AudioReasons | TranscodeReason.ContainerNotSupported)) != 0
                || (bool.TryParse(query.GetValueOrDefault("AlwaysBurnInSubtitleWhenTranscoding"), out var burn) && burn)
                || (source.DefaultSubtitleStreamIndex is { } subtitleIndex
                    && !source.MediaStreams.Any(stream => stream.Index == subtitleIndex
                        && stream.Type == MediaStreamType.Subtitle && stream.IsExternal && stream.IsTextSubtitleStream
                        && stream.DeliveryMethod is SubtitleDeliveryMethod.External or SubtitleDeliveryMethod.Hls)))
            {
                return Delivery.VideoEncode;
            }

            var audio = source.GetDefaultAudioStream(source.DefaultAudioStreamIndex);
            return audio is not null && (IsFalse(query, "AllowAudioStreamCopy")
                || !IncludesCodec(query, "AudioCodec", audio.Codec)
                || OverCap(query, "AudioBitrate", audio.BitRate)
                || OverCap(query, "MaxAudioChannels", audio.Channels)
                || (source.TranscodeReasons & AudioReasons) != 0)
                ? Delivery.AudioEncode : Delivery.Remux;
        }

        internal static MediaSourceInfo? PreferCopy(MediaSourceInfo[] sources, int? requestedBitrate)
        {
            if (sources.Length < 2 || !IsVodHls(sources[0], out var group)
                || sources[0].VideoStream is not { Height: > 0, Width: > 0 } baseline)
            {
                return null;
            }

            var first = sources[0];
            var firstRank = Classify(first);
            if (firstRank is Delivery.Unknown or Delivery.DirectPlay)
            {
                return null;
            }

            var firstAudio = first.GetDefaultAudioStream(first.DefaultAudioStreamIndex);
            var selectedIndex = 0;
            var best = firstRank;
            for (var i = 1; i < sources.Length; i++)
            {
                var candidate = sources[i];
                if (!IsVodHls(candidate, out var candidateGroup) || candidateGroup != group
                    || candidate.RequiresOpening || candidate.IsInfiniteStream
                    || !StreamDescriber.HasCurrentYouTubeLayout(candidate.MediaStreams)
                    || candidate.VideoStream is not { } video
                    || !MatchesPublishedTier(candidate, video)
                    || !EquivalentVideo(baseline, video)
                    || !EquivalentAudio(firstAudio, candidate.GetDefaultAudioStream(candidate.DefaultAudioStreamIndex))
                    || (requestedBitrate is > 0 && (candidate.Bitrate is null || candidate.Bitrate > requestedBitrate)))
                {
                    continue;
                }

                var rank = Classify(candidate);
                if (rank < best)
                {
                    selectedIndex = i;
                    best = rank;
                }
            }

            if (selectedIndex == 0)
            {
                return null;
            }

            var selected = sources[selectedIndex];
            Array.Copy(sources, 0, sources, 1, selectedIndex);
            sources[0] = selected;
            return selected;
        }

        private static bool MatchesPublishedTier(MediaSourceInfo source, MediaStream video)
        {
            var (height, codec) = VersionGroup.TierOf(source.Path);
            return height is null || (video.Height == height && string.Equals(video.Codec, codec, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProfileMismatch(Dictionary<string, StringValues> query, MediaStream video)
        {
            var profiles = query.GetValueOrDefault($"{video.Codec}-profile").ToString();
            return profiles.Length > 0 && (string.IsNullOrWhiteSpace(video.Profile)
                || !profiles.Split(',', StringSplitOptions.TrimEntries).Contains(
                    video.Profile.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase));
        }

        private static bool EquivalentVideo(MediaStream first, MediaStream candidate) =>
            candidate.Width == first.Width && candidate.Height == first.Height
            && (first.ReferenceFrameRate is not { } fps || candidate.ReferenceFrameRate >= fps - 0.05f)
            && (first.BitDepth is null || candidate.BitDepth >= first.BitDepth)
            && first.VideoRangeType == candidate.VideoRangeType
            && (first.BitRate is not > 0 || candidate.BitRate is not > 0 || candidate.BitRate >= first.BitRate * 0.4);

        private static bool EquivalentAudio(MediaStream? first, MediaStream? candidate) =>
            first is null || (candidate is not null && candidate.Language == first.Language
                && (first.Channels is null || candidate.Channels >= first.Channels));

        private static bool IsVodHls(MediaSourceInfo source, out Guid group)
        {
            group = default;
            if (!Uri.TryCreate(source.Path, UriKind.Absolute, out var path))
            {
                return false;
            }

            var index = path.AbsolutePath.LastIndexOf("/VOD/stream/", StringComparison.OrdinalIgnoreCase);
            return index >= 0 && path.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(path.AbsolutePath.AsSpan(index + "/VOD/stream/".Length, path.AbsolutePath.Length - index - "/VOD/stream/".Length - 5), out group);
        }

        private static bool IsFalse(Dictionary<string, StringValues> query, string key) =>
            bool.TryParse(query.GetValueOrDefault(key), out var value) && !value;

        private static bool IncludesCodec(Dictionary<string, StringValues> query, string key, string? codec) =>
            codec is not null && query.GetValueOrDefault(key).ToString().Split(',', StringSplitOptions.TrimEntries)
                .Any(value => value.Equals("copy", StringComparison.OrdinalIgnoreCase)
                    || value.Equals(codec, StringComparison.OrdinalIgnoreCase));

        private static bool OverCap(Dictionary<string, StringValues> query, string key, double? value) =>
            double.TryParse(query.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit)
            && limit > 0 && (value is null || value > limit);
    }
}
