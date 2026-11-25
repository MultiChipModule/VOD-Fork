using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services.Sync;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    /// <summary>
    /// Resolves a page URL into a directly playable stream URL by shelling out to yt-dlp.
    /// </summary>
    /// <remarks>
    /// Reads the selected stream, HLS master and YouTube caption metadata from the same yt-dlp extraction; neither YouTube nor Arte still publishes
    /// a muxed progressive format, so the bare URL means video without audio.
    /// </remarks>
    public sealed partial class YtDlpService : IYtDlpService, IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PlaylistTimeout = TimeSpan.FromMinutes(10);

        private readonly CookieJar _cookies;
        private readonly YtDlpThrottle _throttle;
        private readonly ILogger<YtDlpService> _logger;
        private readonly YouTubeResolutionCache _resolutions = new(TimeProvider.System);

        private readonly Func<PluginConfiguration?> _configuration;
        private readonly YouTubeExtractionGate _youtubeGate;
        private string? _lastYouTubeError;

        internal string? LastYouTubeError => Volatile.Read(ref _lastYouTubeError);

        private static readonly HttpClient _oEmbedClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
            MaxResponseContentBufferSize = 256 * 1024
        };

        internal YouTubeExtractionGate.Snapshot YouTubeStatus => _youtubeGate.Status();

        internal void RestrictYouTube(string reason) => _youtubeGate.Restricted(reason);

        /// <summary>Initializes a new instance of the <see cref="YtDlpService"/> class.</summary>
        /// <param name="cookies">The cookie jar, one working copy per lane.</param>
        /// <param name="throttle">Paces background calls, per platform.</param>
        /// <param name="logger">Logger instance.</param>
        public YtDlpService(CookieJar cookies, YtDlpThrottle throttle, ILogger<YtDlpService> logger)
            : this(cookies, throttle, logger, static () => Plugin.Instance?.Configuration,
                static () => Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "youtube-restriction.json") : null)
        {
        }

        internal YtDlpService(CookieJar cookies, YtDlpThrottle throttle, ILogger<YtDlpService> logger,
            Func<PluginConfiguration?> configuration, Func<string?>? statePath = null)
        {
            _cookies = cookies;
            _throttle = throttle;
            _logger = logger;
            _configuration = configuration;
            _youtubeGate = new(TimeProvider.System, configuration, statePath);
        }

        /// <summary>
        /// Resolves a page URL into something playable, or returns <see langword="null"/> on failure.
        /// </summary>
        /// <param name="pageUrl">The page URL as accepted by yt-dlp.</param>
        /// <param name="format">The yt-dlp format selector (e.g. <c>bv*[protocol^=m3u8]</c>).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Task<StreamResolution?> ResolveAsync(
            string pageUrl,
            string format,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
            ArgumentException.ThrowIfNullOrWhiteSpace(format);

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsYouTube(pageUrl))
                return ExtractStreamAsync(pageUrl, format, false, cancellationToken);

            var key = YouTubePageKey(pageUrl);
            if (_resolutions.TryGet(key, format) is { } cached)
            {
                if (_configuration()?.EnablePlaybackDiagnostics == true)
                    LogYouTubeCache(true);
                return Task.FromResult<StreamResolution?>(cached);
            }

            if (YouTubeStatus.CooldownUntil is not null)
                return Task.FromResult<StreamResolution?>(null);

            return _resolutions.ResolveAsync(key, format, token => ExtractStreamAsync(pageUrl, format, true, token),
                cancellationToken, _configuration()?.EnablePlaybackDiagnostics == true ? LogYouTubeCache : null);
        }

        [LoggerMessage(EventId = 14, Level = LogLevel.Information,
            Message = "VOD YouTube resolution cache hit={Hit}")]
        private partial void LogYouTubeCache(bool hit);

        internal StreamResolution? TryGetCachedYouTubeResolution(string page, string format) => _resolutions.TryGet(YouTubePageKey(page), format);

        internal void InvalidateResolution(string page, string format, StreamResolution served)
            => _resolutions.Invalidate(YouTubePageKey(page), format, served);

        private string YouTubePageKey(string page)
        {
            var video = TryGetYouTubeVideoId(page) ?? page;
            var config = _configuration();
            var seed = config?.YouTubeCookiesPath;
            // Keep a new login or runtime choice separate from cached anonymous or failed attempts.
            var version = !string.IsNullOrWhiteSpace(seed) && File.Exists(seed)
                ? File.GetLastWriteTimeUtc(seed) : default;
            return string.Create(CultureInfo.InvariantCulture, $"{video}|{config?.JsRuntime}|{seed}|{version.Ticks}");
        }

        /// <inheritdoc />
        public void Dispose() => _resolutions.Dispose();

        private async Task<StreamResolution?> ExtractStreamAsync(
            string pageUrl, string format, bool youtube, CancellationToken cancellationToken)
        {
            var startInfo = BuildBaseStartInfo();

            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(format);
            if (youtube)
            {
                startInfo.ArgumentList.Add("--print");
                startInfo.ArgumentList.Add("%(.{id,manifest_url,url,formats,subtitles,automatic_captions,upload_date,timestamp,description,thumbnail,duration})j");
            }
            else
            {
                startInfo.ArgumentList.Add("--print");
                startInfo.ArgumentList.Add("%(manifest_url)s");
                startInfo.ArgumentList.Add("--print");
                startInfo.ArgumentList.Add("%(url)s");
            }
            startInfo.ArgumentList.Add(pageUrl);

            var sw = Stopwatch.StartNew();
            var stdout = await RunProcessAsync(startInfo, Timeout, cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();

            if (stdout is null)
                return null;

            if (youtube)
            {
                var result = ParseYouTubeResolution(stdout);
                if (result is not null)
                {
                    LogResolved("YouTube", result.IsManifest, sw.ElapsedMilliseconds);
                }

                return result;
            }

            var lines = stdout.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length == 0)
            {
                LogResolveFailed(pageUrl, 0, "yt-dlp printed nothing");
                return null;
            }

            var manifestUrl = lines[0];
            var hasManifest = !string.Equals(manifestUrl, "NA", StringComparison.Ordinal);
            var resolved = hasManifest ? manifestUrl : lines[^1];

            LogResolved(pageUrl, hasManifest, sw.ElapsedMilliseconds);
            return new StreamResolution(resolved, hasManifest);
        }

        internal static StreamResolution? ParseYouTubeResolution(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var manifest = YouTubeCaption.ReadString(root, "manifest_url");
                var url = manifest ?? YouTubeCaption.ReadString(root, "url");
                return url is null ? null : new StreamResolution(url, manifest is not null)
                {
                    EnglishCaptions = YouTubeCaption.ReadEnglish(root),
                    Metadata = VideoMetadata.ParseJson(json)
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Runs <c>yt-dlp --flat-playlist -J [--playlist-end N] [--dateafter YYYYMMDD]</c> and returns the parsed result.
        /// Returns <see langword="null"/> when yt-dlp exits non-zero or output cannot be parsed.
        /// </summary>
        /// <param name="url">The playlist or channel URL.</param>
        /// <param name="maxEntries">When set, passes <c>--playlist-end N</c>.</param>
        /// <param name="dateAfter">
        /// When set, passes <c>--dateafter YYYYMMDD</c> so yt-dlp stops enumerating entries older than this date.
        /// Use when <c>RetentionDays</c> is configured to avoid fetching videos that will be immediately discarded.
        /// Note: behaviour varies by extractor — yt-dlp may still enumerate all entries and filter client-side.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="lane">
        /// The throttle lane to spend, when it is not the URL's own platform — playback uses
        /// <see cref="PlatformLane.Playback"/> so a started video cannot queue behind a
        /// platform's backoff.
        /// </param>
        /// <param name="metadataLanguage">
        /// A BCP-47 prefix pinned as <c>--extractor-args "youtube:lang=XX"</c>, or null to pass
        /// nothing.
        /// </param>
        public async Task<PlaylistInfo?> GetPlaylistEntriesAsync(
            string url,
            int? maxEntries,
            DateTimeOffset? dateAfter,
            CancellationToken cancellationToken,
            string? lane = null,
            string? metadataLanguage = null)
        {
            // oEmbed is the public metadata endpoint for a direct video URL. It never
            // resolves a stream and is not used as fallback for failed authenticated yt-dlp.
            if (IsYouTube(url) && TryGetYouTubeVideoId(url) is { } videoId
                && SourceTypeDetector.Detect(url) != SourceType.YoutubePlaylist)
            {
                return await GetPublicVideoListingAsync(videoId, cancellationToken).ConfigureAwait(false);
            }

            lane ??= PlatformLane.For(url);

            // The lease is taken before the cookie file, so the pacing wait happens outside the
            // lane's cookie lock rather than holding it for three seconds.
            using var lease = IsYouTube(url) ? null : await _throttle
                .EnterAsync(lane, YtDlpCallKind.Listing, cancellationToken)
                .ConfigureAwait(false);

            var startInfo = BuildBaseStartInfo();

            foreach (var argument in BuildPlaylistArguments(
                url, maxEntries, dateAfter, lane, metadataLanguage))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var stdout = await RunProcessAsync(startInfo, PlaylistTimeout, cancellationToken, lane, lease)
                .ConfigureAwait(false);
            if (stdout is null)
                return null;

            var info = PlaylistInfo.ParseJson(stdout);
            if (info is null)
                LogParseFailed(url, "flat-playlist");

            // One process, several requests: a listing is charged for the pages it really
            // fetched, not for one call.
            if (info is not null)
            {
                lease?.Charge(YtDlpThrottle.ExtraListingCalls(info.Entries.Count));
            }

            return info;
        }

        /// <summary>
        /// Runs <c>yt-dlp -J --no-playlist</c> for a single video and returns the parsed metadata.
        /// Returns <see langword="null"/> when yt-dlp exits non-zero or output cannot be parsed.
        /// </summary>
        /// <param name="url">The video page URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<VideoMetadata?> GetVideoMetadataAsync(
            string url,
            CancellationToken cancellationToken)
            => (await GetMetadataAsync(url, cancellationToken).ConfigureAwait(false)).Metadata;

        /// <summary>
        /// Runs the same call as <see cref="GetVideoMetadataAsync"/> and additionally says WHY no
        /// metadata came back.
        /// </summary>
        /// <param name="url">The video or channel page URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The metadata, or the reason there is none.</returns>
        /// <remarks>
        /// A channel that is off air has no metadata to return — yt-dlp exits 1 with "The channel is
        /// not currently live" and prints no JSON, and <c>--ignore-no-formats-error</c> does not
        /// suppress it. Callers that must tell that apart from a rate limit or a network failure
        /// need this overload: the live probe concludes "nothing is broadcasting" from the former
        /// and refuses to conclude anything from the latter.
        /// </remarks>
        public async Task<MetadataResult> GetMetadataAsync(
            string url,
            CancellationToken cancellationToken)
        {
            var lane = PlatformLane.For(url);
            using var lease = IsYouTube(url) ? null : await _throttle
                .EnterAsync(lane, YtDlpCallKind.Resolution, cancellationToken)
                .ConfigureAwait(false);

            var startInfo = BuildBaseStartInfo();

            startInfo.ArgumentList.Add("-J");
            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("--no-warnings");
            // A scheduled live has no formats, and yt-dlp exits non-zero on it without this flag, so
            // its metadata would never arrive. Measured 2026-10-03 (spec F14).
            startInfo.ArgumentList.Add("--ignore-no-formats-error");
            startInfo.ArgumentList.Add(url);

            var run = await RunProcessRawAsync(startInfo, Timeout, cancellationToken, lane, lease)
                .ConfigureAwait(false);

            var result = Interpret(run.ExitCode, run.Stdout, run.StdErr, run.TimedOut);

            switch (result.Outcome)
            {
                case MetadataOutcome.ChannelOffAir:
                    LogChannelOffAir(url);
                    break;
                case MetadataOutcome.Failed when run.ExitCode != 0:
                    LogResolveFailed(IsYouTube(url) ? "YouTube" : url, run.ExitCode, run.StdErr.Trim());
                    break;
                case MetadataOutcome.Failed when result.Metadata is null:
                    LogParseFailed(url, "video metadata");
                    break;
                case MetadataOutcome.Failed:
                    LogUnavailable(url);
                    break;
                default:
                    break;
            }

            return result;
        }

        /// <summary>
        /// Whether yt-dlp's standard error says the channel is simply not broadcasting.
        /// </summary>
        /// <param name="stderr">Everything yt-dlp wrote to standard error.</param>
        /// <returns><see langword="true"/> only for yt-dlp's own not-live wording.</returns>
        /// <remarks>
        /// Deliberately one phrase: the message yt-dlp's <c>UserNotLive</c> carries, measured on
        /// 2026-10-04 as "ERROR: [twitch:stream] riotgames: The channel is not currently live".
        /// Anything else stays a failure. Widening this to "a non-zero exit means absent" would let
        /// a rate-limited call retract a broadcast that is still running, and the probe deletes
        /// library items on that conclusion.
        /// </remarks>
        internal static bool IndicatesChannelOffAir(string? stderr)
            => stderr is not null
               && stderr.Contains(NotLiveSignal, StringComparison.OrdinalIgnoreCase);

        // yt-dlp's UserNotLive message, verbatim.
        private const string NotLiveSignal = "The channel is not currently live";

        /// <summary>
        /// Runs <c>yt-dlp -J --playlist-items 1</c> and returns the first entry's full metadata.
        /// Returns <see langword="null"/> when yt-dlp exits non-zero or the output cannot be parsed.
        /// </summary>
        /// <param name="url">A playlist or channel-tab URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// Deliberately not <c>--flat-playlist</c>: a flat Twitch listing carries no live status, so
        /// one full extraction of the head answers "is this channel broadcasting, and what is the
        /// video" in a single call. Measured at 2.1 s (spec F4).
        /// </remarks>
        public async Task<VideoMetadata?> GetFirstEntryMetadataAsync(
            string url,
            CancellationToken cancellationToken)
        {
            var lane = PlatformLane.For(url);
            using var lease = IsYouTube(url) ? null : await _throttle
                .EnterAsync(lane, YtDlpCallKind.Resolution, cancellationToken)
                .ConfigureAwait(false);

            var startInfo = BuildBaseStartInfo();

            startInfo.ArgumentList.Add("-J");
            startInfo.ArgumentList.Add("--playlist-items");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--ignore-no-formats-error");
            startInfo.ArgumentList.Add(url);

            var stdout = await RunProcessAsync(startInfo, PlaylistTimeout, cancellationToken, lane, lease)
                .ConfigureAwait(false);
            if (stdout is null)
                return null;

            var meta = VideoMetadata.ParseFirstEntryJson(stdout);
            if (meta is null)
            {
                LogParseFailed(url, "first playlist entry");
                return null;
            }

            if (meta.IsUnavailable)
            {
                LogUnavailable(url);
                return null;
            }

            return meta;
        }

        /// <summary>
        /// Turns a finished yt-dlp run into a verdict, as a pure function of its outputs.
        /// </summary>
        /// <remarks>
        /// Extracted so the verdict is testable without running a process. It is also where the
        /// reason for a failure survives: the previous code returned the shared
        /// <c>MetadataResult.Failed</c> singleton, which carried neither stderr nor the parsed
        /// metadata — so <c>availability</c> never reached a caller, and 14 of 16 failing videos
        /// on the test instance recorded nothing but the word "Failed".
        /// </remarks>
        /// <param name="exitCode">yt-dlp's exit code; -1 when it had to be killed.</param>
        /// <param name="stdout">Everything it wrote to standard output.</param>
        /// <param name="stderr">Everything it wrote to standard error.</param>
        /// <param name="timedOut">Whether it had to be killed for taking too long.</param>
        /// <returns>The verdict, carrying stderr and whatever metadata parsed.</returns>
        internal static MetadataResult Interpret(
            int exitCode, string stdout, string stderr, bool timedOut)
        {
            if (timedOut)
                return new MetadataResult(MetadataOutcome.Failed, null, stderr, TimedOut: true);

            if (exitCode != 0)
            {
                return IndicatesChannelOffAir(stderr)
                    ? new MetadataResult(MetadataOutcome.ChannelOffAir, null, stderr)
                    : new MetadataResult(MetadataOutcome.Failed, null, stderr);
            }

            var meta = VideoMetadata.ParseJson(stdout);
            if (meta is null)
                return new MetadataResult(MetadataOutcome.Failed, null, stderr);

            // The metadata is KEPT on this failure path. It holds `availability`, which is the
            // only thing that distinguishes an age-restricted video from a deleted one.
            return meta.IsUnavailable
                ? new MetadataResult(MetadataOutcome.Failed, meta, stderr)
                : new MetadataResult(MetadataOutcome.Succeeded, meta, stderr);
        }

        /// <summary>
        /// The arguments a flat-playlist listing needs, as a pure function of its inputs.
        /// </summary>
        /// <remarks>
        /// Extracted so it can be tested directly. The alternative — faking the process — would put
        /// test scaffolding in production code to verify something that is a function of its
        /// arguments and nothing else.
        /// </remarks>
        /// <param name="url">The playlist or channel URL, passed last as yt-dlp expects.</param>
        /// <param name="maxEntries">
        /// The cap, or null for no cap. Null is NOT the same as zero: a source configured with
        /// <c>MaxVideosPerSource = 0</c> means unlimited, and passing <c>--playlist-end 0</c> would
        /// fetch nothing at all. Callers map zero to null.
        /// </param>
        /// <param name="dateAfter">When set, passes <c>--dateafter YYYYMMDD</c>.</param>
        /// <param name="lane">The platform lane, which decides whether a language may be pinned.</param>
        /// <param name="metadataLanguage">
        /// A BCP-47 prefix pinned as <c>--extractor-args "youtube:lang=XX"</c>, or null.
        /// <para>
        /// Measured on 2026-10-19: a flat listing serves the LOCALIZED title and honours this
        /// argument, while a single-video metadata call serves the ORIGINAL and ignores it. Pinning
        /// it makes a change detector's fingerprint deterministic — left unpinned, the language
        /// served depends on the session and drifts, which would mark every video as changed on
        /// every pass.
        /// </para>
        /// <para>
        /// Applied on the YouTube lane only: it is a YouTube extractor argument, and this project
        /// never carries one platform's rule to another.
        /// </para>
        /// </param>
        /// <returns>The arguments, in the order yt-dlp receives them.</returns>
        internal static IReadOnlyList<string> BuildPlaylistArguments(
            string url,
            int? maxEntries,
            DateTimeOffset? dateAfter,
            string lane,
            string? metadataLanguage)
        {
            var args = new List<string> { "--flat-playlist", "-J" };

            if (maxEntries.HasValue)
            {
                args.Add("--playlist-end");
                args.Add(maxEntries.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (dateAfter.HasValue)
            {
                args.Add("--dateafter");
                args.Add(dateAfter.Value.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }

            if (IsYouTube(url))
            {
                args.Add("--extractor-args");
                args.Add("youtubetab:approximate_date");
                if (!string.IsNullOrWhiteSpace(metadataLanguage)
                    && string.Equals(lane, PlatformLane.Youtube, StringComparison.OrdinalIgnoreCase))
                {
                    args.Add("--extractor-args");
                    args.Add($"youtube:lang={metadataLanguage}");
                }
            }

            args.Add("--no-warnings");
            args.Add(url);
            return args;
        }

        private ProcessStartInfo BuildBaseStartInfo() => new()
        {
            FileName = _configuration()?.YtDlpPath ?? "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        private static void ApplyCookies(ProcessStartInfo startInfo, CookieLease cookies)
        {
            if (cookies.Path is not null)
            {
                startInfo.ArgumentList.Insert(0, "--cookies");
                startInfo.ArgumentList.Insert(1, cookies.Path);
            }
        }

        internal static void ApplyJsRuntime(ProcessStartInfo startInfo, string? runtime, bool youtube)
        {
            if (string.IsNullOrWhiteSpace(runtime))
                return;

            if (youtube)
                startInfo.ArgumentList.Insert(0, "--no-js-runtimes");
            startInfo.ArgumentList.Insert(youtube ? 1 : 0, "--js-runtimes");
            startInfo.ArgumentList.Insert(youtube ? 2 : 1, runtime);
        }

        /// <summary>What one yt-dlp process left behind.</summary>
        private readonly record struct ProcessRun(
            int ExitCode, string Stdout, string StdErr, bool TimedOut = false);

        /// <summary>Runs a yt-dlp process and returns stdout, or null on non-zero exit.</summary>
        private async Task<string?> RunProcessAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string? lane = null,
            YtDlpLease? lease = null)
        {
            var run = await RunProcessRawAsync(startInfo, timeout, cancellationToken, lane, lease)
                .ConfigureAwait(false);

            if (run.ExitCode != 0)
            {
                LogResolveFailed(IsYouTube(startInfo.ArgumentList[^1]) ? "YouTube" : startInfo.ArgumentList[^1], run.ExitCode, run.StdErr.Trim());
                return null;
            }

            return run.Stdout;
        }

        /// <summary>
        /// Runs a yt-dlp process and hands back its exit code and both streams, so a caller that
        /// can read meaning into a specific failure gets the chance to.
        /// </summary>
        /// <remarks>
        /// The throttle's verdict is settled here rather than at each call site: a timeout is
        /// <b>returned</b> from this method, not thrown — a rate-limited session stops answering
        /// rather than saying so, which makes the timeout itself the signal — and
        /// <see cref="RunProcessAsync"/> does not hand stderr back to its callers, so two of the
        /// four background entry points could not judge their own outcome. One place knows
        /// "exit code, stderr, timeout" and reports it.
        /// </remarks>
        private async Task<ProcessRun> RunProcessRawAsync(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string? lane = null,
            YtDlpLease? lease = null)
        {
            var youtube = IsYouTube(startInfo.ArgumentList[^1]);
            var config = _configuration();
            var authenticated = youtube && !string.IsNullOrWhiteSpace(config?.YouTubeCookiesPath);
            if (youtube && YouTubeStatus.CooldownUntil is not null)
                return new ProcessRun(-1, string.Empty, "YouTube rate-limit gate deferred extraction");
            if (authenticated && !CookieJar.HasYouTubeSession(config!.YouTubeCookiesPath))
                return YouTubeFailure("The YouTube cookie file needs a current signed-in session. Export fresh cookies and check owner-only file access.");

            using var youtubeLease = youtube
                ? await _youtubeGate.EnterAsync(lane is null, cancellationToken).ConfigureAwait(false) : null;
            if (youtube && youtubeLease is null)
                return new ProcessRun(-1, string.Empty, "YouTube rate-limit gate deferred extraction");

            using var cookies = await _cookies.AcquireAsync(lane ?? PlatformLane.Playback, cancellationToken, youtube).ConfigureAwait(false);
            if (authenticated && (cookies.Path is not { } path || !CookieJar.HasYouTubeSession(path)))
                return YouTubeFailure("The YouTube cookie file could not be prepared or no longer contains a signed-in session. Export fresh cookies.");

            if (youtube)
            {
                // Inspect extractor warnings even on successful exit. yt-dlp can continue with
                // public formats after reporting that the configured session was rotated.
                startInfo.ArgumentList.Remove("--no-warnings");
                startInfo.ArgumentList.Insert(0, "--ignore-config");
                startInfo.ArgumentList.Insert(1, "--extractor-retries");
                startInfo.ArgumentList.Insert(2, "0");
                startInfo.ArgumentList.Insert(3, "--retries");
                startInfo.ArgumentList.Insert(4, "0");
            }
            ApplyCookies(startInfo, cookies);
            ApplyJsRuntime(startInfo, config?.JsRuntime, youtube);

            using var process = new Process { StartInfo = startInfo };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                process.Start();
            }
            catch (Win32Exception) when (youtube)
            {
                return YouTubeFailure("yt-dlp could not start. Check the configured executable path and permissions.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);

                // F9: a rate-limited session stops answering rather than saying so, so a timeout
                // is a refusal on every platform and backs its lane off.
                lease?.RateLimited();
                if (youtube)
                    Volatile.Write(ref _lastYouTubeError, "YouTube extraction timed out. Check server connectivity before retrying.");

                // Returned, not thrown. A timeout is the single most informative failure this
                // service can report — it is how a rate limit actually manifests — and an
                // exception reduced it to a message string that callers folded into a generic
                // failure. The caller now sees the flag and can classify it.
                return new ProcessRun(-1, string.Empty, string.Empty, TimedOut: true);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (youtube)
            {
                var restriction = YouTubeExtractionGate.DetectRestriction(stderr);
                if (restriction is not null)
                {
                    _youtubeGate.Restricted(restriction);
                    return YouTubeFailure("YouTube rate-limit restriction: " + restriction + ". Wait until the dashboard retry time.");
                }

                if (authenticated && IsYouTubeCookieFailure(stderr))
                    return YouTubeFailure("YouTube rejected the configured account cookies. Export fresh cookies for the dedicated account.");

                var error = process.ExitCode == 0 ? null : stderr.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase)
                    ? "Sign in to confirm your age"
                    : "YouTube did not supply playable media. Check yt-dlp and the configured JavaScript runtime.";
                if (stderr.Contains("No supported JavaScript runtime", StringComparison.OrdinalIgnoreCase))
                    error = "yt-dlp could not use a JavaScript runtime. Check its name, path and execute permissions.";
                Volatile.Write(ref _lastYouTubeError, error);
                // Never forward raw YouTube stderr, which can contain local paths or signed URLs.
                stderr = error ?? string.Empty;
            }

            if (lease is not null)
            {
                if (lane is not null && PlatformLimits.IsPlatformRefusal(lane, stderr, timedOut: false))
                {
                    lease.RateLimited();
                }
                else
                {
                    lease.Succeeded();
                }
            }

            return new ProcessRun(process.ExitCode, stdout, stderr);
        }

        private ProcessRun YouTubeFailure(string message)
        {
            Volatile.Write(ref _lastYouTubeError, message);
            return new ProcessRun(-1, string.Empty, message);
        }

        internal static bool IsYouTubeCookieFailure(string stderr) =>
            stderr.Contains("YouTube account cookies are no longer valid", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("YouTube account cookies have expired", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("does not look like a Netscape format cookies file", StringComparison.OrdinalIgnoreCase);

        internal static string? TryGetYouTubeVideoId(string url)
        {
            if (!YouTubeCaption.IsYouTubePage(url) || !Uri.TryCreate(url, UriKind.Absolute, out var page))
                return null;

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(page.Query);
            var path = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = page.AbsolutePath == "/watch" && query.TryGetValue("v", out var video) ? video.ToString()
                : page.IdnHost == "youtu.be" && path.Length == 1 ? path[0]
                : path.Length == 2 && path[0] is "shorts" or "live" or "embed" ? path[1] : null;
            return id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? id : null;
        }

        private async Task<PlaylistInfo?> GetPublicVideoListingAsync(string id, CancellationToken cancellationToken)
        {
            using var lease = await _youtubeGate.EnterAsync(playback: false, cancellationToken).ConfigureAwait(false);
            if (lease is null)
                return null;

            try
            {
                var page = $"https://www.youtube.com/watch?v={id}";
                using var response = await _oEmbedClient.GetAsync(
                    $"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(page)}", cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    _youtubeGate.Restricted("Rate limited");
                if (!response.IsSuccessStatusCode)
                    return null;

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var title = YouTubeCaption.ReadString(document.RootElement, "title");
                return title is null ? null : new PlaylistInfo
                {
                    Title = title,
                    Entries = [new PlaylistEntry
                    {
                        Id = id, Title = title, VideoUrl = page, IeKey = "Youtube",
                        ThumbnailUrl = YouTubeCaption.ReadString(document.RootElement, "thumbnail_url")
                    }]
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return null;
            }
        }

        private static bool IsYouTube(string url) => YouTubeCaption.IsYouTubePage(url);

        private void TryKill(Process process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (SystemException ex) { LogKillFailed(ex); }
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "Resolved {PageUrl} in {ElapsedMs} ms (manifest: {HasManifest})")]
        private partial void LogResolved(string pageUrl, bool hasManifest, long elapsedMs);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error,
            Message = "yt-dlp failed on {PageUrl} exit={ExitCode}: {StdErr}")]
        private partial void LogResolveFailed(string pageUrl, int exitCode, string stdErr);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
            Message = "Could not kill yt-dlp process after timeout")]
        private partial void LogKillFailed(Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
            Message = "yt-dlp output for {Url} ({Mode}) could not be parsed as JSON")]
        private partial void LogParseFailed(string url, string mode);

        [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
            Message = "yt-dlp reports {Url} is not broadcasting")]
        private partial void LogChannelOffAir(string url);

        [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
            Message = "yt-dlp lists no format for {Url}: the video is private, removed or otherwise unavailable")]
        private partial void LogUnavailable(string url);
    }
}
