using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Data;
using Jellyfin.Plugin.VOD.Services.Publication;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>Projects published YouTube items into the optional native channel library.</summary>
    public sealed partial class YouTubeChannelTask : IScheduledTask
    {
        private readonly Func<PluginConfiguration?> _configuration;
        private readonly IVodDatabase _database;
        private readonly IYtDlpService _ytDlp;
        private readonly ILibraryManager _library;
        private readonly LibraryPublisher _publisher;
        private readonly ILogger<YouTubeChannelTask> _logger;

        /// <summary>Initializes the channel discovery and publication task.</summary>
        /// <param name="configuration">Reads current plugin settings.</param>
        /// <param name="database">Provides retained canonical upload candidates.</param>
        /// <param name="ytDlp">Lists channels and playlists using the existing platform throttle.</param>
        /// <param name="library">Resolves existing Jellyfin playback items.</param>
        /// <param name="publisher">Updates the native channel folders and playlist links.</param>
        /// <param name="logger">Reports discovery and publication summaries.</param>
        public YouTubeChannelTask(Func<PluginConfiguration?> configuration, IVodDatabase database,
            IYtDlpService ytDlp, ILibraryManager library, LibraryPublisher publisher, ILogger<YouTubeChannelTask> logger)
        {
            _configuration = configuration;
            _database = database;
            _ytDlp = ytDlp;
            _library = library;
            _publisher = publisher;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "VOD YouTube channel library";

        /// <inheritdoc />
        public string Key => "VodYouTubeChannelLibrary";

        /// <inheritdoc />
        public string Description => "Refreshes the channel folders and native playlists for retained uploads.";

        /// <inheritdoc />
        public string Category => "VOD";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
            [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks }];

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = _configuration();
            if (config is null || string.IsNullOrWhiteSpace(config.YouTubeChannelLibraryName))
            {
                LogNoTarget();
                return;
            }

            if (string.Equals(config.YouTubeChannelLibraryName, config.SeriesLibraryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(config.YouTubeChannelLibraryName, config.MoviesLibraryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Use a separate mixed library for the YouTube channel view.");
            }

            var target = await _publisher.PrepareTargetAsync(config.YouTubeChannelLibraryName, cancellationToken).ConfigureAwait(false);
            if (!target.IsMixed)
            {
                throw new InvalidOperationException("The native YouTube channel library must use Jellyfin's mixed content type.");
            }

            var sources = config.Sources.Where(source => source.Enabled)
                .Select(source => (Source: source, Type: SourceTypeDetector.Detect(source.Url)))
                .Where(entry => entry.Type is SourceType.YoutubeChannel or SourceType.YoutubePlaylist)
                .OrderBy(entry => entry.Type == SourceType.YoutubePlaylist).ToArray();
            var excluded = config.YouTubeExcludedPlaylistIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            var channels = new List<ChannelSource>();
            var skipped = 0;
            var knownPlaylists = new Dictionary<string, IReadOnlyList<ChannelPlaylist>>(StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            foreach (var (source, type) in sources)
            {
                var published = _database.GetVideosBySource(source.Url);
                var isChannel = type == SourceType.YoutubeChannel;
                var configuredPlaylist = isChannel ? null : VodNativeId.ForSource(source.Url)?["youtube_pl_".Length..];
                if (!isChannel && (configuredPlaylist is null || excluded.Contains(configuredPlaylist)))
                {
                    skipped++;
                    continue;
                }

                var header = await ListAsync(source.Url, isChannel ? 1 : null, cancellationToken).ConfigureAwait(false);
                if (header?.ChannelId is not { } channelId || !IsChannelId(channelId)
                    || string.IsNullOrWhiteSpace(header.ChannelName))
                {
                    throw new InvalidOperationException("YouTube did not return a stable channel ID and name. Keeping the last channel view.");
                }

                if (!isChannel && header.PlaylistCount > header.Entries.Count)
                {
                    throw new InvalidOperationException("YouTube returned an incomplete configured playlist. Keeping the last channel view.");
                }

                if (!knownPlaylists.TryGetValue(channelId, out var playlists))
                {
                    playlists = await DiscoverAsync(channelId, excluded, cancellationToken).ConfigureAwait(false);
                    knownPlaylists[channelId] = playlists;
                }

                var uploads = published.Where(row => Publishability.IsPublishable(row, source.RetentionDays, now))
                    .Select(row => new ChannelUpload(row, FindPrimary(row.Id, source.Mode), source.RetentionDays)).ToArray();
                if (isChannel)
                {
                    channels.Add(new ChannelSource(channelId, header.ChannelName, header.ThumbnailUrl, header.BannerUrl, uploads, playlists));
                }
                else
                {
                    // An explicit playlist may be unlisted and absent from discovery. Only include
                    // its items in Videos when the flat listing resolves the uploader to this owner.
                    var owned = header.Entries.Where(entry => entry.ChannelId == channelId)
                        .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                    var channelUploads = uploads.Where(upload => upload.Video.NativeId is { } id && owned.Contains(id)).ToArray();
                    var configured = MakePlaylist(configuredPlaylist!, header, null);
                    channels.Add(new ChannelSource(channelId, header.ChannelName, null, null,
                        channelUploads, [configured, .. playlists], uploads));
                }
                progress.Report(80d * channels.Count / sources.Length);
            }

            var catalog = YouTubeLibraryCatalog.Build(channels, excluded, now);
            var report = await _publisher.PublishChannelCatalogAsync(target.Anchor, catalog, cancellationToken).ConfigureAwait(false);
            LogCatalogSummary(catalog.Count, knownPlaylists.Values.Sum(playlists => playlists.Count),
                catalog.Sum(channel => channel.Playlists.Sum(playlist => playlist.Items.Count)),
                report.Created, report.Updated, report.Unchanged, report.Removed, skipped);
            progress.Report(100);
        }

        [LoggerMessage(EventId = 1, Level = LogLevel.Information,
            Message = "YouTube channel task skipped because no mixed browse library is configured")]
        private partial void LogNoTarget();

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "YouTube channel task: channels={Channels} discoveredPlaylists={Playlists} playbackLinks={Links} created={Created} updated={Updated} unchanged={Unchanged} removed={Removed} skippedSources={Skipped}")]
        private partial void LogCatalogSummary(int channels, int playlists, int links, int created,
            int updated, int unchanged, int removed, int skipped);

        private async Task<IReadOnlyList<ChannelPlaylist>> DiscoverAsync(
            string channelId, HashSet<string> excluded, CancellationToken cancellationToken)
        {
            var listing = await ListAsync($"https://www.youtube.com/channel/{channelId}/playlists", null, cancellationToken)
                .ConfigureAwait(false);
            if (listing is null)
            {
                throw new InvalidOperationException("Could not list the YouTube channel's playlists. Keeping the last channel view.");
            }

            var playlists = new List<ChannelPlaylist>();
            foreach (var entry in listing.Entries.Where(entry => IsPlaylistId(entry.Id) && !excluded.Contains(entry.Id))
                         .DistinctBy(entry => entry.Id, StringComparer.Ordinal))
            {
                var url = $"https://www.youtube.com/playlist?list={entry.Id}";
                var playlist = await ListAsync(url, null, cancellationToken).ConfigureAwait(false);
                if (playlist is null || playlist.PlaylistCount > playlist.Entries.Count)
                {
                    throw new InvalidOperationException("YouTube returned an incomplete playlist. Keeping the last channel view.");
                }

                if (playlist.ChannelId is not null && !string.Equals(playlist.ChannelId, channelId, StringComparison.Ordinal))
                {
                    continue;
                }

                playlists.Add(MakePlaylist(entry.Id, playlist, entry.Title));
            }

            return playlists;
        }

        private static ChannelPlaylist MakePlaylist(string id, PlaylistInfo playlist, string? fallbackName) =>
            new(id, playlist.Title ?? fallbackName ?? id, playlist.ThumbnailUrl, playlist.Description,
                playlist.Entries.Select(video => video.Id).ToArray());

        private Task<PlaylistInfo?> ListAsync(string url, int? maximum, CancellationToken token) =>
            _ytDlp.GetPlaylistEntriesAsync(url, maximum, null, token);

        internal static bool IsChannelId(string value) =>
            value.StartsWith("UC", StringComparison.Ordinal) && value.Length == 24 && value.All(IsYouTubeIdCharacter);

        private static bool IsPlaylistId(string value) =>
            value.Length is >= 10 and <= 128 && value.All(IsYouTubeIdCharacter);

        private static bool IsYouTubeIdCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '_' or '-';

        private Guid FindPrimary(string videoId, SourceMode mode)
        {
            var id = _library.GetNewItemId(_publisher.ItemKeyFor(videoId),
                mode == SourceMode.Movies ? typeof(Movie) : typeof(Episode));
            return _library.GetItemById(id) is Video item && item.Id == id ? id : Guid.Empty;
        }
    }
}
