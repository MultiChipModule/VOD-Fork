using System;
using System.IO;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.VOD.Tests.Services
{
    internal sealed class MockYouTubeExtractor : IDisposable
    {
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("vod-mock-youtube-");
        private readonly CookieJar _cookies;

        internal MockYouTubeExtractor(string command)
        {
            ArgumentsPath = Path.Combine(_folder.FullName, "calls");
            File.WriteAllText(ArgumentsPath, string.Empty);
            var program = Path.Combine(_folder.FullName, "yt-dlp");
            File.WriteAllText(program, "#!/bin/sh\n" + $"printf '%s\\n' \"$*\" >> '{ArgumentsPath}'\n" + command + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            Configuration = new() { YtDlpPath = program, YouTubeExtractionIntervalSeconds = 1 };
            _cookies = new CookieJar(NullLogger<CookieJar>.Instance, () => Configuration, () => _folder.FullName);
            Service = new YtDlpService(_cookies, ThrottleClock.Throttle(), NullLogger<YtDlpService>.Instance, () => Configuration);
        }

        internal const string HlsOutput = """
            printf '%s\n' '{"id":"SLe8Ot4XTiM","manifest_url":"https://example.invalid/master.m3u8","formats":[{"protocol":"m3u8_native","height":720,"vcodec":"avc1"}]}'
            """;

        internal string ArgumentsPath { get; }

        internal PluginConfiguration Configuration { get; }

        internal YtDlpService Service { get; }

        internal void ConfigureCookies(string document)
        {
            var seed = Path.Combine(_folder.FullName, "seed.txt");
            File.WriteAllText(seed, document);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(seed, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Configuration.YouTubeCookiesPath = seed;
        }

        public void Dispose()
        {
            Service.Dispose();
            _cookies.Dispose();
            _folder.Delete(recursive: true);
        }
    }
}
