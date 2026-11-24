using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services
{
    internal sealed partial class CaptionPlaybackFilter(
        ILibraryManager library,
        IUserManager users,
        IMediaSourceManager mediaSources,
        IMediaStreamRepository streams,
        IVodDatabase database,
        YtDlpService ytDlp,
        CaptionCache cache,
        ILogger<CaptionPlaybackFilter> logger,
        StreamDescriber? describer = null) : IDisposable
    {
        private readonly PlaylistCache _missingTracks = new();
        private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();

        internal async Task<bool> TryPrepareAsync(Guid itemId, Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            try
            {
                return await PrepareAsync(itemId, userId, config, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogCaptionFailure(itemId, "timeout");
            }
#pragma warning disable CA1031 // A caption failure must leave Jellyfin's original playback action available.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                // Exception messages can contain signed timed-text URLs.
                LogCaptionFailure(itemId, ex.GetType().Name);
            }

            return false;
        }

        private async Task<bool> PrepareAsync(Guid itemId, Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var user = users.GetUserById(userId);
            if (user is null || library.GetItemById<BaseItem>(itemId, user) is not Video item
                || item.GetProviderId("VOD") is not { Length: > 0 } vodId
                || database.GetVideoByUuid(vodId) is not { } row
                || !YouTubeCaption.IsYouTubePage(row.VideoUrl)
                || row.LiveStatus != Sync.LiveStatus.NotLive)
            {
                return false;
            }

            var gate = _locks[(int)((uint)StringComparer.Ordinal.GetHashCode(vodId) % (uint)_locks.Length)];
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PrepareVersionsAsync(item, vodId, row.NativeId ?? row.VideoUrl, row.VideoUrl, user, config, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            return true;
        }

        private async Task PrepareVersionsAsync(
            Video item, string vodId, string videoKey, string url, Jellyfin.Database.Implementations.Entities.User user,
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (describer is null && !ReadStreams(item.Id).Any(stream => stream.Type == MediaStreamType.Video))
            {
                // Let the server probe first. Otherwise its probe could replace the external track
                // before PlaybackInfo is populated.
                await mediaSources.GetPlaybackMediaSources(item, user, true, false, cancellationToken).ConfigureAwait(false);
            }

            var versions = item.GetAllVersions()
                .Where(version => string.Equals(version.GetProviderId("VOD"), vodId, StringComparison.Ordinal))
                .Select(version => (Item: version, Stored: ReadStreams(version.Id))).ToArray();
            foreach (var version in versions.Where(version => !StreamDescriber.HasCurrentYouTubeLayout(version.Stored)))
            {
                if (describer is not null
                    && await describer.RefreshYouTubeLayoutAsync(config, version.Item, vodId, cancellationToken)
                        .ConfigureAwait(false) is { } probed)
                {
                    // Store real physical rows before attempting optional subtitle downloads.
                    // Keep authenticated external tracks from the previous layout.
                    var updated = probed.Concat(version.Stored.Where(stream => stream.IsExternal)).ToArray();
                    streams.SaveMediaStreams(version.Item.Id, updated, cancellationToken);
                    LogLayoutRepaired(version.Item.Id, version.Stored.Count, probed.Count);
                }
            }

            using var downloadBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var captions = await FindCaptionsAsync(videoKey, url, config, downloadBudget, cancellationToken)
                .ConfigureAwait(false);
            foreach (var version in versions)
            {
                var stored = ReadStreams(version.Item.Id);
                if (StreamDescriber.HasCurrentYouTubeLayout(stored)
                    && UpdateStreams(stored, captions, cache) is { } updated)
                {
                    streams.SaveMediaStreams(version.Item.Id, updated, cancellationToken);
                }
            }
        }

        private async Task<IReadOnlyList<CachedCaption>> FindCaptionsAsync(
            string videoKey, string url, PluginConfiguration config, CancellationTokenSource downloadBudget,
            CancellationToken cancellationToken)
        {
            var creator = cache.Find(videoKey, config, creator: true);
            var automatic = cache.Find(videoKey, config, creator: false);
            var creatorKey = videoKey + ":creator";
            var automaticKey = videoKey + (config.AllowTranslatedEnglishCaptions ? ":translated" : ":automatic");
            var needCreator = creator is null && !_missingTracks.TryGet(creatorKey, out _);
            var needAutomatic = config.EnableAutomaticEnglishCaptions && automatic is null
                && !_missingTracks.TryGet(automaticKey, out _);
            if (needCreator || needAutomatic)
            {
                var resolution = await ytDlp.ResolveAsync(url, StreamCacheKey.DefaultFormat, cancellationToken)
                    .ConfigureAwait(false);
                if (resolution is not null)
                {
                    downloadBudget.CancelAfter(TimeSpan.FromSeconds(5));
                    if (needCreator)
                    {
                        creator = await FetchTrackAsync(creatorKey, videoKey,
                            resolution.EnglishCaptions.Where(track => track.Kind == CaptionKind.Creator),
                            config, downloadBudget.Token, cancellationToken).ConfigureAwait(false);
                    }

                    if (needAutomatic)
                    {
                        automatic = await FetchTrackAsync(automaticKey, videoKey,
                            resolution.EnglishCaptions.Where(track => track.Kind != CaptionKind.Creator
                                && CaptionCache.IsEnabled(track.Kind, config)),
                            config, downloadBudget.Token, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return new[] { creator, automatic }.OfType<CachedCaption>()
                .Where(track => File.Exists(track.Path)).ToArray();
        }

        private async Task<CachedCaption?> FetchTrackAsync(
            string missingKey, string videoKey, IEnumerable<YouTubeCaption> candidates, PluginConfiguration config,
            CancellationToken budget, CancellationToken cancellationToken)
        {
            var tracks = candidates.GroupBy(track => track.Kind).SelectMany(group => group.Take(2)).ToArray();
            if (tracks.Length == 0)
            {
                _missingTracks.Set(missingKey, string.Empty);
            }

            foreach (var track in tracks)
            {
                try
                {
                    if (await cache.FetchAsync(videoKey, track, config, budget).ConfigureAwait(false) is { } result)
                    {
                        return result;
                    }
                }
                catch (HttpRequestException)
                {
                    // A lower-ranked rendition may still be available.
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            return null;
        }

        internal static IReadOnlyList<MediaStream>? UpdateStreams(
            IReadOnlyList<MediaStream> existing, IReadOnlyList<CachedCaption> captions, CaptionCache cache)
        {
            const int CreatorIndex = 1000;
            var previous = existing.Where(stream => stream.IsExternal && cache.Owns(stream.Path)).ToArray();
            var untouched = existing.Except(previous).ToList();
            var hasLocalEnglish = untouched.Any(stream => stream.IsExternal && IsEnglishSubtitle(stream));
            var result = new List<MediaStream>(untouched);
            foreach (var caption in captions)
            {
                var isCreator = caption.Kind == CaptionKind.Creator;
                if (isCreator && hasLocalEnglish)
                {
                    continue;
                }

                // Reserve a slot for each kind so adding a creator track never renumbers an
                // active or remembered automatic track. Keep ordinary FFmpeg indices intact.
                var index = isCreator ? CreatorIndex : CreatorIndex + 1;
                while (untouched.Any(stream => stream.Index == index))
                {
                    index += 2;
                }

                var prior = previous.FirstOrDefault(stream => stream.Index == index
                    && string.Equals(stream.Path, caption.Path, StringComparison.Ordinal)
                    && stream.IsDefault == isCreator);
                result.Add(prior ?? new MediaStream
                {
                    Index = index,
                    Type = MediaStreamType.Subtitle,
                    Codec = "vtt",
                    Language = "eng",
                    Title = caption.Kind switch
                    {
                        CaptionKind.Creator => "English (Creator-uploaded)",
                        CaptionKind.Translated => "English (Auto-translated)",
                        _ => "English (Auto-generated)"
                    },
                    IsDefault = isCreator,
                    IsExternal = true,
                    SupportsExternalStream = true,
                    Path = caption.Path
                });
            }

            return existing.SequenceEqual(result) ? null : result;
        }

        private static bool IsEnglishSubtitle(MediaStream stream) =>
            stream.Type == MediaStreamType.Subtitle && stream.Language is { } language
            && YouTubeCaption.IsEnglish(language);

        internal bool IsVisibleYouTubeVideo(Guid itemId, Guid userId)
        {
            return users.GetUserById(userId) is { } user
                && library.GetItemById<BaseItem>(itemId, user) is Video item
                && item.GetProviderId("VOD") is { Length: > 0 } vodId
                && database.GetVideoByUuid(vodId) is { } video
                && YouTubeCaption.IsYouTubePage(video.VideoUrl);
        }

        internal bool IsManagedSubtitle(Guid mediaSourceId, int index) =>
            ReadStreams(mediaSourceId).Any(stream => stream.Index == index
                && stream.Type == MediaStreamType.Subtitle && stream.IsExternal && cache.Owns(stream.Path));

        internal bool CanReadSubtitle(Guid itemId, Guid mediaSourceId, Guid userId)
        {
            var user = users.GetUserById(userId);
            return user is not null
                && library.GetItemById<BaseItem>(itemId, user) is Video
                && (mediaSourceId == itemId || library.GetItemById<BaseItem>(mediaSourceId, user) is Video);
        }

        private IReadOnlyList<MediaStream> ReadStreams(Guid id) =>
            streams.GetMediaStreams(new MediaStreamQuery { ItemId = id });

        [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Could not prepare captions for {ItemId} ({Reason})")]
        private partial void LogCaptionFailure(Guid itemId, string reason);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
            Message = "Reprobed YouTube media source {ItemId}: replaced {StoredCount} stored streams with {PhysicalCount} physical streams")]
        private partial void LogLayoutRepaired(Guid itemId, int storedCount, int physicalCount);

        public void Dispose()
        {
            foreach (var gate in _locks)
            {
                gate.Dispose();
            }
        }
    }

    // The native subtitle endpoints also support tokenless clients. Require a Jellyfin user
    // with library access for this plugin's cached files, leaving existing tracks alone.
    internal sealed partial class CaptionPlaybackDispatch : IAsyncActionFilter
    {
        internal static bool IsPlaybackInfo(string? controller, string action) =>
            controller == "Jellyfin.Api.Controllers.MediaInfoController"
            && action is "GetPlaybackInfo" or "GetPostedPlaybackInfo";

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.ActionDescriptor is ControllerActionDescriptor action
                && context.ModelState.IsValid && Plugin.Instance?.Configuration is { } config)
            {
                var http = context.HttpContext;
                var userId = Guid.Empty;
                var authenticated = http.User.Identity?.IsAuthenticated == true
                    && Guid.TryParse(http.User.FindFirst("Jellyfin-UserId")?.Value, out userId) && userId != Guid.Empty;
                var controller = action.ControllerTypeInfo.FullName;

                if (IsPlaybackInfo(controller, action.ActionName)
                    && authenticated && context.ActionArguments.TryGetValue("itemId", out var value) && value is Guid itemId)
                {
                    var prepared = await http.RequestServices.GetRequiredService<CaptionPlaybackFilter>()
                        .TryPrepareAsync(itemId, userId, config, http.RequestAborted).ConfigureAwait(false);
                    var executed = await next().ConfigureAwait(false);
                    if (prepared && config.EnablePlaybackDiagnostics)
                    {
                        var logger = http.RequestServices.GetRequiredService<ILogger<CaptionPlaybackDispatch>>();
                        var client = ClientFamily(http.User.FindFirst("Jellyfin-Client")?.Value,
                            http.Request.Headers.UserAgent.ToString());
                        var info = (executed.Result as ObjectResult)?.Value as PlaybackInfoResponse;
                        foreach (var sourceInfo in info?.MediaSources ?? [])
                        {
                            var audioIndex = sourceInfo.DefaultAudioStreamIndex;
                            var videoIndex = sourceInfo.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video)?.Index;
                            LogNegotiation(logger, client, http.Request.Method, itemId, sourceInfo.Protocol.ToString(),
                                sourceInfo.SupportsDirectPlay, sourceInfo.SupportsDirectStream, sourceInfo.SupportsTranscoding,
                                sourceInfo.TranscodingUrl is not null, videoIndex, audioIndex, sourceInfo.DefaultSubtitleStreamIndex);
                        }
                    }

                    return;
                }
                else if (authenticated && config.EnablePlaybackDiagnostics && IsNativeVideoRequest(controller, action.ActionName)
                    && context.ActionArguments.TryGetValue("itemId", out var nativeItem) && nativeItem is Guid nativeItemId
                    && http.RequestServices.GetRequiredService<CaptionPlaybackFilter>().IsVisibleYouTubeVideo(nativeItemId, userId))
                {
                    var executed = await next().ConfigureAwait(false);
                    var client = ClientFamily(http.User.FindFirst("Jellyfin-Client")?.Value,
                        http.Request.Headers.UserAgent.ToString());
                    var status = (executed.Result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode
                        ?? http.Response.StatusCode;
                    LogNativeVideo(http.RequestServices.GetRequiredService<ILogger<CaptionPlaybackDispatch>>(), client,
                        http.Request.Method, action.ActionName, nativeItemId,
                        context.ActionArguments.TryGetValue("static", out var useStatic) && useStatic is true,
                        http.Request.Headers.ContainsKey("Range"), status);
                    return;
                }
                else if (controller == "Jellyfin.Api.Controllers.SubtitleController"
                    && action.ActionName is "GetSubtitle" or "GetSubtitleWithTicks"
                    && TryGetSubtitleRequest(context.ActionArguments, out var subtitleItem, out var source, out var index))
                {
                    var captions = http.RequestServices.GetRequiredService<CaptionPlaybackFilter>();
                    if (captions.IsManagedSubtitle(source, index)
                        && (!authenticated || !captions.CanReadSubtitle(subtitleItem, source, userId)))
                    {
                        context.Result = authenticated ? new NotFoundResult() : new UnauthorizedResult();
                        return;
                    }
                }
            }

            await next().ConfigureAwait(false);
        }

        internal static bool IsNativeVideoRequest(string? controller, string action) =>
            (controller == "Jellyfin.Api.Controllers.VideosController" && action is "GetVideoStream" or "GetVideoStreamByContainer")
            || (controller == "Jellyfin.Api.Controllers.DynamicHlsController" && action is "GetMasterHlsVideoPlaylist" or "GetVariantHlsVideoPlaylist");

        [LoggerMessage(EventId = 4, Level = LogLevel.Information,
            Message = "VOD media request client={Client} method={Method} action={Action} item={ItemId} static={Static} rangeRequested={RangeRequested} actionStatus={Status}")]
        private static partial void LogNativeVideo(ILogger logger, string client, string method, string action,
            Guid itemId, bool @static, bool rangeRequested, int status);

        internal static string ClientFamily(string? client, string userAgent) =>
            (client?.Contains("Infuse", StringComparison.OrdinalIgnoreCase) == true
                || userAgent.Contains("Infuse", StringComparison.OrdinalIgnoreCase)) ? "Infuse"
                : (client?.Contains("Jellyfin Web", StringComparison.OrdinalIgnoreCase) == true ? "Jellyfin Web" : "Other");

        [LoggerMessage(EventId = 3, Level = LogLevel.Information,
            Message = "VOD PlaybackInfo client={Client} method={Method} item={ItemId} protocol={Protocol} directPlay={DirectPlay} directStream={DirectStream} transcode={Transcode} hasTranscodingUrl={HasTranscodingUrl} video={Video} audio={Audio} subtitle={Subtitle}")]
        private static partial void LogNegotiation(ILogger logger, string client, string method, Guid itemId, string protocol,
            bool directPlay, bool directStream, bool transcode, bool hasTranscodingUrl, int? video, int? audio, int? subtitle);

        internal static bool TryGetSubtitleRequest(
            IDictionary<string, object?> arguments, out Guid itemId, out Guid mediaSourceId, out int index)
        {
            // Match the native endpoint's query-over-route precedence.
            itemId = Argument<Guid>(arguments, "itemId", "routeItemId");
            index = Argument<int>(arguments, "index", "routeIndex");
            return Guid.TryParse(Argument<string>(arguments, "mediaSourceId", "routeMediaSourceId"), out mediaSourceId);
        }

        private static T? Argument<T>(IDictionary<string, object?> arguments, string query, string route) =>
            arguments.TryGetValue(query, out var value) && value is T queryValue
                ? queryValue
                : arguments.TryGetValue(route, out value) && value is T routeValue ? routeValue : default;
    }
}
