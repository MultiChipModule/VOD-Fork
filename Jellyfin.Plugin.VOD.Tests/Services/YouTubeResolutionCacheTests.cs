using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YouTubeResolutionCacheTests
    {
        [Fact]
        public async Task CoalescesIdenticalRequestsAndKeepsDifferentFormatsIndependent()
        {
            using var cache = new YouTubeResolutionCache(TimeProvider.System);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            var shared = new StreamResolution("https://example.com/shared.m3u8", true);
            async Task<StreamResolution?> Extract(CancellationToken ct)
            {
                Interlocked.Increment(ref count);
                await release.Task.WaitAsync(ct);
                return shared;
            }

            var requests = Enumerable.Range(0, 8).Select(_ => cache.ResolveAsync("video", "default", Extract, default)).ToArray();
            var different = new StreamResolution("https://example.com/av1.m3u8", true);
            Assert.Same(different, await cache.ResolveAsync("video", "av1", _ => Task.FromResult<StreamResolution?>(different), default));
            Assert.Same(different, await cache.ResolveAsync("another video", "default", _ => Task.FromResult<StreamResolution?>(different), default));
            release.SetResult();
            Assert.All(await Task.WhenAll(requests), item => Assert.Same(shared, item));
            Assert.Equal(1, count);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ExpiresBeforeTheSignedUrlInQueryOrPath(bool query)
        {
            var clock = new TestClock();
            using var cache = new YouTubeResolutionCache(clock);
            var expiry = clock.Now.AddSeconds(65).ToUnixTimeSeconds();
            var first = new StreamResolution(query ? $"https://example.com/a.m3u8?expire={expiry}" : $"https://example.com/expire/{expiry}/a.m3u8", true);
            var second = new StreamResolution("https://example.com/b.m3u8", true);
            Assert.Same(first, await cache.ResolveAsync("video", "default", _ => Task.FromResult<StreamResolution?>(first), default));
            clock.Now = clock.Now.AddSeconds(36);
            Assert.Same(second, await cache.ResolveAsync("video", "default", _ => Task.FromResult<StreamResolution?>(second), default));
        }

        [Fact]
        public async Task CancellationReleasesActiveExtractionAndAllowsWaitingRequestToRetry()
        {
            using var cache = new YouTubeResolutionCache(TimeProvider.System);
            using var cancel = new CancellationTokenSource();
            var first = cache.ResolveAsync("v", "default", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }, cancel.Token);
            var recovered = new StreamResolution("https://example.com/a.m3u8", true);
            var waiting = cache.ResolveAsync("v", "default", _ => Task.FromResult<StreamResolution?>(recovered), default);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Same(recovered, await waiting);
        }

        private sealed class TestClock : TimeProvider
        {
            internal DateTimeOffset Now = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
