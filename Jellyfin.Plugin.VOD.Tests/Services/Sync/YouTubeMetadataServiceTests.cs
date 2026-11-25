using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class YouTubeMetadataServiceTests : IDisposable
    {
        private const string Channel = "UCA123456789012345678901";
        private const string Uploads = "UUA123456789012345678901";
        private const string ChannelUrl = "https://www.youtube.com/channel/" + Channel;
        private const string Playlist = "PLA1234567890";
        private const string PlaylistUrl = "https://www.youtube.com/playlist?list=" + Playlist;
        private const string Key = "fake-test-key-for-offline-fixtures";
        private const string Video = "v0000000000";
        private const string Feed = """
            <feed xmlns="http://www.w3.org/2005/Atom"
                  xmlns:yt="http://www.youtube.com/xml/schemas/2015"
                  xmlns:media="http://search.yahoo.com/mrss/">
              <yt:channelId>UCA123456789012345678901</yt:channelId>
              <title>New channel name</title>
              <entry>
                <yt:videoId>v0000000000</yt:videoId><title>Recent video</title>
                <published>2026-11-24T21:15:56Z</published>
                <media:group><media:description>Recent description</media:description>
                <media:thumbnail url="https://i.ytimg.com/vi/v0000000000/hqdefault.jpg"/></media:group>
              </entry>
            </feed>
            """;
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("vod-yt-metadata-");
        private readonly TestClock _clock = new();

        [Fact]
        public async Task ImportsHistoricalPagesAndBatchesDetailsInOriginalOrderWithNoExtractor()
        {
            var config = MakeConfig();
            var flat = Substitute.For<IYtDlpService>();
            var requests = new List<string>();
            using var http = new HttpClient(new Handler(request =>
            {
                Assert.True(request.Headers.TryGetValues("X-Goog-Api-Key", out var values));
                Assert.Equal(Key, Assert.Single(values));
                Assert.DoesNotContain(Key, request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
                var uri = request.RequestUri;
                var query = QueryHelpers.ParseQuery(uri.Query);
                requests.Add(uri.Segments[^1]);
                return uri.Segments[^1] switch
                {
                    "channels" => Json(new
                    {
                        items = new[] { new { id = Channel, snippet = new { title = "Channel", description = "Channel description" },
                            contentDetails = new { relatedPlaylists = new { uploads = Uploads } } } }
                    }),
                    "playlistItems" when !query.ContainsKey("pageToken") => Page(0, 50, "page-two", 52),
                    "playlistItems" => Page(50, 2, null, 52),
                    "videos" => VideoDetails(query["id"].ToString().Split(',')),
                    _ => throw new InvalidOperationException("Unexpected fixture route")
                };
            }));
            using var service = new YouTubeMetadataService(flat, () => config, http, _clock);
            var first = Assert.IsType<PlaylistInfo>(await service.ListAsync(ChannelUrl, null, false, false, default));
            Assert.False(first.IsPartial);
            Assert.Equal(Enumerable.Range(0, 52).Select(Id), first.Entries.Select(entry => entry.Id));
            Assert.Equal("Original " + Video, first.Entries[0].Title);
            Assert.Equal(83, first.Entries[0].Duration);
            Assert.Equal(new DateTimeOffset(2026, 11, 24, 21, 15, 56, TimeSpan.Zero), first.Entries[0].PublishedAt);
            Assert.Equal("Channel description", first.Description);
            Assert.NotNull(await service.ListAsync(ChannelUrl, null, false, false, default));
            Assert.Equal(["channels", "playlistItems", "playlistItems", "videos", "videos"], requests);
            Assert.Equal(5, service.Status().ApiUnitsUsed);
            Assert.Empty(flat.ReceivedCalls());
        }

        [Fact]
        public async Task CatalogUsesBatchMetadataToRecognizeArchivedBroadcasts()
        {
            var config = MakeConfig();
            var flat = Substitute.For<IYtDlpService>();
            using var http = new HttpClient(new Handler(request =>
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
                Assert.Contains("liveStreamingDetails", query["part"].ToString(), StringComparison.Ordinal);
                Assert.Equal("v0000000000,v0000000001", query["id"].ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"items":[
                         {"id":"v0000000000","snippet":{"title":"Replay","liveBroadcastContent":"none"},"liveStreamingDetails":{"actualEndTime":"2026-11-25T00:00:00Z"}},
                         {"id":"v0000000001","snippet":{"title":"Short ordinary upload"},"contentDetails":{"duration":"PT25S"}}]}
                        """)
                };
            }));
            using var service = new YouTubeMetadataService(flat, () => config, http, _clock);
            var excluded = await service.GetCatalogBroadcastsAsync(["v0000000000", "v0000000001"], default);
            Assert.Equal("v0000000000", Assert.Single(excluded!));
            Assert.Equal(1, service.Status().ApiUnitsUsed);
            Assert.Empty(flat.ReceivedCalls());
        }

        [Fact]
        public async Task KnownChannelHeadersAreBatchedBeforeImport()
        {
            const string other = "UCB123456789012345678901";
            var config = MakeConfig();
            var flat = Substitute.For<IYtDlpService>();
            var count = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                count++;
                Assert.Equal("channels", request.RequestUri!.Segments[^1]);
                Assert.Equal(Channel + "," + other, QueryHelpers.ParseQuery(request.RequestUri.Query)["id"].ToString());
                return Json(new { items = new[] { new { id = Channel, snippet = new { title = "One" } },
                    new { id = other, snippet = new { title = "Two" } } } });
            }));
            using var service = new YouTubeMetadataService(flat, () => config, http, _clock);
            var otherUrl = "https://www.youtube.com/channel/" + other;
            await service.PrimeHeadersAsync([ChannelUrl, otherUrl, ChannelUrl], default);
            Assert.Equal("One", (await service.ListAsync(ChannelUrl, 1, false, false, default, headerOnly: true))?.ChannelName);
            Assert.Equal("Two", (await service.ListAsync(otherUrl, 1, false, false, default, headerOnly: true))?.ChannelName);
            Assert.Equal(1, count);
            Assert.Empty(flat.ReceivedCalls());
        }

        [Fact]
        public async Task KnownChannelCanUseRssWhenFlatListingIsRestricted()
        {
            var flat = Substitute.For<IYtDlpService>();
            flat.GetPlaylistEntriesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>()).Returns((PlaylistInfo?)null);
            using var http = new HttpClient(new Handler(request =>
            {
                Assert.Equal("www.youtube.com", request.RequestUri!.Host);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed) };
            }));
            using var service = new YouTubeMetadataService(flat, () => new PluginConfiguration(), http, _clock);
            var result = Assert.IsType<PlaylistInfo>(await service.ListAsync(ChannelUrl, null, false, false, default));
            Assert.True(result.IsPartial);
            Assert.True(result.IsIncremental);
            Assert.Single(result.Entries);
            Assert.Equal(1, service.Status().RssRequests);
            Assert.Equal(1, service.Status().FlatListings);
        }

        [Fact]
        public async Task RssIsConditionalAndPublishesExactNewMetadataWithoutEnablingCleanup()
        {
            var config = new PluginConfiguration();
            var flat = Substitute.For<IYtDlpService>();
            var count = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                Assert.Equal("www.youtube.com", request.RequestUri!.Host);
                Assert.DoesNotContain(request.Headers, value => value.Key == "X-Goog-Api-Key");
                var response = ++count == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed) }
                    : new HttpResponseMessage(HttpStatusCode.NotModified);
                if (count > 1)
                    Assert.Equal("\"rss1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"rss1\"");
                return response;
            }));
            using var service = new YouTubeMetadataService(flat, () => config, http, _clock);
            var db = Substitute.For<IVodDatabase>();
            var source = new SourceDefinition { Url = ChannelUrl };
            db.GetSource(ChannelUrl).Returns(new SourceRecord { Name = "Old name", SeriesBannerUrl = "kept artwork" });
            db.GetVideosBySource(ChannelUrl).Returns([new VideoRecord { Id = "stored", SourceUrl = ChannelUrl, NativeId = Video }]);
            var listing = new ListingStep(flat, db, NullLogger<ListingStep>.Instance, service);
            var result = await listing.RunAsync(new PlannedListing(source, false, ListingReason.ShortListingDue),
                config, _clock.GetUtcNow(), default);
            Assert.Equal(CallOutcome.Ok, result.Outcome);
            Assert.False(result.CleanupAllowed);
            db.Received().UpsertVideo(Arg.Is<VideoRecord>(row => row.Id == "stored" && row.Title == "Recent video"
                && row.PublishedAt.HasValue && row.PublishedAt.Value.Minute == 15 && row.Extras.Qualities.Count == 0));
            db.Received().UpsertSource(Arg.Is<SourceRecord>(row => row.Name == "New channel name" && row.SeriesBannerUrl == "kept artwork"));

            Assert.NotNull(await service.ListAsync(ChannelUrl, 30, true, false, default));
            Assert.Equal(1, count);
            _clock.Advance(TimeSpan.FromMinutes(30));
            Assert.Single(Assert.IsType<PlaylistInfo>(await service.ListAsync(ChannelUrl, 30, true, false, default)).Entries);
            Assert.Equal(2, service.Status().RssRequests);
            Assert.Empty(flat.ReceivedCalls());
        }

        [Fact]
        public async Task ScheduledImportsLeaveReservedQuotaAndMembershipCacheExpiresInSixHours()
        {
            var config = MakeConfig();
            config.YouTubeApiDailyBudget = 4;
            config.YouTubeApiImportReserve = 2;
            var flat = Substitute.For<IYtDlpService>();
            var count = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                count++;
                return request.RequestUri!.Segments[^1] == "playlists"
                    ? Json(new { items = new[] { new { id = Playlist, snippet = new { title = "Playlist", channelId = Channel } } } })
                    : Page(0, 1, null, 1);
            }));
            using var service = new YouTubeMetadataService(flat, () => config, http, _clock, () => _folder.FullName);
            Assert.NotNull(await service.ListAsync(PlaylistUrl, null, false, false, default, membershipOnly: true));
            Assert.Equal(2, count);
            Assert.NotNull(await service.ListAsync(PlaylistUrl, null, false, false, default, membershipOnly: true));
            Assert.Equal(2, count);
            _clock.Advance(TimeSpan.FromHours(6));
            Assert.Null(await service.ListAsync(PlaylistUrl, null, false, false, default, membershipOnly: true));
            Assert.Equal(2, count);
            Assert.NotNull(await service.ListAsync(PlaylistUrl, null, false, true, default, membershipOnly: true));
            Assert.Equal(4, count);
            using var restarted = new YouTubeMetadataService(flat, () => config, http, _clock, () => _folder.FullName);
            Assert.Equal(4, restarted.Status().ApiUnitsUsed);
            Assert.Empty(flat.ReceivedCalls());
        }

        [Fact]
        public async Task UpstreamQuotaStopsApiRetriesAndKeepsRecentUploadsWorking()
        {
            var config = MakeConfig();
            var flat = Substitute.For<IYtDlpService>();
            var requests = new List<string>();
            using var http = new HttpClient(new Handler(request =>
            {
                requests.Add(request.RequestUri!.Host);
                return request.RequestUri.Host == "www.youtube.com" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed) }
                    : new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("""{"error":{"errors":[{"reason":"quotaExceeded"}]}}""")
                    };
            }));
            using var service = new YouTubeMetadataService(flat, () => config, http, _clock);
            var recent = Assert.IsType<PlaylistInfo>(await service.ListAsync(ChannelUrl, null, false, false, default));
            Assert.True(recent.IsIncremental);
            Assert.True(recent.IsPartial);
            Assert.True(service.Status().UpstreamQuotaExhausted);
            Assert.Null(await service.ListAsync(PlaylistUrl, null, false, true, default));
            Assert.Equal(["www.googleapis.com", "www.youtube.com"], requests);
            Assert.Empty(flat.ReceivedCalls());
        }

        [Fact]
        public async Task NoKeyCapsFlatListingsAndRejectsAmbiguousUrlsBeforeCallingYtDlp()
        {
            var flat = Substitute.For<IYtDlpService>();
            var config = new PluginConfiguration();
            var canonical = "https://www.youtube.com/@test/videos";
            flat.GetPlaylistEntriesAsync(canonical, 100, null, Arg.Any<CancellationToken>(), null, null)
                .Returns(new PlaylistInfo
                {
                    ChannelId = Channel, ChannelName = "Channel", PlaylistCount = 500,
                    Entries = [new PlaylistEntry { Id = Video }]
                });
            using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No fixture HTTP call expected")));
            using var service = new YouTubeMetadataService(flat, () => config, http, _clock);
            Assert.True(Assert.IsType<PlaylistInfo>(await service.ListAsync("https://www.youtube.com/@test", null, false, false, default)).IsPartial);
            Assert.Null(await service.ListAsync("https://www.youtube.com/clip/maybe-video", null, false, false, default));
            Assert.Single(flat.ReceivedCalls());
            Assert.Equal(1, service.Status().FlatListings);
        }

        [Fact]
        public void KeyMustUseAProtectedFileAndQuotaResetsAtPacificMidnight()
        {
            var config = MakeConfig();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(config.YouTubeApiKeyFile, UnixFileMode.UserRead | UnixFileMode.GroupRead);
                Assert.Null(YouTubeMetadataService.ReadKey(config.YouTubeApiKeyFile));
            }
            Assert.Null(YouTubeMetadataService.ReadKey("relative.txt"));

            var quota = new YouTubeMetadataQuota(_clock, () => null);
            _clock.Now = new DateTimeOffset(2026, 11, 25, 7, 59, 0, TimeSpan.Zero);
            Assert.True(quota.TrySpend(1, 0, false));
            Assert.False(quota.TrySpend(1, 0, false));
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(quota.TrySpend(1, 0, false));
            Assert.Equal(new DateOnly(2026, 11, 25), quota.Read().Day);
        }

        [Fact]
        public void SchedulerUsesThirtyMinuteChannelAndSixHourPlaylistDefaults()
        {
            var config = new PluginConfiguration();
            var now = _clock.GetUtcNow();
            var channel = new SourceDefinition { Url = ChannelUrl, Enabled = true };
            var playlist = new SourceDefinition { Url = PlaylistUrl, Enabled = true };
            var known = new Dictionary<string, SourceRecord>(StringComparer.Ordinal)
            {
                [ChannelUrl] = new() { FullListedAt = now.AddDays(-1), ShortListedAt = now.AddMinutes(-30) },
                [PlaylistUrl] = new() { FullListedAt = now.AddHours(-6), ShortListedAt = now.AddHours(-6) }
            };
            var due = ListingSelector.Select([channel, playlist], known, config, now);
            Assert.Contains(due, row => row.Source.Url == ChannelUrl && !row.Full);
            Assert.Contains(due, row => row.Source.Url == PlaylistUrl && row.Full);
            config.YouTubeRssIntervalMinutes = 60;
            config.YouTubePlaylistRefreshHours = 12;
            Assert.Empty(ListingSelector.Select([channel, playlist], known, config, now));
        }

        private PluginConfiguration MakeConfig()
        {
            var file = Path.Combine(_folder.FullName, "key.txt");
            File.WriteAllText(file, Key);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new PluginConfiguration { YouTubeApiKeyFile = file };
        }

        private static string Id(int i) => "v" + i.ToString("D10", CultureInfo.InvariantCulture);

        private static HttpResponseMessage Page(int start, int count, string? nextPageToken, int total) => Json(new
        {
            nextPageToken,
            pageInfo = new { totalResults = total },
            items = Enumerable.Range(start, count).Select(i => new
            {
                snippet = new { title = "Playlist " + Id(i), videoOwnerChannelId = Channel },
                contentDetails = new { videoId = Id(i), videoPublishedAt = "2026-11-24T21:15:56Z" }
            })
        });

        private static HttpResponseMessage VideoDetails(string[] ids)
        {
            Assert.InRange(ids.Length, 1, 50);
            return Json(new
            {
                items = ids.Select(id => new
                {
                    id, snippet = new { title = "Original " + id, channelId = Channel, publishedAt = "2026-11-24T21:15:56Z" },
                    contentDetails = new { duration = "PT1M23S" }
                })
            });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value))
        };

        public void Dispose() => _folder.Delete(true);

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(reply(request));
        }

        private sealed class TestClock : TimeProvider
        {
            internal DateTimeOffset Now { get; set; } = new(2026, 11, 24, 23, 15, 0, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow() => Now;

            internal void Advance(TimeSpan duration) => Now += duration;
        }
    }
}
