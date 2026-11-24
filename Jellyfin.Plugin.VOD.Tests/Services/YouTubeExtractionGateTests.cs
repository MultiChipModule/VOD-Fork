using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YouTubeExtractionGateTests
    {
        [Fact]
        public void DefaultsSerializeExtractionAndSpaceStartsTenSecondsApart()
        {
            var clock = new ManualClock();
            var gate = new YouTubeExtractionGate(clock, () => null);
            var first = Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(false).Lease);
            Assert.Equal((null, false), gate.TryEnter(true));
            clock.Advance(TimeSpan.FromSeconds(9));
            first.Dispose();
            Assert.Equal((null, false), gate.TryEnter(true));
            clock.Advance(TimeSpan.FromSeconds(1));
            using var playback = Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(true).Lease);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal((null, false), gate.TryEnter(false));
            Assert.Equal(1, gate.Status().BackgroundLastHour);
        }

        [Fact]
        public void BackgroundBudgetDoesNotConsumePlaybackBudget()
        {
            var clock = new ManualClock();
            var config = new PluginConfiguration { YouTubeBackgroundPerHour = 2 };
            var gate = new YouTubeExtractionGate(clock, () => config);
            gate.TryEnter(false).Lease?.Dispose();
            clock.Advance(TimeSpan.FromSeconds(10));
            gate.TryEnter(false).Lease?.Dispose();
            clock.Advance(TimeSpan.FromSeconds(10));

            Assert.Equal((null, true), gate.TryEnter(false));
            Assert.Equal(clock.GetUtcNow().AddSeconds(3580), gate.Status().NextBackgroundRetry);
            using (Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(true).Lease))
            {
                Assert.Equal(2, gate.Status().BackgroundLastHour);
            }

            clock.Advance(TimeSpan.FromSeconds(3580));
            using var background = Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(false).Lease);
        }

        [Fact]
        public async Task QueuedPlaybackWinsAndCancellationReleasesPriority()
        {
            var clock = new ManualClock();
            var gate = new YouTubeExtractionGate(clock, () => null);
            using var active = Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(false).Lease);
            using var cancel = new CancellationTokenSource();
            var queued = gate.EnterAsync(true, cancel.Token);
            Assert.Equal(1, gate.Status().WaitingPlayback);
            clock.Advance(TimeSpan.FromSeconds(10));
            active.Dispose();

            Assert.Equal((null, false), gate.TryEnter(false));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(0, gate.Status().WaitingPlayback);
            using var next = Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(false).Lease);
        }

        [Theory]
        [InlineData("WARNING: preliminary warning\nERROR: Sign in to confirm you’re not a bot", "Bot challenge")]
        [InlineData("ERROR: HTTP Error 429: Too Many Requests", "Rate limited")]
        [InlineData("ERROR: Sign in to confirm your age", null)]
        public void ClassifiesRestrictionsWithoutConflatingAgeChecks(string stderr, string? expected)
            => Assert.Equal(expected, YouTubeExtractionGate.DetectRestriction(stderr));

        [Fact]
        public void CooldownSurvivesRestartAndEscalatesToTwentyFourHours()
        {
            var clock = new ManualClock();
            var folder = Directory.CreateTempSubdirectory("vod-youtube-gate-");
            try
            {
                var state = Path.Combine(folder.FullName, "restriction.json");
                var gate = new YouTubeExtractionGate(clock, () => null, () => state);
                gate.Restricted("Bot challenge");
                gate = new YouTubeExtractionGate(clock, () => null, () => state);
                Assert.Equal(clock.GetUtcNow().AddHours(1), gate.Status().CooldownUntil);
                Assert.Equal((null, true), gate.TryEnter(true));
                Assert.Equal((null, true), gate.TryEnter(false));

                clock.Advance(TimeSpan.FromHours(1));
                using (Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(true).Lease))
                {
                    Assert.Null(gate.Status().CooldownUntil);
                }

                foreach (var hours in new[] { 2, 4, 8, 16, 24, 24 })
                {
                    gate.Restricted("Rate limited");
                    Assert.Equal(clock.GetUtcNow().AddHours(hours), gate.Status().NextRetry);
                    clock.Advance(TimeSpan.FromHours(hours));
                }
            }
            finally
            {
                folder.Delete(recursive: true);
            }
        }

        private sealed class ManualClock : TimeProvider
        {
            private long _ticks = DateTimeOffset.UnixEpoch.Ticks;

            public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
                => System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            internal void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
        }
    }
}
