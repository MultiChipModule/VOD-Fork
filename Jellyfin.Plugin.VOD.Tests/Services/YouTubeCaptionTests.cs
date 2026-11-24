using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YouTubeCaptionTests : IDisposable
    {
        private const string VideoUrl = "https://www.youtube.com/watch?v=SLe8Ot4XTiM";
        private const string TimedText = "https://www.youtube.com/api/timedtext";
        private const string Vtt = "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello\n";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        [Fact]
        public void ReadsCaptionFallbacksAlongsideUnchangedHlsSelection()
        {
            var source = YtDlpService.ParseYouTubeResolution(
                """
                {
                  "manifest_url": "https://manifest.googlevideo.com/master.m3u8",
                  "url": "https://example.com/video",
                  "subtitles": {
                    "en-US": [{"ext":"vtt","url":"https://www.youtube.com/api/timedtext?manual=1"}],
                    "fr": [{"ext":"vtt","url":"https://www.youtube.com/api/timedtext?lang=fr"}]
                  },
                  "automatic_captions": {
                    "en": [{"ext":"vtt","url":"https://www.youtube.com/api/timedtext?lang=es&tlang=en"},
                           {"ext":"vtt","url":"https://www.youtube.com/api/timedtext?lang=en&tlang=en"}],
                    "en-orig": [{"ext":"json3","url":"https://www.youtube.com/api/timedtext?lang=en&fmt=json3"},
                                {"ext":"vtt","url":"https://www.youtube.com/api/timedtext?lang=en&fmt=vtt"}]
                  }
                }
                """);

            Assert.NotNull(source);
            Assert.True(source.IsManifest);
            Assert.Equal("https://manifest.googlevideo.com/master.m3u8", source.Url);
            Assert.Equal(
                [CaptionKind.Creator, CaptionKind.OriginalAutomatic, CaptionKind.Automatic, CaptionKind.Translated],
                source.EnglishCaptions.Select(track => track.Kind));
        }

        [Fact]
        public void MissingCreatorCaptionsSelectOriginalEnglishAndIgnoreTranslationsByDefault()
        {
            var source = YtDlpService.ParseYouTubeResolution(
                """
                {"url":"https://example.com/video","automatic_captions":{
                    "en":[{"ext":"vtt","url":"https://www.youtube.com/api/timedtext?lang=es&tlang=en"}],
                    "en-orig":[{"ext":"vtt","url":"https://www.youtube.com/api/timedtext?lang=en"}]
                }}
                """);

            Assert.NotNull(source);
            Assert.False(source.IsManifest);
            var config = new PluginConfiguration();
            Assert.Equal(CaptionKind.OriginalAutomatic, Assert.Single(source.EnglishCaptions,
                track => CaptionCache.IsEnabled(track.Kind, config)).Kind);
            config.AllowTranslatedEnglishCaptions = true;
            Assert.All(source.EnglishCaptions, track => Assert.True(CaptionCache.IsEnabled(track.Kind, config)));
            config.EnableAutomaticEnglishCaptions = false;
            Assert.All(source.EnglishCaptions, track => Assert.False(CaptionCache.IsEnabled(track.Kind, config)));
            Assert.True(CaptionCache.IsEnabled(CaptionKind.Creator, config));
        }

        [Theory]
        [InlineData("https://www.youtube.com.evil.example/api/timedtext")]
        [InlineData("http://www.youtube.com/api/timedtext")]
        [InlineData("https://www.youtube.com:444/api/timedtext")]
        [InlineData("https://user@www.youtube.com/api/timedtext")]
        [InlineData("https://www.youtube.com/redirect")]
        public void RejectsNonTimedTextUrls(string url) =>
            Assert.False(YouTubeCaption.IsTimedText(new Uri(url)));

        [Theory]
        [InlineData("WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello", HttpStatusCode.OK, true)]
        [InlineData("WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello", HttpStatusCode.Forbidden, false)]
        [InlineData("<html>An error page</html>", HttpStatusCode.OK, false)]
        [InlineData("WEBVTT\n\n", HttpStatusCode.OK, false)]
        public async Task DownloadsOnlyValidWebVtt(string body, HttpStatusCode status, bool expected)
        {
            using var client = new HttpClient(new CaptionHandler(body, status));
            var cache = new CaptionCache(_directory, client);
            var config = new PluginConfiguration();
            var track = new YouTubeCaption(new Uri(TimedText), CaptionKind.OriginalAutomatic);
            var cached = await cache.FetchAsync(VideoUrl, track, config, CancellationToken.None);
            Assert.Equal(expected, cached is not null);
            Assert.Equal(cached, cache.Find(VideoUrl, config, creator: false));
            if (cached is not null)
            {
                Assert.Equal(body + "\n", File.ReadAllText(cached.Path));
                Assert.Single(Directory.EnumerateFiles(_directory));
            }
        }

        [Fact]
        public void CachePrunesExpiredAndOverBudgetFiles()
        {
            var cache = new CaptionCache(_directory);
            var config = new PluginConfiguration { CaptionCacheHours = 2, CaptionCacheMiB = 4 };
            var stale = cache.Store(VideoUrl, CaptionKind.Creator, Vtt, config);
            File.SetLastWriteTimeUtc(stale.Path, DateTime.UtcNow.AddHours(-3));
            Assert.Null(cache.Find(VideoUrl, config, creator: true));
            var first = cache.Store("first", CaptionKind.Automatic, Vtt + new string('a', 3 * 1024 * 1024), config);
            Assert.False(File.Exists(stale.Path));
            var second = cache.Store("second", CaptionKind.Automatic, Vtt + new string('b', 2 * 1024 * 1024), config);
            Assert.False(File.Exists(first.Path));
            Assert.True(File.Exists(second.Path));
        }

        [Fact]
        public void RegistersOneSelectableExternalTrackWithoutChangingQualityStreams()
        {
            var cache = new CaptionCache(_directory);
            var caption = cache.Store(VideoUrl, CaptionKind.OriginalAutomatic, Vtt, new PluginConfiguration());
            var video = new MediaStream { Index = 3, Type = MediaStreamType.Video, Codec = "h264", Height = 1080 };
            var audio = new MediaStream { Index = 5, Type = MediaStreamType.Audio, Codec = "aac" };
            var streams = CaptionPlaybackFilter.UpdateStreams([video, audio], [caption], cache)!;

            Assert.Same(video, streams[0]);
            Assert.Same(audio, streams[1]);
            var subtitle = Assert.Single(streams, stream => stream.Type == MediaStreamType.Subtitle);
            Assert.True(subtitle.IsExternal);
            Assert.True(subtitle.SupportsExternalStream);
            Assert.Equal(1001, subtitle.Index);
            Assert.Equal("eng", subtitle.Language);
            Assert.Equal("vtt", subtitle.Codec);
            Assert.Equal(caption.Path, subtitle.Path);
            Assert.Equal("English (Auto-generated)", subtitle.Title);
            Assert.Null(CaptionPlaybackFilter.UpdateStreams(streams, [caption], cache));
            Assert.Equal([video, audio], CaptionPlaybackFilter.UpdateStreams(streams, [], cache));
        }

        [Fact]
        public void BothTracksUseStableIndicesAndCreatorIsPreferred()
        {
            var cache = new CaptionCache(_directory);
            var config = new PluginConfiguration();
            var automatic = cache.Store(VideoUrl, CaptionKind.OriginalAutomatic, Vtt, config);
            var creator = cache.Store(VideoUrl, CaptionKind.Creator, Vtt, config);
            var embedded = new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Language = "en-GB" };
            var french = new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Language = "fra" };
            var automaticOnly = CaptionPlaybackFilter.UpdateStreams([embedded, french], [automatic], cache)!;
            Assert.Equal([embedded, french], automaticOnly.Take(2));
            var both = CaptionPlaybackFilter.UpdateStreams(automaticOnly, [creator, automatic], cache)!;
            Assert.Equal(3, both.Count);
            Assert.Same(french, both[0]);
            Assert.Equal("English (Creator-uploaded)", both[1].Title);
            Assert.True(both[1].IsDefault);
            Assert.False(both[2].IsDefault);
            Assert.NotEqual(both[1].Index, both[2].Index);
            Assert.Equal(automaticOnly[^1].Index, both[2].Index);
            Assert.Equal(creator, cache.Find(VideoUrl, config, creator: true));
            Assert.Equal(automatic, cache.Find(VideoUrl, config, creator: false));
            Assert.Null(CaptionPlaybackFilter.UpdateStreams(both, [creator, automatic], cache));
            var creatorOnly = CaptionPlaybackFilter.UpdateStreams(both, [creator], cache)!;
            Assert.Equal(both[1].Index, creatorOnly[1].Index);
            Assert.Equal([french], CaptionPlaybackFilter.UpdateStreams(creatorOnly, [], cache));
            Assert.Null(CaptionPlaybackFilter.UpdateStreams([french], [], cache));
        }

        [Fact]
        public void NormalizationKeepsCueTimingsAndLineBreaks()
        {
            const string styled = """
                WEBVTT
                X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000

                STYLE
                ::cue { color: red; font-size: 200%; }

                REGION
                id:bottom

                cue-001
                00:00:01.205 --> 00:00:05.678 line:95% position:50% align:start
                <v Creator><c.blue>Hi, &amp; welcome!</c>
                <i>Second line.</i><00:00:03.000> More.

                """;

            var normalized = CaptionNormalization.PlainWebVtt(styled);
            Assert.DoesNotContain("::cue", normalized, StringComparison.Ordinal);
            Assert.DoesNotContain("position:", normalized, StringComparison.Ordinal);
            Assert.DoesNotContain("REGION", normalized, StringComparison.Ordinal);
            Assert.Contains("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000", normalized, StringComparison.Ordinal);
            Assert.Contains("cue-001\n00:00:01.205 --> 00:00:05.678\nHi, &amp; welcome!\nSecond line.<00:00:03.000> More.", normalized, StringComparison.Ordinal);
        }

        [Fact]
        public void SubtitleAuthorizationUsesTheNativeQueryOverride()
        {
            var item = Guid.NewGuid();
            var source = Guid.NewGuid();
            var args = new Dictionary<string, object?>
            {
                ["routeItemId"] = Guid.NewGuid(),
                ["itemId"] = item,
                ["routeIndex"] = 1,
                ["index"] = 7,
                ["routeMediaSourceId"] = Guid.NewGuid().ToString("N"),
                ["mediaSourceId"] = source.ToString("N")
            };
            Assert.True(CaptionPlaybackDispatch.TryGetSubtitleRequest(args, out var boundItem, out var boundSource, out var index));
            Assert.Equal(item, boundItem);
            Assert.Equal(source, boundSource);
            Assert.Equal(7, index);
        }

        [Fact]
        public async Task AuthenticatedPlaybackRegistersTheSameCachedCaptionForEveryQuality()
        {
            const string vodId = "vod-item";
            var primary = new TestVideo { Id = Guid.NewGuid() };
            var alternate = new TestVideo { Id = Guid.NewGuid() };
            primary.SetProviderId("VOD", vodId);
            alternate.SetProviderId("VOD", vodId);
            primary.Alternate = alternate;

            var userId = Guid.NewGuid();
            var user = new User("Viewer", "provider", "reset-provider");
            var library = Substitute.For<ILibraryManager>();
            var users = Substitute.For<IUserManager>();
            var mediaSources = Substitute.For<IMediaSourceManager>();
            var repository = Substitute.For<IMediaStreamRepository>();
            var database = Substitute.For<IVodDatabase>();
            database.GetVideoByUuid(vodId).Returns(new VideoRecord { VideoUrl = VideoUrl, NativeId = "SLe8Ot4XTiM" });
            users.GetUserById(userId).Returns(user);
            library.GetItemById<BaseItem>(primary.Id, user).Returns(primary);
            var videoStream = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" };
            repository.GetMediaStreams(Arg.Any<MediaStreamQuery>()).Returns([videoStream]);

            var config = new PluginConfiguration();
            var cache = new CaptionCache(_directory);
            var caption = cache.Store("SLe8Ot4XTiM", CaptionKind.OriginalAutomatic, Vtt, config);
            var creator = cache.Store("SLe8Ot4XTiM", CaptionKind.Creator, Vtt, config);
            using var filter = new CaptionPlaybackFilter(library, users, mediaSources, repository, database,
                null!, cache, NullLogger<CaptionPlaybackFilter>.Instance);

            await filter.TryPrepareAsync(primary.Id, userId, config, CancellationToken.None);
            foreach (var id in new[] { primary.Id, alternate.Id })
            {
                repository.Received(1).SaveMediaStreams(id,
                    Arg.Is<IReadOnlyList<MediaStream>>(tracks => tracks[0] == videoStream && tracks[1].Path == creator.Path && tracks[2].Path == caption.Path),
                    CancellationToken.None);
            }

            Assert.True(filter.CanReadSubtitle(primary.Id, primary.Id, userId));
            Assert.False(filter.CanReadSubtitle(primary.Id, alternate.Id, userId));
            Assert.False(filter.CanReadSubtitle(primary.Id, primary.Id, Guid.NewGuid()));
        }

        private sealed class TestVideo : Video
        {
            internal Video? Alternate { get; set; }

            protected override IEnumerable<(BaseItem Item, MediaSourceType MediaSourceType)> GetAllItemsForMediaSources()
            {
                yield return (this, MediaSourceType.Default);
                if (Alternate is not null)
                {
                    yield return (Alternate, MediaSourceType.Default);
                }
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private sealed class CaptionHandler(string body, HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
