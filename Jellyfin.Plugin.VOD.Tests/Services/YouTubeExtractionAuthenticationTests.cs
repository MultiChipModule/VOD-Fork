using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    public sealed class YouTubeExtractionAuthenticationTests
    {
        private const string Video = "https://www.youtube.com/watch?v=SLe8Ot4XTiM";
        private const string Playlist = "https://www.youtube.com/playlist?list=PLtestabcdef";
        private const string SignedInCookies = """
            # Netscape HTTP Cookie File
            .youtube.com	TRUE	/	TRUE	0	LOGIN_INFO	fixture
            #HttpOnly_.youtube.com	TRUE	/	TRUE	0	__Secure-3PAPISID	fixture
            """;

        [Fact]
        public async Task AllYtDlpPathsUseTheDedicatedCookieFileAndExplicitRuntime()
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor("""
                case "$*" in
                    *--flat-playlist*|*--playlist-items*)
                        printf '%s\n' '{"_type":"playlist","entries":[{"id":"SLe8Ot4XTiM","title":"Video","url":"https://www.youtube.com/watch?v=SLe8Ot4XTiM","formats":[{"protocol":"m3u8_native","height":720,"vcodec":"avc1"}]}]}'
                        ;;
                    *)
                """ + "\n" + MockYouTubeExtractor.HlsOutput + "\n;;\nesac");
            yt.ConfigureCookies(SignedInCookies);
            yt.Configuration.JsRuntime = "node:/runtime/node";

            Assert.NotNull(await yt.Service.ResolveAsync(Video, StreamCacheKey.DefaultFormat, CancellationToken.None));
            Assert.NotNull(await yt.Service.GetPlaylistEntriesAsync(Playlist, null, null, CancellationToken.None));
            Assert.NotNull((await yt.Service.GetMetadataAsync(Video, CancellationToken.None)).Metadata);
            Assert.NotNull(await yt.Service.GetFirstEntryMetadataAsync(Playlist, CancellationToken.None));

            var args = File.ReadAllLines(yt.ArgumentsPath);
            Assert.Equal(4, args.Length);
            Assert.Contains("cookies-youtube-playback.txt", args[0], StringComparison.Ordinal);
            Assert.All(args[1..], row => Assert.Contains("cookies-youtube.txt", row, StringComparison.Ordinal));
            Assert.All(args, row =>
            {
                Assert.Contains("--ignore-config", row, StringComparison.Ordinal);
                Assert.Contains("--no-js-runtimes --js-runtimes node:/runtime/node", row, StringComparison.Ordinal);
                Assert.Contains("--cookies ", row, StringComparison.Ordinal);
                Assert.DoesNotContain("seed.txt", row, StringComparison.Ordinal);
                Assert.DoesNotContain("--no-warnings", row, StringComparison.Ordinal);
            });
        }

        [Fact]
        public async Task ConfiguredPublicOrExpiredCookiesDoNotLaunchAnAnonymousProcess()
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor(MockYouTubeExtractor.HlsOutput);
            yt.ConfigureCookies(SignedInCookies.Replace("\t0\t", "\t1\t", StringComparison.Ordinal));

            Assert.Null(await yt.Service.ResolveAsync(Video, StreamCacheKey.DefaultFormat, CancellationToken.None));
            Assert.Null(await yt.Service.GetPlaylistEntriesAsync(Playlist, null, null, CancellationToken.None));
            Assert.Empty(File.ReadAllLines(yt.ArgumentsPath));
            Assert.Contains("Export fresh cookies", yt.Service.LastYouTubeError);
        }

        [Fact]
        public async Task RotatedCookieWarningDiscardsEvenAPlayableExitZeroResult()
        {
            if (OperatingSystem.IsWindows())
                return;
            using var yt = new MockYouTubeExtractor("""
                printf '%s\n' 'WARNING: The provided YouTube account cookies are no longer valid. secret-debug-text' >&2
                """ + "\n" + MockYouTubeExtractor.HlsOutput);
            yt.ConfigureCookies(SignedInCookies);

            Assert.Null(await yt.Service.ResolveAsync(Video, StreamCacheKey.DefaultFormat, CancellationToken.None));
            Assert.Null(yt.Service.TryGetCachedYouTubeResolution(Video, StreamCacheKey.DefaultFormat));
            Assert.Contains("rejected the configured account cookies", yt.Service.LastYouTubeError);
            Assert.DoesNotContain("secret-debug-text", yt.Service.LastYouTubeError, StringComparison.Ordinal);
            Assert.Single(File.ReadAllLines(yt.ArgumentsPath));
        }
    }
}
