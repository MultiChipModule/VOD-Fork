using System;
using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YouTubeBudgetSelectionTests
    {
        [Fact]
        public void SelectsHighestCompatibleRungWithinBudgetAndPreservesTrackIndices()
        {
            var original = Source(1080, 5_000_000);
            var aboveBudget = Source(720, 2_000_000);
            var wrongAudio = Source(480, 1_000_000);
            wrongAudio.Streams[0].Language = "jpn";
            var compatible = Source(480, 900_000);
            var lower = Source(360, 500_000);
            Assert.Equal(compatible.Id, YouTubeBudgetSelection.Choose(
                original, [aboveBudget, wrongAudio, lower, compatible], 1_500_000, 0, 1001));
        }

        [Fact]
        public void KeepsCurrentSourceIfItFitsOrAnExplicitTrackWouldChange()
        {
            var original = Source(1080, 5_000_000);
            var lower = Source(720, 2_000_000);
            Assert.Null(YouTubeBudgetSelection.Choose(original, [lower], 6_000_000, 0, 1001));
            lower.Streams[2].Path = "/subtitles/different.vtt";
            Assert.Null(YouTubeBudgetSelection.Choose(original, [lower], 3_000_000, 0, 1001));
            Assert.Equal(lower.Id, YouTubeBudgetSelection.Choose(original, [lower], 3_000_000, 0, -1));
        }

        [Fact]
        public void NeverSelectsEstimatedOrMismatchedVariantMetadata()
        {
            var original = Source(1080, 5_000_000);
            var unknown = Source(720, 2_000_000);
            unknown.Streams[0].BitRate = null;
            var mismatched = Source(480, 1_000_000);
            mismatched.Streams[1].Height = 400;
            var changedCodec = Source(480, 900_000);
            changedCodec.Streams[1].Codec = "vp9";
            Assert.Null(YouTubeBudgetSelection.Choose(original, [unknown, mismatched, changedCodec], 3_000_000, 0, 1001));
        }

        [Fact]
        public void PublishesBoundedLowerOriginalsWithoutChangingExistingTierIds()
        {
            VideoQuality[] qualities = [new(1080, "h264", 60), new(2160, "vp9", 60),
                new(720, "h264", 30), new(480, "h264", 30), new(360, "h264", 30), new(240, "h264", 30)];
            var legacy = QualityLadder.OfferedTiers(qualities);
            var current = QualityLadder.YouTubeTiers(qualities);
            Assert.Equal(legacy, current.Take(legacy.Count));
            Assert.Equal([720, 480, 360], current.Skip(legacy.Count).Select(tier => tier.Height));
        }

        private static YouTubeBudgetSelection.Source Source(int height, int videoRate)
        {
            var path = $"http://localhost:8096/VOD/stream/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.m3u8?height={height}&codec=h264";
            return new(Guid.NewGuid(), path,
            [
                new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac", Channels = 2,
                    BitRate = 128_000, SampleRate = 48000, Language = "eng", IsDefault = true },
                new MediaStream { Type = MediaStreamType.Video, Index = 1, Codec = "h264", Height = height,
                    Width = height * 16 / 9, BitRate = videoRate, Profile = "High", Level = 40, RealFrameRate = 30, BitDepth = 8 },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 1001, Codec = "vtt", IsExternal = true,
                    Path = "/subtitles/auto.vtt" }
            ]);
        }
    }
}
