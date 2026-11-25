using System;
using System.Collections.Generic;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class NativePlaybackReportsTests
    {
        private const string ProviderId = "vod-item";
        private readonly TestVideo _primary = new() { Id = Guid.NewGuid() };
        private readonly TestVideo _alternate = new() { Id = Guid.NewGuid() };

        public NativePlaybackReportsTests()
        {
            _primary.SetProviderId("VOD", ProviderId);
            _alternate.SetProviderId("VOD", ProviderId);
            _alternate.PrimaryVersionId = _primary.Id;
            _primary.Alternate = _alternate;
        }

        [Fact]
        public void KeepsVisibleItemAndActualMediaSourceSeparateOnNativeStartProgressAndStop()
        {
            PlaybackProgressInfo[] events =
            [
                new PlaybackStartInfo { ItemId = _alternate.Id, PositionTicks = 15 },
                new PlaybackProgressInfo { ItemId = _alternate.Id, PositionTicks = 30 }
            ];
            foreach (var report in events)
            {
                report.Item = new BaseItemDto { Id = _alternate.Id };
                report.PlaySessionId = "existing-play-session";
                report.PlayMethod = PlayMethod.DirectStream;
                var position = report.PositionTicks;
                Assert.True(NativePlaybackReports.Normalize(report, Visible, IsYouTube));
                Assert.Equal(_primary.Id, report.ItemId);
                Assert.Equal(_alternate.Id.ToString("N"), report.MediaSourceId);
                Assert.Null(report.Item);
                Assert.Equal("existing-play-session", report.PlaySessionId);
                Assert.Equal(PlayMethod.DirectStream, report.PlayMethod);
                Assert.Equal(position, report.PositionTicks);
            }

            var stopped = new PlaybackStopInfo { ItemId = _alternate.Id, MediaSourceId = _alternate.Id.ToString("N"), PositionTicks = 50 };
            Assert.True(NativePlaybackReports.Normalize(stopped, Visible, IsYouTube));
            Assert.Equal(_primary.Id, stopped.ItemId);
            Assert.Equal(_alternate.Id.ToString("N"), stopped.MediaSourceId);
            Assert.Equal(50, stopped.PositionTicks);
        }

        [Fact]
        public void LeavesPrimaryNonYouTubeAndInvisibleOrUnrelatedSourcesAlone()
        {
            var canonical = new PlaybackProgressInfo { ItemId = _primary.Id, MediaSourceId = _alternate.Id.ToString("N") };
            Assert.False(NativePlaybackReports.Normalize(canonical, Visible, IsYouTube));

            var alternate = new PlaybackProgressInfo { ItemId = _alternate.Id };
            Assert.False(NativePlaybackReports.Normalize(alternate, Visible, _ => false));
            Assert.False(NativePlaybackReports.Normalize(alternate, id => id == _alternate.Id ? _alternate : null, IsYouTube));

            alternate.MediaSourceId = Guid.NewGuid().ToString("N");
            Assert.False(NativePlaybackReports.Normalize(alternate, Visible, IsYouTube));
            Assert.Equal(_alternate.Id, alternate.ItemId);
        }

        [Fact]
        public void RehydratesAnOutdatedNestedDtoEvenWhenTheTopLevelItemIsCanonical()
        {
            var report = new PlaybackProgressInfo
            {
                ItemId = _primary.Id, MediaSourceId = _alternate.Id.ToString("N"),
                Item = new BaseItemDto { Id = _alternate.Id }, IsPaused = true, PositionTicks = 234
            };
            Assert.True(NativePlaybackReports.Normalize(report, Visible, IsYouTube));
            Assert.Equal(_primary.Id, report.ItemId);
            Assert.Equal(_alternate.Id.ToString("N"), report.MediaSourceId);
            Assert.Null(report.Item);
            Assert.True(report.IsPaused);
            Assert.Equal(234, report.PositionTicks);
        }

        [Fact]
        public void CanResolveASourceOnlyReportWithoutInventingAnItem()
        {
            var report = new PlaybackStartInfo { MediaSourceId = _alternate.Id.ToString("N") };
            Assert.True(NativePlaybackReports.Normalize(report, Visible, IsYouTube));
            Assert.Equal(_primary.Id, report.ItemId);
            Assert.Equal(_alternate.Id.ToString("N"), report.MediaSourceId);
        }

        private Video? Visible(Guid id) => id == _primary.Id ? _primary : id == _alternate.Id ? _alternate : null;

        private static bool IsYouTube(string id) => id == ProviderId;

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
    }
}
