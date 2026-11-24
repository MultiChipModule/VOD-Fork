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
        ILogger<CaptionPlaybackFilter> logger) : IDisposable
    {
        private readonly PlaylistCache _missingTracks = new();
        private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();

        internal async Task TryPrepareAsync(Guid itemId, Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            try
            {
                await PrepareAsync(itemId, userId, config, cancellationToken).ConfigureAwait(false);
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
        }

        private async Task PrepareAsync(Guid itemId, Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var user = users.GetUserById(userId);
            if (user is null || library.GetItemById<BaseItem>(itemId, user) is not Video item
                || item.GetProviderId("VOD") is not { Length: > 0 } vodId
                || database.GetVideoByUuid(vodId) is not { } row
                || !YouTubeCaption.IsYouTubePage(row.VideoUrl)
                || row.LiveStatus != Sync.LiveStatus.NotLive)
            {
                return;
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
        }

        private async Task PrepareVersionsAsync(
            Video item, string vodId, string videoKey, string url, Jellyfin.Database.Implementations.Entities.User user,
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (!ReadStreams(item.Id).Any(stream => stream.Type == MediaStreamType.Video))
            {
                // Let the server probe first. Otherwise its probe could replace the external track
                // before PlaybackInfo is populated.
                await mediaSources.GetPlaybackMediaSources(item, user, true, false, cancellationToken).ConfigureAwait(false);
            }

            var descriptions = item.GetAllVersions()
                .Where(version => string.Equals(version.GetProviderId("VOD"), vodId, StringComparison.Ordinal))
                .Select(version => (version.Id, Streams: ReadStreams(version.Id))).ToArray();
            if (!descriptions.Any(description => description.Streams.Count > 0))
            {
                return;
            }

            using var downloadBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var captions = await FindCaptionsAsync(videoKey, url, config, downloadBudget, cancellationToken)
                .ConfigureAwait(false);
            foreach (var description in descriptions.Where(description => description.Streams.Count > 0))
            {
                var updated = UpdateStreams(description.Streams, captions, cache);
                if (updated is not null)
                {
                    streams.SaveMediaStreams(description.Id, updated, cancellationToken);
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
            var creator = captions.FirstOrDefault(track => track.Kind == CaptionKind.Creator);
            var hasLocalEnglish = untouched.Any(stream => stream.IsExternal && IsEnglishSubtitle(stream));
            if (creator is not null && !hasLocalEnglish)
            {
                // The selected HLS master can advertise the same YouTube English track. Once
                // a normalized copy is available, offer that copy in PlaybackInfo instead.
                untouched.RemoveAll(stream => !stream.IsExternal && IsEnglishSubtitle(stream));
            }

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
    internal sealed class CaptionPlaybackDispatch : IAsyncActionFilter
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
                    await http.RequestServices.GetRequiredService<CaptionPlaybackFilter>()
                        .TryPrepareAsync(itemId, userId, config, http.RequestAborted).ConfigureAwait(false);
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
