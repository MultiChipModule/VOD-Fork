using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.VOD.Data.Models;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class YouTubeLibraryCatalogTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

        [Fact]
        public void GroupsChannelsAndSortsByNameWithVideosFirst()
        {
            var upload = Upload("a", 2);
            var before = YouTubeLibraryCatalog.Build(
                [Source("UC-1", "Zulu", [upload], Playlist("z", "Zoo", "a")),
                 Source("UC-1", "Zulu", [upload], Playlist("a", "Archive", "a")),
                 Source("UC-2", "alpha", [])], new HashSet<string>(), Now);
            Assert.Equal(["alpha", "Zulu"], before.Select(channel => channel.Name));
            Assert.Equal(["Videos", "Archive", "Zoo"], before[1].Playlists.Select(playlist => playlist.Name));
            Assert.All(before[1].Playlists, playlist => Assert.Equal([upload.ItemId], playlist.Items));

            var renamed = YouTubeLibraryCatalog.Build(
                [Source("UC-1", "Aardvark", [upload]), Source("UC-2", "alpha", [])], new HashSet<string>(), Now);
            Assert.Equal(before[1].Id, renamed[0].Id);
            Assert.Equal(before[1].Playlists[0].Items, renamed[0].Playlists[0].Items);
        }

        [Fact]
        public void CanonicalLinksKeepOriginalPlaylistOrderAndRetention()
        {
            var newest = Upload("a", 1);
            var boundary = Upload("b", 100);
            var expired = Upload("c", 101);
            var duplicate = Upload("a", 2);
            var channels = YouTubeLibraryCatalog.Build(
                [Source("one", "Channel", [newest, boundary, expired],
                    Playlist("keep", "Preserve order", "b", "c", "a", "b"),
                    Playlist("excluded", "Ignore", "a"),
                    Playlist("empty", "Hide", "c")),
                 Source("two", "Second", [duplicate])],
                new HashSet<string>(StringComparer.Ordinal) { "excluded" }, Now);
            var uploads = channels[0].Playlists[0];
            var normal = channels[0].Playlists[1];
            Assert.Equal(2, channels[0].Playlists.Count);
            Assert.Equal(2, uploads.Items.Count);
            Assert.Equal(boundary.ItemId, uploads.Items[1]);
            Assert.Equal([boundary.ItemId, uploads.Items[0]], normal.Items);
            Assert.Equal(channels[1].Playlists[0].Items[0], uploads.Items[0]);
            Assert.DoesNotContain(expired.ItemId, uploads.Items);
        }

        [Fact]
        public void ReadsStableChannelIdentityFromFlatYtDlpOutput()
        {
            var listing = PlaylistInfo.ParseJson("""
                {"_type":"playlist","id":"UUabcdefghijkl1234567890",
                 "channel_id":"UCabcdefghijkl1234567890","channel":"Original name","title":"Original name - Videos"}
                """);
            Assert.NotNull(listing);
            Assert.Equal("UCabcdefghijkl1234567890", listing.ChannelId);
            Assert.True(YouTubeChannelTask.IsChannelId(listing.ChannelId!));
            Assert.Equal("Original name", listing.ChannelName);
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=a", 1123001, "Original title", null)]
        [InlineData("https://www.youtube.com/watch?v=a", 10, "Original title", 10)]
        [InlineData("https://www.arte.tv/en/videos/a", 1123001, "Original title", 1123001)]
        public void RemovesOnlySyntheticYouTubeEpisodeDisplayNumbers(string url, int number, string title, int? expected)
        {
            var video = new VideoRecord { VideoUrl = url, Id = "id", Title = title, SeasonNumber = 2026, EpisodeNumber = number };
            Assert.Equal(expected, LibraryPublisher.EpisodeNumberForPresentation(video));
            Assert.Equal(number, video.EpisodeNumber);
            Assert.Equal("id", video.Id);
            Assert.Equal(title, video.Title);
        }

        private static ChannelUpload Upload(string id, int days) =>
            new(new VideoRecord { NativeId = id, PublishedAt = Now.AddDays(-days) }, Guid.NewGuid(), 100);

        private static ChannelSource Source(string id, string name, ChannelUpload[] uploads, params ChannelPlaylist[] playlists) =>
            new(id, name, "avatar", "banner", uploads, playlists);

        private static ChannelPlaylist Playlist(string id, string name, params string[] videos) =>
            new(id, name, null, null, videos);
    }
}
