using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.VOD.Controllers;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YouTubeCooldownPlaybackTests
    {
        private const string Page = "https://www.youtube.com/watch?v=SLe8Ot4XTiM";
        private const string LocalStream = "http://localhost:8096/VOD/stream/test.m3u8";

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FirstChallengeAndRepeatedNativeRetriesNeverReachJellyfinOrTheMediaProbe(bool startsWithNativeStream)
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor("""
                printf '%s\n' "ERROR: Sign in to confirm you're not a bot" >&2
                exit 1
                """);
            using var fixture = new PlaybackFixture(yt, registeredLayout: false);
            if (startsWithNativeStream)
            {
                var initial = await fixture.Dispatch("VideosController", "GetVideoStream");
                Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(initial.Result).StatusCode);
            }

            for (var i = 0; i < 10; i++)
            {
                var negotiation = await fixture.Dispatch("MediaInfoController", i % 2 == 0 ? "GetPostedPlaybackInfo" : "GetPlaybackInfo");
                var info = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(negotiation.Result).Value);
                Assert.Equal(PlaybackErrorCode.RateLimitExceeded, info.ErrorCode);
                Assert.Empty(info.MediaSources);
                Assert.Equal(1, negotiation.HttpContext.Response.Headers.RetryAfter.Count);

                foreach (var (controller, action) in new[]
                {
                    ("DynamicHlsController", "GetMasterHlsVideoPlaylist"),
                    ("DynamicHlsController", "GetVariantHlsVideoPlaylist"),
                    ("DynamicHlsController", "GetHlsVideoSegment"),
                    ("VideosController", "GetVideoStreamByContainer")
                })
                {
                    var request = await fixture.Dispatch(controller, action);
                    Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(request.Result).StatusCode);
                    Assert.Equal(1, request.HttpContext.Response.Headers.RetryAfter.Count);
                }
            }

            Assert.Equal(0, fixture.NativeInvocations);
            Assert.Empty(fixture.MediaSources.ReceivedCalls());
            Assert.Empty(fixture.Encoder.ReceivedCalls());
            Assert.Single(File.ReadAllLines(yt.ArgumentsPath));
            Assert.NotNull(yt.Service.YouTubeStatus.CooldownUntil);
        }

        [Fact]
        public async Task LoopbackHlsReturnsRetryAfterBeforeFetchingUpstream()
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor("exit 1");
            yt.Service.RestrictYouTube("Rate limited");
            var db = Substitute.For<IVodDatabase>();
            db.GetVideoByUuid("test").Returns(new VideoRecord { Id = "test", VideoUrl = Page });
            var clients = Substitute.For<IHttpClientFactory>();
            var controller = new VodController(db, yt.Service, new PlaylistCache(), null!, null!, null!, clients,
                null!, null!, NullLogger<VodController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            for (var i = 0; i < 5; i++)
            {
                var result = Assert.IsType<ObjectResult>(await controller.GetStream("test"));
                Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
                Assert.Equal(1, controller.Response.Headers.RetryAfter.Count);
                Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
            }

            Assert.Empty(clients.ReceivedCalls());
            Assert.Empty(File.ReadAllLines(yt.ArgumentsPath));
        }

        [Fact]
        public async Task ExistingNativeJobCanServeSegmentsDuringCooldown()
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor("exit 1");
            using var fixture = new PlaybackFixture(yt);
            yt.Service.RestrictYouTube("Bot challenge");
            fixture.Transcodes.GetTranscodingJob("active").Returns(new TranscodingJob(NullLogger<TranscodingJob>.Instance)
            {
                MediaSource = new MediaSourceInfo { Path = LocalStream }
            });
            fixture.Transcodes.GetTranscodingJob("unrelated").Returns(new TranscodingJob(NullLogger<TranscodingJob>.Instance)
            {
                MediaSource = new MediaSourceInfo { Path = "/another/video" }
            });

            Assert.Null((await fixture.Dispatch("DynamicHlsController", "GetHlsVideoSegment", "active")).Result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable,
                Assert.IsType<ObjectResult>((await fixture.Dispatch("DynamicHlsController", "GetHlsVideoSegment", "unrelated")).Result).StatusCode);
            Assert.Equal(1, fixture.NativeInvocations);
            Assert.Empty(File.ReadAllLines(yt.ArgumentsPath));
        }

        [Fact]
        public async Task SuccessfulPlaybackReusesOneResolutionAcrossAliasesAndNativeEndpoints()
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor(MockYouTubeExtractor.HlsOutput);
            using var fixture = new PlaybackFixture(yt);

            Assert.Null((await fixture.Dispatch("MediaInfoController", "GetPostedPlaybackInfo")).Result);
            Assert.Null((await fixture.Dispatch("VideosController", "GetVideoStream")).Result);
            Assert.NotNull(await yt.Service.ResolveAsync("https://youtu.be/SLe8Ot4XTiM", StreamCacheKey.DefaultFormat, CancellationToken.None));
            Assert.Equal(2, fixture.NativeInvocations);
            Assert.Single(File.ReadAllLines(yt.ArgumentsPath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ReportingToggleControlsNativeModernAndLegacyRequests(bool enabled)
        {
            if (OperatingSystem.IsWindows())
                return;

            using var yt = new MockYouTubeExtractor("exit 1");
            yt.Configuration.EnableYouTubePlaybackReporting = enabled;
            using var fixture = new PlaybackFixture(yt);
            var expected = enabled ? fixture.ItemId : fixture.AlternateId;

            foreach (var action in new[] { "OnPlaybackStart", "OnPlaybackProgressLegacy", "OnPlaybackStoppedLegacy" })
            {
                var args = new Dictionary<string, object?>
                {
                    ["itemId"] = fixture.AlternateId,
                    ["positionTicks"] = 300L,
                    ["playSessionId"] = "existing-session",
                    ["isPaused"] = true
                };
                await fixture.Dispatch("PlaystateController", action, arguments: args);
                Assert.Equal(expected, args["itemId"]);
                Assert.Equal(300L, args["positionTicks"]);
                Assert.Equal("existing-session", args["playSessionId"]);
                Assert.Equal(true, args["isPaused"]);
                if (enabled)
                    Assert.Equal(fixture.AlternateId.ToString("N"), args["mediaSourceId"]);
            }

            var paused = new PlaybackProgressInfo { ItemId = fixture.AlternateId, IsPaused = true,
                PositionTicks = 350, PlayMethod = PlayMethod.DirectStream, PlaySessionId = "existing-session" };
            await fixture.Dispatch("PlaystateController", "ReportPlaybackProgress", arguments:
                new Dictionary<string, object?> { ["playbackProgressInfo"] = paused });
            Assert.Equal(expected, paused.ItemId);
            Assert.Equal(PlayMethod.DirectStream, paused.PlayMethod);
            Assert.True(paused.IsPaused);
            Assert.Equal(350, paused.PositionTicks);
            Assert.Equal(4, fixture.NativeInvocations);
            Assert.Empty(File.ReadAllLines(yt.ArgumentsPath));
        }

        [Fact]
        public async Task MissingAlternateDoesNotEvictTheSharedMasterOrTriggerAnotherExtraction()
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor(MockYouTubeExtractor.HlsOutput);
            var db = Substitute.For<IVodDatabase>();
            db.GetVideoByUuid("test").Returns(new VideoRecord { Id = "test", VideoUrl = Page });
            var clients = Substitute.For<IHttpClientFactory>();
            using var handler = new MasterHandler();
            clients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
            var cache = new PlaylistCache();
            var library = Substitute.For<ILibraryManager>();
            var writer = new MediaStreamWriter(library, Substitute.For<IMediaStreamRepository>(),
                NullLogger<MediaStreamWriter>.Instance);
            var controller = new VodController(db, yt.Service, cache, new SubtitleTokens(cache), writer, null!,
                clients, library, null!, NullLogger<VodController>.Instance);

            var omitted = await controller.GetStream("test", height: 480, codec: "h264");
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(omitted).StatusCode);
            var alternate = Assert.IsType<ContentResult>(await controller.GetStream("test", height: 720, codec: "h264"));
            var original = Assert.IsType<ContentResult>(await controller.GetStream("test"));
            Assert.Contains("720.m3u8", alternate.Content, StringComparison.Ordinal);
            Assert.Contains("720.m3u8", original.Content, StringComparison.Ordinal);
            Assert.Equal(1, handler.Calls);
            Assert.Single(File.ReadAllLines(yt.ArgumentsPath));
        }

        private sealed class MasterHandler : HttpMessageHandler
        {
            internal int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        #EXTM3U
                        #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS="avc1.64001f,mp4a.40.2"
                        720.m3u8
                        """)
                });
            }
        }

        private sealed class PlaybackFixture : IDisposable
        {
            private readonly MockYouTubeExtractor _yt;
            private readonly ServiceProvider _services;
            private readonly CaptionPlaybackFilter _filter;
            private readonly StreamDescriber _describer;
            private readonly Guid _userId = Guid.NewGuid();
            private readonly Guid _itemId = Guid.NewGuid();

            internal PlaybackFixture(MockYouTubeExtractor yt, bool registeredLayout = true)
            {
                _yt = yt;
                var item = new TestVideo { Id = _itemId, Path = LocalStream };
                item.SetProviderId("VOD", "test");
                var alternate = new TestVideo { Id = AlternateId, PrimaryVersionId = item.Id, Path = LocalStream + "?height=720" };
                alternate.SetProviderId("VOD", "test");
                item.Alternate = alternate;
                var user = new User("Viewer", "provider", "reset-provider");
                var users = Substitute.For<IUserManager>();
                var library = Substitute.For<ILibraryManager>();
                var database = Substitute.For<IVodDatabase>();
                var streams = Substitute.For<IMediaStreamRepository>();
                users.GetUserById(_userId).Returns(user);
                library.GetItemById<BaseItem>(_itemId, user).Returns(item);
                library.GetItemById<BaseItem>(AlternateId, user).Returns(alternate);
                database.GetVideoByUuid("test").Returns(new VideoRecord { Id = "test", VideoUrl = Page });
                streams.GetMediaStreams(Arg.Any<MediaStreamQuery>())
                    .Returns(registeredLayout ? [new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" }] : []);
                var playlists = new PlaylistCache();
                _describer = new StreamDescriber(library, streams, Encoder, playlists, new SubtitleTokens(playlists),
                    NullLogger<StreamDescriber>.Instance);
                _filter = new CaptionPlaybackFilter(library, users, MediaSources, streams, database, yt.Service,
                    new CaptionCache(Path.Combine(Path.GetTempPath(), "vod-unpopulated-caption-test")),
                    NullLogger<CaptionPlaybackFilter>.Instance, _describer);
                _services = new ServiceCollection().AddSingleton(_filter).AddSingleton(Transcodes).BuildServiceProvider();
            }

            internal Guid ItemId => _itemId;

            internal Guid AlternateId { get; } = Guid.NewGuid();

            internal IMediaSourceManager MediaSources { get; } = Substitute.For<IMediaSourceManager>();

            internal IMediaEncoder Encoder { get; } = Substitute.For<IMediaEncoder>();

            internal ITranscodeManager Transcodes { get; } = Substitute.For<ITranscodeManager>();

            internal int NativeInvocations { get; private set; }

            internal async Task<ActionExecutingContext> Dispatch(string controller, string action, string? playSession = null,
                IDictionary<string, object?>? arguments = null)
            {
                var http = new DefaultHttpContext
                {
                    RequestServices = _services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", _userId.ToString())], "test"))
                };
                var descriptor = new ControllerActionDescriptor { ActionName = action, ControllerTypeInfo = new ControllerType(controller) };
                var actionContext = new ActionContext(http, new RouteData(), descriptor);
                var filters = new List<IFilterMetadata>();
                var context = new ActionExecutingContext(actionContext, filters,
                    arguments ?? new Dictionary<string, object?> { ["itemId"] = _itemId, ["playSessionId"] = playSession }, this);
                await new CaptionPlaybackDispatch(() => _yt.Configuration).OnActionExecutionAsync(context, () =>
                {
                    NativeInvocations++;
                    return Task.FromResult(new ActionExecutedContext(actionContext, filters, this));
                });
                return context;
            }

            public void Dispose()
            {
                _services.Dispose();
                _filter.Dispose();
                _describer.Dispose();
            }
        }

        private sealed class ControllerType(string controller) : TypeDelegator(typeof(ControllerBase))
        {
            public override string FullName => "Jellyfin.Api.Controllers." + controller;
        }

        private sealed class TestVideo : Video
        {
            internal Video? Alternate { get; set; }

            protected override IEnumerable<(BaseItem Item, MediaBrowser.Model.Dto.MediaSourceType MediaSourceType)> GetAllItemsForMediaSources()
            {
                yield return (this, MediaBrowser.Model.Dto.MediaSourceType.Default);
                if (Alternate is not null)
                    yield return (Alternate, MediaBrowser.Model.Dto.MediaSourceType.Default);
            }
        }
    }
}
