using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Hands yt-dlp a cookie file it may safely rewrite, without ever touching the user's seed.
    /// </summary>
    /// <remarks>
    /// yt-dlp rewrites <c>--cookies</c> in place. The seed is therefore copied into the plugin data
    /// folder; re-seeding triggers when the seed's mtime changes. Concurrent yt-dlp processes would race
    /// on one working copy, so there is a copy — and a semaphore — <b>per lane</b>: a single shared copy
    /// behind a single lock is what would otherwise make the throttle's per-platform lanes fictional,
    /// since an Arte call would queue behind a YouTube one for as long as its process ran.
    /// </remarks>
    public sealed partial class CookieJar : IDisposable
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<CookieJar> _logger;
        private readonly Func<PluginConfiguration?> _configuration;
        private readonly Func<string?> _dataPath;
        /// <summary>
        /// Per lane, not global: with one flag, a lane whose copy succeeds clears the flag a lane
        /// whose copy keeps failing had set, so the same error logs again on every single call —
        /// up to the full call rate, with a stack trace each time.
        /// </summary>
        private readonly ConcurrentDictionary<string, bool> _seedFailureLogged =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initializes a new instance of the <see cref="CookieJar"/> class.</summary>
        public CookieJar(ILogger<CookieJar> logger)
            : this(logger, static () => Plugin.Instance?.Configuration, static () => Plugin.Instance?.DataFolderPath)
        {
        }

        internal CookieJar(ILogger<CookieJar> logger, Func<PluginConfiguration?> configuration, Func<string?> dataPath)
        {
            _logger = logger;
            _configuration = configuration;
            _dataPath = dataPath;
        }

        /// <summary>The file name the working copy of a lane's cookies takes.</summary>
        /// <param name="lane">The lane key, from <see cref="PlatformLane.For"/>.</param>
        /// <returns>A file name safe on every platform the server runs on.</returns>
        /// <remarks>
        /// One copy per lane rather than one for everything: yt-dlp rewrites the file in place, so
        /// a shared copy has to be serialised, and that serialisation is what would make the
        /// throttle's per-platform lanes fictional. A lane only talks to its own platform, so the
        /// cookies it keeps refreshed are the only ones it needs.
        /// </remarks>
        internal static string WorkingFileNameFor(string lane)
        {
            var safe = lane;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(invalid, '_');
            }

            return $"cookies-{safe}.txt";
        }

        /// <summary>
        /// Takes the cookie file of one lane for the duration of one yt-dlp invocation.
        /// Returns a lease whose <see cref="CookieLease.Path"/> is null when no cookies are configured.
        /// </summary>
        /// <param name="lane">The lane key, from <see cref="PlatformLane.For"/>.</param>
        /// <param name="cancellationToken">A token to abandon the wait.</param>
        /// <returns>The lease; disposing it releases that lane's file.</returns>
        /// <param name="youtube">Use the dedicated cookie configuration for YouTube.</param>
        public async Task<CookieLease> AcquireAsync(string lane, CancellationToken cancellationToken, bool youtube = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lane);

            var config = _configuration();
            var seed = youtube ? config?.YouTubeCookiesPath : config?.CookiesPath;
            if (string.IsNullOrWhiteSpace(seed) || _dataPath() is not { } folder)
            {
                return new CookieLease(null, null);
            }

            if (youtube && !CanUseYouTubeSeed(seed))
            {
                return new CookieLease(null, null);
            }

            if (youtube && lane == PlatformLane.Playback)
            {
                lane = "youtube-playback";
            }

            var gate = _locks.GetOrAdd(lane, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return new CookieLease(EnsureWorkingCopy(seed, folder, lane, youtube), gate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!youtube)
                {
                    LogSeedFailedOnce(seed, ex, lane);
                }
                gate.Release();
                return new CookieLease(null, null);
            }
        }

        private string? EnsureWorkingCopy(string seed, string folder, string lane, bool youtube)
        {
            if (!File.Exists(seed))
            {
                if (!youtube)
                {
                    LogSeedMissingOnce(seed, lane);
                }
                return null;
            }

            var stamp = File.GetLastWriteTimeUtc(seed).Ticks
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (youtube)
                stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + "\n" + stamp)));

            var workingPath = Path.Combine(folder, WorkingFileNameFor(lane));
            var stampPath = workingPath + ".seed";
            var seeded = File.Exists(stampPath)
                ? File.ReadAllText(stampPath).Trim()
                : null;

            if (!File.Exists(workingPath) || !string.Equals(stamp, seeded, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(folder);
                if (youtube)
                {
                    CopyPrivate(seed, workingPath);
                }
                else
                {
                    File.Copy(seed, workingPath, overwrite: true);
                }
                File.WriteAllText(stampPath, stamp);
                RestrictPermissions(workingPath);
                if (!youtube)
                {
                    LogSeeded(seed);
                }
            }

            if (youtube)
            {
                RestrictPermissions(workingPath);
            }

            _seedFailureLogged[lane] = false;
            return workingPath;
        }

        internal static bool CanUseYouTubeSeed(string path)
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
                {
                    return false;
                }

                using var reader = new StreamReader(path);
                return reader.BaseStream.Length is > 0 and <= 4_194_304
                    && reader.ReadLine() is "# Netscape HTTP Cookie File" or "# HTTP Cookie File";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static bool HasYouTubeSession(string path)
        {
            if (!CanUseYouTubeSeed(path))
                return false;

            try
            {
                var login = false;
                var signed = false;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var line in File.ReadLines(path))
                {
                    var values = line.Split('\t', 7);
                    if (values.Length != 7 || values[2] != "/" || string.IsNullOrWhiteSpace(values[6]))
                        continue;

                    var domain = values[0].StartsWith("#HttpOnly_", StringComparison.Ordinal) ? values[0][10..] : values[0];
                    var host = domain.TrimStart('.');
                    if ((!string.Equals(host, "youtube.com", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(host, "www.youtube.com", StringComparison.OrdinalIgnoreCase))
                        || !long.TryParse(values[4], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)
                        || (expiry != 0 && expiry <= now))
                        continue;

                    login |= values[5] == "LOGIN_INFO";
                    signed |= values[5] is "SAPISID" or "__Secure-1PAPISID" or "__Secure-3PAPISID";
                    if (login && signed)
                        return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            return false;
        }

        private static void CopyPrivate(string seed, string destination)
        {
            var temporary = destination + ".tmp";
            try
            {
                var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                File.Delete(temporary);
                using (var write = new FileStream(temporary, options))
                using (var read = File.OpenRead(seed))
                {
                    read.CopyTo(write);
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        private static void RestrictPermissions(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        private void LogSeedFailedOnce(string seed, Exception ex, string lane)
        {
            if (!_seedFailureLogged.TryGetValue(lane, out var logged) && logged)
            {
                _seedFailureLogged[lane] = true;
                LogSeedFailed(seed, ex);
            }
        }

        private void LogSeedMissingOnce(string seed, string lane)
        {
            if (!_seedFailureLogged.TryGetValue(lane, out var logged) && logged)
            {
                _seedFailureLogged[lane] = true;
                LogSeedMissing(seed);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var gate in _locks.Values)
            {
                gate.Dispose();
            }

            _locks.Clear();
        }

        [LoggerMessage(EventId = 20, Level = LogLevel.Information,
            Message = "Seeded working cookie file from {SeedPath}")]
        private partial void LogSeeded(string seedPath);

        [LoggerMessage(EventId = 21, Level = LogLevel.Warning,
            Message = "Cookie seed {SeedPath} does not exist; continuing without cookies")]
        private partial void LogSeedMissing(string seedPath);

        [LoggerMessage(EventId = 22, Level = LogLevel.Error,
            Message = "Could not read cookie seed {SeedPath}; continuing without cookies")]
        private partial void LogSeedFailed(string seedPath, Exception exception);
    }
}
