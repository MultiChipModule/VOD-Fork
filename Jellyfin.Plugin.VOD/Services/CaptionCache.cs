using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;

namespace Jellyfin.Plugin.VOD.Services
{
    internal sealed class CaptionCache(string directory, HttpClient? client = null)
    {
        private const int MaxCaptionBytes = 4 * 1024 * 1024;
        private static readonly HttpClient _http = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CheckCertificateRevocationList = true,
            UseCookies = false
        })
        {
            MaxResponseContentBufferSize = MaxCaptionBytes,
            Timeout = TimeSpan.FromSeconds(4)
        };

        private readonly object _diskLock = new();

        internal string DirectoryPath { get; } = Path.GetFullPath(directory);

        internal static bool IsEnabled(CaptionKind kind, PluginConfiguration config) =>
            kind == CaptionKind.Creator
            || (config.EnableAutomaticEnglishCaptions
                && (kind != CaptionKind.Translated || config.AllowTranslatedEnglishCaptions));

        internal CachedCaption? Find(string videoKey, PluginConfiguration config, bool creator)
        {
            var cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(config.CaptionCacheHours, 1, 720));
            foreach (var kind in Enum.GetValues<CaptionKind>())
            {
                if ((kind == CaptionKind.Creator) != creator || !IsEnabled(kind, config))
                {
                    continue;
                }

                var path = PathFor(videoKey, kind);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    return new CachedCaption(path, kind);
                }
            }

            return null;
        }

        internal async Task<CachedCaption?> FetchAsync(
            string videoKey, YouTubeCaption caption, PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (!YouTubeCaption.IsTimedText(caption.Url) || !IsEnabled(caption.Kind, config))
            {
                return null;
            }

            using var response = await (client ?? _http).GetAsync(caption.Url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("YouTube limited caption requests", null, HttpStatusCode.TooManyRequests);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!IsWebVtt(text) || Encoding.UTF8.GetByteCount(text) > MaxCaptionBytes)
            {
                return null;
            }

            return Store(videoKey, caption.Kind, CaptionNormalization.PlainWebVtt(text, singleLine: caption.Kind != CaptionKind.Creator), config);
        }

        internal static bool IsWebVtt(string text) =>
            text.AsSpan().TrimStart('\uFEFF').StartsWith("WEBVTT", StringComparison.Ordinal)
            && text.AsSpan().TrimStart('\uFEFF') is { Length: > 6 } header
            && header[6] is '\r' or '\n' or ' ' or '\t'
            && text.Contains(" --> ", StringComparison.Ordinal);

        internal CachedCaption Store(string videoKey, CaptionKind kind, string text, PluginConfiguration config)
        {
            var path = PathFor(videoKey, kind);
            lock (_diskLock)
            {
                Directory.CreateDirectory(DirectoryPath);
                var temporary = path + ".tmp";
                try
                {
                    File.WriteAllText(temporary, text);
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }

                Prune(path, config);
            }

            return new CachedCaption(path, kind);
        }

        internal bool Owns(string? path) =>
            path is not null && string.Equals(Path.GetDirectoryName(path), DirectoryPath, StringComparison.Ordinal)
            && path.EndsWith(".vtt", StringComparison.Ordinal);

        private string PathFor(string videoKey, CaptionKind kind)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(videoKey)));
            var variant = kind == CaptionKind.Creator ? "plain" : "single-line";
            return Path.Combine(DirectoryPath, string.Create(CultureInfo.InvariantCulture, $"{hash}-{kind}-{variant}.vtt"));
        }

        private void Prune(string keep, PluginConfiguration config)
        {
            var maxBytes = Math.Clamp(config.CaptionCacheMiB, 4, 1024) * 1024L * 1024;
            var cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(config.CaptionCacheHours, 1, 720));
            var keptBytes = new FileInfo(keep).Length;
            foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.vtt")
                         .Where(file => !string.Equals(file.FullName, keep, StringComparison.Ordinal))
                         .OrderByDescending(file => file.LastWriteTimeUtc))
            {
                if (file.LastWriteTimeUtc < cutoff || keptBytes + file.Length > maxBytes)
                {
                    file.Delete();
                }
                else
                {
                    keptBytes += file.Length;
                }
            }
        }
    }

    internal sealed record CachedCaption(string Path, CaptionKind Kind);
}
