using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YouTubePlaybackSelectionTests
    {
        private const string BasePath = "http://localhost:8096/jellyfin/VOD/stream/8cf7060d-454b-40b8-97bc-c402fc222c90.m3u8";

        [Theory]
        [InlineData("h264", "vp9")]
        [InlineData("vp9", "h264")]
        [InlineData("av1", "vp9")]
        [InlineData("hevc", "av1")]
        public void PrefersEquivalentOriginalNegotiatedByServer(string compatible, string incompatible)
        {
            var baseline = Source(incompatible, compatible);
            var original = Source(compatible, compatible);
            MediaSourceInfo[] result = [baseline, original];
            Assert.Equal(YouTubePlaybackSelection.Delivery.VideoEncode, YouTubePlaybackSelection.Classify(baseline));
            Assert.Equal(YouTubePlaybackSelection.Delivery.Remux, YouTubePlaybackSelection.Classify(original));
            Assert.Same(original, YouTubePlaybackSelection.PreferCopy(result, 10_000_000));
            Assert.Equal(new[] { original, baseline }, result);
            Assert.Contains($"VideoCodec={compatible}", result[0].TranscodingUrl, StringComparison.Ordinal);
            Assert.Contains($"codec={compatible}", result[0].Path, StringComparison.Ordinal);
        }

        [Fact]
        public void UsesActualJellyfinDeviceProfileNegotiation()
        {
            var transcoder = Substitute.For<ITranscoderSupport>();
            transcoder.CanEncodeToAudioCodec(Arg.Any<string>()).Returns(true);
            var builder = new StreamBuilder(transcoder, NullLogger.Instance);
            var profile = new DeviceProfile
            {
                MaxStreamingBitrate = 20_000_000,
                TranscodingProfiles =
                [
                    new TranscodingProfile
                    {
                        Type = DlnaProfileType.Video, Context = EncodingContext.Streaming,
                        Container = "mp4", Protocol = MediaStreamProtocol.hls,
                        VideoCodec = "h264", AudioCodec = "aac"
                    }
                ]
            };
            var prior = Source("vp9", "h264");
            var alternative = Source("h264", "h264");
            foreach (var source in new[] { prior, alternative })
            {
                source.Container = "hls";
                source.VideoStream.BitRate = 4_000_000;
                source.MediaStreams[0].BitRate = 128_000;
                var result = builder.GetOptimalVideoStream(new MediaOptions
                {
                    Profile = profile, ItemId = Guid.NewGuid(), MediaSources = [source],
                    AllowAudioStreamCopy = true, AllowVideoStreamCopy = true, EnableDirectStream = false
                });
                Assert.NotNull(result);
                Assert.Equal(PlayMethod.Transcode, result.PlayMethod);
                source.TranscodingUrl = result.ToUrl(null, null, null);
                source.TranscodeReasons = result.TranscodeReasons;
                Assert.Contains("VideoCodec=h264", source.TranscodingUrl, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Same(alternative, YouTubePlaybackSelection.PreferCopy([prior, alternative], profile.MaxStreamingBitrate));
        }

        [Fact]
        public void RejectsLowerResolutionFpsBitDepthAndOverBudgetSources()
        {
            var baseline = Source("vp9", "av1");
            var lowerResolution = Source("av1", "av1", height: 720, width: 1280);
            var lowerFps = Source("av1", "av1", fps: 30);
            var overBudget = Source("av1", "av1", bitrate: 11_000_000);
            var lowerDepth = Source("av1", "av1");
            baseline.VideoStream.BitDepth = 10;
            lowerDepth.VideoStream.BitDepth = 8;
            Assert.Null(YouTubePlaybackSelection.PreferCopy([baseline, lowerResolution, lowerFps, overBudget, lowerDepth], 10_000_000));
        }

        [Fact]
        public void RejectsNativeVideoConstraintsAndAllowsAudioOnlyWork()
        {
            var source = Source("h264", "h264", audio: "opus");
            Assert.Equal(YouTubePlaybackSelection.Delivery.AudioEncode, YouTubePlaybackSelection.Classify(source));
            source.TranscodeReasons = TranscodeReason.VideoLevelNotSupported;
            Assert.Equal(YouTubePlaybackSelection.Delivery.VideoEncode, YouTubePlaybackSelection.Classify(source));
            source.TranscodeReasons = 0;
            source.TranscodingUrl += "&MaxFramerate=30";
            Assert.Equal(YouTubePlaybackSelection.Delivery.VideoEncode, YouTubePlaybackSelection.Classify(source));
        }

        [Fact]
        public void ExternalTextCanBeRenderedWithoutEncoding()
        {
            var source = Source("h264", "h264");
            source.MediaStreams =
            [
                .. source.MediaStreams,
                new MediaStream { Codec = "vtt", Type = MediaStreamType.Subtitle, Index = 1000, IsExternal = true, DeliveryMethod = SubtitleDeliveryMethod.External },
                new MediaStream { Codec = "vtt", Type = MediaStreamType.Subtitle, Index = 1001, IsExternal = true, DeliveryMethod = SubtitleDeliveryMethod.External }
            ];
            foreach (var index in new[] { 1000, 1001 })
            {
                source.DefaultSubtitleStreamIndex = index;
                Assert.Equal(YouTubePlaybackSelection.Delivery.Remux, YouTubePlaybackSelection.Classify(source));
            }

            source.MediaStreams[^1].DeliveryMethod = SubtitleDeliveryMethod.Encode;
            Assert.Equal(YouTubePlaybackSelection.Delivery.VideoEncode, YouTubePlaybackSelection.Classify(source));
        }

        [Fact]
        public void DoesNotUseInventedProfilesOrUnrelatedMediaSources()
        {
            var first = Source("vp9", "h264");
            var compatible = Source("h264", "h264");
            compatible.Path = "https://example.com/file.mkv";
            compatible.SupportsDirectPlay = true;
            Assert.Null(YouTubePlaybackSelection.PreferCopy([first, compatible], null));
            first.TranscodingUrl = null;
            Assert.Equal(YouTubePlaybackSelection.Delivery.Unknown, YouTubePlaybackSelection.Classify(first));
            Assert.Null(YouTubePlaybackSelection.PreferCopy([first, compatible], null));
        }

        [Fact]
        public void ExplicitVersionResumeAndTrackRequestsBypassReordering()
        {
            var args = new Dictionary<string, object?>();
            Assert.False(CaptionPlaybackDispatch.IsAutomaticSelection("GetPlaybackInfo", args));
            Assert.True(CaptionPlaybackDispatch.IsAutomaticSelection("GetPostedPlaybackInfo", args));
            foreach (var (key, value) in new (string, object)[]
            {
                ("mediaSourceId", "explicit"), ("liveStreamId", "live"),
                ("startTimeTicks", 10000L), ("audioStreamIndex", 0), ("subtitleStreamIndex", -1)
            })
            {
                args[key] = value;
                Assert.False(CaptionPlaybackDispatch.IsAutomaticSelection("GetPostedPlaybackInfo", args));
                args.Clear();
            }
        }

        [Fact]
        public void OriginalCodecTiersKeepPreviouslyPublishedIdentitiesAndStayBounded()
        {
            VideoQuality[] qualities =
            [
                new(1080, "h264", 60), new(2160, "vp9", 60),
                new(2160, "av1", 60), new(1080, "hevc", 60), new(2160, "av1", 30)
            ];
            var existing = QualityLadder.OfferedTiers(qualities);
            var alternatives = QualityLadder.YouTubeTiers(qualities);
            Assert.Equal(existing, alternatives.Take(existing.Count));
            Assert.Equal(4, alternatives.Count);
            Assert.Contains(new Tier(2160, "av1", 60), alternatives);
            Assert.Contains(new Tier(1080, "hevc", 60), alternatives);
        }

        private static MediaSourceInfo Source(string video, string requested, string audio = "aac",
            int height = 1080, int width = 1920, int fps = 60, int bitrate = 7_000_000) =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = $"{BasePath}?height={height}&codec={video}",
                SupportsTranscoding = true,
                Bitrate = bitrate,
                TranscodingUrl = $"/videos/id/master.m3u8?VideoCodec={requested}&AudioCodec=aac",
                MediaStreams =
                [
                    new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = audio, Language = "eng", Channels = 2 },
                    new MediaStream { Type = MediaStreamType.Video, Index = 1, Codec = video, Width = width, Height = height, RealFrameRate = fps }
                ]
            };
    }
}
