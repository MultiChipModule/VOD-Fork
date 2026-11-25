using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Services.Sync.Dto;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    /// <summary>Retrieves public YouTube catalog data without resolving media.</summary>
    public sealed class YouTubeMetadataService : IDisposable
    {
        private const int FlatLimit = 100;
        private static readonly XNamespace _atom = "http://www.w3.org/2005/Atom";
        private static readonly XNamespace _youtube = "http://www.youtube.com/xml/schemas/2015";
        private static readonly XNamespace _media = "http://search.yahoo.com/mrss/";
        private static readonly HttpClient _publicHttp = new(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, CheckCertificateRevocationList = true
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };

        private readonly IYtDlpService _flat;
        private readonly Func<PluginConfiguration?> _configuration;
        private readonly HttpClient _http;
        private readonly TimeProvider _clock;
        private readonly YouTubeMetadataQuota _quota;
        private readonly YouTubeApiKeyStore _apiKeys;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1024 });
        private readonly SemaphoreSlim _requests = new(1);
        private readonly Func<string?> _stateFolder;
        private long _rssRequests;
        private long _flatListings;
        private long _oEmbedRequests;
        private DateTimeOffset _apiRetryAfter;
        private DateTimeOffset _rssRetryAfter;
        private string? _apiIssue;

        /// <summary>Creates a metadata provider with the shared flat listing fallback.</summary>
        public YouTubeMetadataService(IYtDlpService flat, Func<PluginConfiguration?> configuration)
            : this(flat, configuration, _publicHttp, TimeProvider.System, () => Plugin.Instance?.DataFolderPath)
        {
        }

        internal YouTubeMetadataService(IYtDlpService flat, Func<PluginConfiguration?> configuration, HttpClient http,
            TimeProvider clock, Func<string?>? stateFolder = null)
        {
            _flat = flat;
            _configuration = configuration;
            _http = http;
            _clock = clock;
            _stateFolder = stateFolder ?? (() => null);
            _apiKeys = new YouTubeApiKeyStore(_stateFolder);
            _quota = new YouTubeMetadataQuota(clock, () => _stateFolder() is { } path ? Path.Combine(path, "youtube-api-quota.json") : null);
        }

        internal YouTubeMetadataStatus Status()
        {
            var config = _configuration();
            var state = _quota.Read();
            var budget = Budget(config);
            var reserve = Reserve(config);
            _apiKeys.Read(config?.YouTubeApiKeyFile, out var status);
            return new(status, state.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), state.Used, budget, reserve,
                Math.Max(0, budget - state.Used - reserve), state.UpstreamExhausted, Volatile.Read(ref _apiIssue),
                Interlocked.Read(ref _rssRequests), Interlocked.Read(ref _flatListings), Interlocked.Read(ref _oEmbedRequests));
        }

        internal bool WriteApiKey(string? key)
        {
            if (!_apiKeys.TryWrite(key))
                return false;

            Volatile.Write(ref _apiIssue, null);
            return true;
        }

        private static int Budget(PluginConfiguration? config) => Math.Clamp(config?.YouTubeApiDailyBudget ?? 10000, 1, 1000000);

        private static int Reserve(PluginConfiguration? config) => Math.Clamp(config?.YouTubeApiImportReserve ?? 1000, 0, Budget(config) - 1);

        internal async Task PrimeHeadersAsync(IEnumerable<string> sources, CancellationToken token)
        {
            var config = _configuration();
            if (_apiKeys.Read(config?.YouTubeApiKeyFile, out _) is not { } key)
                return;

            await _requests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var channelIds = new HashSet<string>(StringComparer.Ordinal);
                var playlistIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var url in sources.Where(YouTubeCaption.IsYouTubePage))
                {
                    var uri = new Uri(url);
                    var paths = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (SourceTypeDetector.Detect(url) == SourceType.YoutubePlaylist
                        && QueryHelpers.ParseQuery(uri.Query).TryGetValue("list", out var list)
                        && IsId(list.ToString(), 10, 128) && GetFresh<ApiPayload>("playlist:" + list) is null)
                    {
                        playlistIds.Add(list.ToString());
                    }
                    else if (SourceTypeDetector.Detect(url) == SourceType.YoutubeChannel
                        && paths.LastOrDefault() is not ("shorts" or "streams" or "live")
                        && YtDlpService.TryGetYouTubeVideoId(url) is null
                        && ChannelId(paths) is { } channel && GetFresh<ApiPayload>("channel-id:" + channel) is null)
                    {
                        channelIds.Add(channel);
                    }
                }

                var api = new ApiRequest(key, config, false);
                await PrimeAsync(api, "channels", "snippet,contentDetails,brandingSettings", "channel-id:", channelIds, token)
                    .ConfigureAwait(false);
                await PrimeAsync(api, "playlists", "snippet", "playlist:", playlistIds, token).ConfigureAwait(false);
            }
            finally
            {
                _requests.Release();
            }
        }

        private async Task PrimeAsync(ApiRequest api, string endpoint, string parts, string prefix, HashSet<string> ids,
            CancellationToken token)
        {
            // A singleton can share the ordinary lookup. Batches amortize larger sets while
            // still leaving the manual quota reserve untouched.
            if (ids.Count < 2)
                return;
            foreach (var batch in ids.Order(StringComparer.Ordinal).Chunk(50))
            {
                var payload = await ApiAsync(api, endpoint, token, ("part", parts), ("id", string.Join(',', batch))).ConfigureAwait(false);
                if (payload is null)
                    return;
                foreach (var item in Items(payload.Value))
                    if (Text(item, "id") is { } id && ids.Contains(id))
                        Put(prefix + id, new ApiPayload(item), PlaylistLifetime(api.Config));
            }
        }

        internal async Task<PlaylistInfo?> ListAsync(string url, int? maximum, bool incremental, bool manual,
            CancellationToken token, string? language = null, bool headerOnly = false, bool membershipOnly = false)
        {
            if (!YouTubeCaption.IsYouTubePage(url))
                return null;

            await _requests.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var config = _configuration();
                var type = SourceTypeDetector.Detect(url);
                var uri = new Uri(url);
                var query = QueryHelpers.ParseQuery(uri.Query);
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var channelPath = segments.FirstOrDefault();
                var customTab = segments.LastOrDefault() is "shorts" or "streams" or "live";
                var browsePlaylists = segments.LastOrDefault() == "playlists" && type == SourceType.YoutubeChannel;
                var key = _apiKeys.Read(config?.YouTubeApiKeyFile, out _);
                var api = key is null ? null : new ApiRequest(key, config, manual);

                var alias = ChannelAlias(segments);
                var channelId = ChannelId(segments);
                if (incremental && type == SourceType.YoutubeChannel && !customTab && channelId is not null)
                    return await ReadRssAsync(channelId, config, token).ConfigureAwait(false);

                if (api is not null && !customTab)
                {
                    if (type == SourceType.YoutubePlaylist && query.TryGetValue("list", out var id) && IsId(id.ToString(), 10, 128))
                        return await ReadPlaylistAsync(api, id.ToString(), maximum, membershipOnly, token).ConfigureAwait(false);

                    if (!browsePlaylists && type != SourceType.YoutubePlaylist && YtDlpService.TryGetYouTubeVideoId(url) is { } videoId)
                        return await ReadVideoAsync(api, videoId, token).ConfigureAwait(false);

                    if (type == SourceType.YoutubeChannel)
                    {
                        var (field, value) = channelId is not null ? ("id", channelId)
                            : channelPath?.StartsWith('@') == true ? ("forHandle", Uri.UnescapeDataString(channelPath[1..]))
                            : channelPath == "user" && segments.Length >= 2 ? ("forUsername", Uri.UnescapeDataString(segments[1]))
                            : (null, null);

                        if (field is null)
                        {
                            var flatHeader = await ReadFlatAsync(FlatUrl(url), 1, language, config, token).ConfigureAwait(false);
                            channelId = flatHeader?.ChannelId;
                            if (channelId is null || !YouTubeChannelTask.IsChannelId(channelId))
                                return null;
                            (field, value) = ("id", channelId);
                        }

                        var header = channelId is null ? null : GetFresh<ApiPayload>("channel-id:" + channelId);
                        if (header is null)
                        {
                            var channel = await ApiAsync(api, "channels", token,
                                ("part", "snippet,contentDetails,brandingSettings"), (field, value!)).ConfigureAwait(false);
                            if (channel is not null && Items(channel.Value).FirstOrDefault() is { ValueKind: JsonValueKind.Object } item)
                                header = new ApiPayload(item);
                        }
                        if (header is null || Text(header.Value, "id") is not { } stableId
                            || !YouTubeChannelTask.IsChannelId(stableId))
                            return await RecoverUploadsAsync(channelId, config, manual || headerOnly || browsePlaylists, token).ConfigureAwait(false);
                        var entry = header.Value;
                        if (GetFresh<ApiPayload>("channel-id:" + stableId) is null)
                            Put("channel-id:" + stableId, header, PlaylistLifetime(config));
                        SaveIdentity(alias, stableId);
                        if (incremental)
                            return await ReadRssAsync(stableId, config, token).ConfigureAwait(false);

                        if (browsePlaylists)
                            return await DiscoverPlaylistsAsync(api, stableId, token).ConfigureAwait(false);

                        var uploads = Text(Property(Property(entry, "contentDetails"), "relatedPlaylists"), "uploads");
                        if (headerOnly)
                            return ChannelInfo(entry, stableId, [], false);
                        if (uploads is null)
                            return null;
                        var membership = await ReadMembershipAsync(api, uploads, maximum, token).ConfigureAwait(false);
                        return membership is { } result ? ChannelInfo(entry, stableId, result.Entries, result.Partial)
                            : await RecoverUploadsAsync(stableId, config, manual, token).ConfigureAwait(false);
                    }
                }

                // Public feeds and metadata endpoints are independent of the playback login.
                // A failed configured API request returns earlier and cannot trigger fan-out.
                var fallback = await ReadFlatAsync(FlatUrl(url), maximum, language, config, token).ConfigureAwait(false);
                if (fallback?.ChannelId is { } discovered && YouTubeChannelTask.IsChannelId(discovered))
                    SaveIdentity(alias, discovered);
                return fallback ?? (type == SourceType.YoutubeChannel && !customTab
                    ? await RecoverUploadsAsync(channelId, config, manual || headerOnly || browsePlaylists, token).ConfigureAwait(false)
                    : null);
            }
            finally
            {
                _requests.Release();
            }
        }

        private Task<PlaylistInfo?> RecoverUploadsAsync(string? channelId, PluginConfiguration? config, bool skip, CancellationToken token)
            => skip || channelId is null ? Task.FromResult<PlaylistInfo?>(null) : ReadRssAsync(channelId, config, token);

        private static string? FlatUrl(string url)
        {
            var uri = new Uri(url);
            var kind = SourceTypeDetector.Detect(url);
            if (kind == SourceType.YoutubePlaylist)
            {
                return QueryHelpers.ParseQuery(uri.Query).TryGetValue("list", out var list) && IsId(list.ToString(), 10, 128)
                    ? "https://www.youtube.com/playlist?list=" + list : null;
            }
            if (YtDlpService.TryGetYouTubeVideoId(url) is { } id)
                return WatchUrl(id);

            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 0)
                return null;
            var identityLength = path[0] is "channel" or "c" or "user" ? 2 : 1;
            if (path.Length < identityLength || path.Length > identityLength + 1
                || (identityLength == 1 && !path[0].StartsWith('@'))
                || (path[0] == "channel" && !YouTubeChannelTask.IsChannelId(path[1])))
                return null;
            var tab = path.Length == identityLength ? "videos" : path[^1];
            if (tab is not ("videos" or "streams" or "live" or "shorts" or "playlists" or "featured"))
                return null;
            return "https://www.youtube.com/" + string.Join('/', path.Take(identityLength))
                + "/" + (tab == "live" ? "streams" : tab == "featured" ? "videos" : tab);
        }

        private async Task<PlaylistInfo?> ReadFlatAsync(string? url, int? maximum, string? language,
            PluginConfiguration? config, CancellationToken token)
        {
            if (url is null)
                return null;

            var limit = Math.Clamp(maximum ?? FlatLimit, 1, FlatLimit);
            var cacheKey = (url, limit, language);
            if (GetFresh<PlaylistInfo>(cacheKey) is { } cached)
                return cached;

            var direct = YtDlpService.TryGetYouTubeVideoId(url) is not null && SourceTypeDetector.Detect(url) != SourceType.YoutubePlaylist;
            if (direct)
                Interlocked.Increment(ref _oEmbedRequests);
            else
                Interlocked.Increment(ref _flatListings);
            var listing = await _flat.GetPlaylistEntriesAsync(url, limit, null, token, metadataLanguage: language).ConfigureAwait(false);
            if (listing is null)
                return null;

            var partial = !direct && (listing.Entries.Count >= limit || listing.PlaylistCount > listing.Entries.Count);
            var result = listing with { IsPartial = partial };
            Put(cacheKey, result, SourceTypeDetector.Detect(url) == SourceType.YoutubePlaylist
                || new Uri(url).AbsolutePath.TrimEnd('/').EndsWith("/playlists", StringComparison.Ordinal)
                    ? PlaylistLifetime(config) : TimeSpan.FromMinutes(15));
            return result;
        }

        private async Task<PlaylistInfo?> ReadRssAsync(string channelId, PluginConfiguration? config, CancellationToken token)
        {
            var key = "rss:" + channelId;
            if (GetFresh<RssFeed>(key) is { } fresh)
                return fresh.Playlist;
            if (_rssRetryAfter > _clock.GetUtcNow())
                return null;
            _cache.TryGetValue(key, out Cached<RssFeed>? older);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}");
            if (EntityTagHeaderValue.TryParse(older?.Value.ETag, out var etag))
                request.Headers.IfNoneMatch.Add(etag);
            try
            {
                Interlocked.Increment(ref _rssRequests);
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    _rssRetryAfter = _clock.GetUtcNow().AddMinutes(30);
                if (response.StatusCode != HttpStatusCode.NotModified && !response.IsSuccessStatusCode)
                    return null;

                var parsed = response.StatusCode == HttpStatusCode.NotModified ? older?.Value.Playlist
                    : ParseFeed(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), channelId);
                if (parsed is null)
                    return null;
                Put(key, new RssFeed(parsed, response.Headers.ETag?.ToString() ?? older?.Value.ETag),
                    TimeSpan.FromMinutes(Math.Clamp(config?.YouTubeRssIntervalMinutes ?? 30, 15, 1440)), TimeSpan.FromDays(2));
                return parsed;
            }
            catch (Exception ex) when (ex is HttpRequestException or XmlException
                || (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                return null;
            }
        }

        internal static PlaylistInfo? ParseFeed(string xml, string channelId)
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
            });
            var feed = XDocument.Load(reader).Root;
            if (feed?.Name != _atom + "feed" || feed.Element(_youtube + "channelId")?.Value != channelId)
                return null;
            var entries = feed.Elements(_atom + "entry").Select(item =>
            {
                var id = item.Element(_youtube + "videoId")?.Value;
                var title = item.Element(_atom + "title")?.Value;
                var group = item.Element(_media + "group");
                return IsId(id, 11, 11) && !string.IsNullOrWhiteSpace(title)
                    ? new PlaylistEntry
                    {
                        Id = id!, VideoUrl = WatchUrl(id!), ChannelId = channelId, Title = title, IeKey = "Youtube",
                        Description = group?.Element(_media + "description")?.Value,
                        ThumbnailUrl = Image(group?.Element(_media + "thumbnail")?.Attribute("url")?.Value),
                        PublishedAt = ParseDate(item.Element(_atom + "published")?.Value)
                    } : null;
            }).OfType<PlaylistEntry>().ToArray();
            return new PlaylistInfo
            {
                ChannelId = channelId, ChannelName = feed.Element(_atom + "title")?.Value,
                Entries = entries, IsPartial = true, IsIncremental = true
            };
        }

        private async Task<PlaylistInfo?> ReadVideoAsync(ApiRequest api, string id, CancellationToken token)
        {
            var details = await VideoDetailsAsync(api, [id], token).ConfigureAwait(false);
            return details?.GetValueOrDefault(id) is { } entry ? new PlaylistInfo
            {
                ChannelId = entry.ChannelId, Title = entry.Title, Description = entry.Description,
                ThumbnailUrl = entry.ThumbnailUrl, Entries = [entry]
            } : null;
        }

        private async Task<PlaylistInfo?> ReadPlaylistAsync(ApiRequest api, string id, int? maximum,
            bool membershipOnly, CancellationToken token)
        {
            var header = GetFresh<ApiPayload>("playlist:" + id);
            if (header is null)
            {
                var details = await ApiAsync(api, "playlists", token, ("part", "snippet"), ("id", id)).ConfigureAwait(false);
                if (details is null || Items(details.Value).FirstOrDefault() is not { ValueKind: JsonValueKind.Object } playlist)
                    return null;
                header = new ApiPayload(playlist);
                Put("playlist:" + id, header, PlaylistLifetime(api.Config));
            }
            var membership = await ReadMembershipAsync(api, id, maximum, token, membershipOnly).ConfigureAwait(false);
            if (membership is not { } result)
                return null;
            var snippet = Property(header.Value, "snippet");
            return new PlaylistInfo
            {
                Title = Text(snippet, "title"), Description = Text(snippet, "description"), ThumbnailUrl = Thumbnail(snippet),
                ChannelId = Text(snippet, "channelId"), ChannelName = Text(snippet, "channelTitle"),
                Entries = result.Entries, IsPartial = result.Partial,
                PlaylistCount = result.Partial ? null : result.Entries.Count
            };
        }

        private async Task<PlaylistInfo?> DiscoverPlaylistsAsync(ApiRequest api, string channel, CancellationToken token)
        {
            var pages = await PagesAsync(api, "playlists", "snippet", "channelId", channel, null, token).ConfigureAwait(false);
            if (pages is not { } listing)
                return null;
            foreach (var item in listing.Items)
                if (Text(item, "id") is { } id)
                    Put("playlist:" + id, new ApiPayload(item), PlaylistLifetime(api.Config));
            return new PlaylistInfo
            {
                ChannelId = channel, IsPartial = listing.Partial,
                Entries = listing.Items.Select(item => (Id: Text(item, "id"), Snippet: Property(item, "snippet")))
                    .Where(item => IsId(item.Id, 10, 128))
                    .Select(item => new PlaylistEntry
                    {
                        Id = item.Id!, Title = Text(item.Snippet, "title"), Description = Text(item.Snippet, "description"),
                        VideoUrl = $"https://www.youtube.com/playlist?list={item.Id}", ThumbnailUrl = Thumbnail(item.Snippet)
                    }).ToArray()
            };
        }

        private async Task<(IReadOnlyList<PlaylistEntry> Entries, bool Partial)?> ReadMembershipAsync(
            ApiRequest api, string playlist, int? maximum, CancellationToken token, bool membershipOnly = false)
        {
            var pages = await PagesAsync(api, "playlistItems", "snippet,contentDetails", "playlistId", playlist, maximum, token)
                .ConfigureAwait(false);
            if (pages is not { } listing)
                return null;

            var byId = new Dictionary<string, PlaylistEntry>(StringComparer.Ordinal);
            var invalidEntry = false;
            foreach (var item in listing.Items)
            {
                var snippet = Property(item, "snippet");
                var details = Property(item, "contentDetails");
                var id = Text(details, "videoId") ?? Text(Property(snippet, "resourceId"), "videoId");
                if (!IsId(id, 11, 11))
                {
                    invalidEntry = true;
                    continue;
                }
                byId.TryAdd(id!, Entry(id!, snippet, ParseDate(Text(details, "videoPublishedAt")), fromPlaylist: true));
            }

            if (membershipOnly)
                return (byId.Values.ToArray(), listing.Partial || invalidEntry);

            var enriched = await VideoDetailsAsync(api, byId.Keys, token).ConfigureAwait(false);
            // A failed detail batch is an outage, even when playlist pages were successful.
            if (enriched is null)
                return null;
            return (byId.Select(item => enriched.GetValueOrDefault(item.Key) ?? item.Value).ToArray(),
                listing.Partial || invalidEntry);
        }

        private async Task<Dictionary<string, PlaylistEntry>?> VideoDetailsAsync(ApiRequest api, IEnumerable<string> ids, CancellationToken token)
        {
            var result = new Dictionary<string, PlaylistEntry>(StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (var id in ids)
            {
                if (GetFresh<PlaylistEntry>("video:" + id) is { } cached)
                    result[id] = cached;
                else
                    missing.Add(id);
            }
            foreach (var batch in missing.Order(StringComparer.Ordinal).Chunk(50))
            {
                var payload = await ApiAsync(api, "videos", token, ("part", "snippet,contentDetails"),
                    ("id", string.Join(',', batch))).ConfigureAwait(false);
                if (payload is null)
                    return null;
                foreach (var item in Items(payload.Value))
                {
                    var id = Text(item, "id");
                    if (!IsId(id, 11, 11))
                        continue;
                    var snippet = Property(item, "snippet");
                    var entry = Entry(id!, snippet, ParseDate(Text(snippet, "publishedAt")),
                        ReadDuration(Text(Property(item, "contentDetails"), "duration")));
                    result[id!] = entry;
                    Put("video:" + id, entry, PlaylistLifetime(api.Config));
                }
            }
            return result;
        }

        private async Task<(List<JsonElement> Items, bool Partial)?> PagesAsync(ApiRequest api, string endpoint,
            string parts, string field, string value, int? maximum, CancellationToken token)
        {
            var rows = new List<JsonElement>();
            string? pageToken = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int? total = null;
            do
            {
                var payload = await ApiAsync(api, endpoint, token, ("part", parts), (field, value),
                    ("maxResults", "50"), ("pageToken", pageToken ?? string.Empty)).ConfigureAwait(false);
                if (payload is null)
                    return null;
                var page = Items(payload.Value);
                var available = Property(Property(payload.Value, "pageInfo"), "totalResults");
                if (available.ValueKind == JsonValueKind.Number && available.TryGetInt32(out var count))
                    total ??= count;
                rows.AddRange(page);
                pageToken = Text(payload.Value, "nextPageToken");
                if (maximum is > 0 && rows.Count >= maximum)
                    return (rows.Take(maximum.Value).ToList(), pageToken is not null || rows.Count > maximum);
                if (pageToken is not null && !seen.Add(pageToken))
                    return null;
            }
            while (pageToken is not null);
            // A count mismatch can be caused by changing pages or filtered members. A partial
            // snapshot must never authorize deleting stored rows.
            return (rows, total > rows.Count);
        }

        private static string ApiFields(string endpoint) => endpoint switch
        {
            "channels" => "items(id,snippet(title,description,thumbnails),contentDetails(relatedPlaylists(uploads)),brandingSettings(image(bannerExternalUrl)))",
            "videos" => "items(id,snippet(title,description,channelId,publishedAt,liveBroadcastContent,thumbnails),contentDetails(duration))",
            "playlists" => "nextPageToken,pageInfo(totalResults),items(id,snippet(title,description,channelId,channelTitle,thumbnails))",
            _ => "nextPageToken,pageInfo(totalResults),items(snippet(title,description,thumbnails,videoOwnerChannelId,resourceId(videoId)),contentDetails(videoId,videoPublishedAt))"
        };

        private async Task<JsonElement?> ApiAsync(ApiRequest api, string endpoint, CancellationToken token,
            params (string Key, string Value)[] parameters)
        {
            var query = parameters.Where(item => item.Value.Length > 0).ToDictionary(item => item.Key, item => (string?)item.Value);
            query.Add("fields", ApiFields(endpoint));
            var requestUri = QueryHelpers.AddQueryString($"https://www.googleapis.com/youtube/v3/{endpoint}", query);
            if (GetFresh<ApiPayload>(requestUri) is { } cached)
                return cached.Value;

            if (_apiRetryAfter > _clock.GetUtcNow())
                return null;
            if (!_quota.TrySpend(Budget(api.Config), Reserve(api.Config), api.Manual))
            {
                Volatile.Write(ref _apiIssue, "Daily API quota unavailable. Scheduled sync keeps published items and leaves reserved quota for Import now.");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("X-Goog-Api-Key", api.Key);
            try
            {
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                if (!response.IsSuccessStatusCode)
                {
                    var errors = Property(Property(json.RootElement, "error"), "errors");
                    if (errors.ValueKind == JsonValueKind.Array && errors.EnumerateArray().Any(error =>
                        Text(error, "reason") is "quotaExceeded" or "dailyLimitExceeded"))
                        _quota.Exhausted();
                    else
                        _apiRetryAfter = _clock.GetUtcNow().AddMinutes(15);
                    Volatile.Write(ref _apiIssue, "YouTube Data API rejected the request. Check project quota, key restrictions and API enablement.");
                    return null;
                }
                if (Property(json.RootElement, "items").ValueKind != JsonValueKind.Array)
                    return null;
                var payload = json.RootElement.Clone();
                Put(requestUri, new ApiPayload(payload), PlaylistLifetime(api.Config));
                Volatile.Write(ref _apiIssue, null);
                return payload;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                || (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                _apiRetryAfter = _clock.GetUtcNow().AddMinutes(5);
                Volatile.Write(ref _apiIssue, "YouTube Data API is temporarily unavailable. Published items have been kept.");
                return null;
            }
        }

        private string? ChannelId(string[] path) =>
            path.Length >= 2 && path[0] == "channel" && YouTubeChannelTask.IsChannelId(path[1])
                ? path[1] : ReadIdentity(ChannelAlias(path));

        private static string? ChannelAlias(string[] path)
        {
            if (path.Length == 0)
                return null;
            if (path[0].StartsWith('@'))
                return path[0].ToLowerInvariant();
            return path.Length >= 2 && path[0] is "user" or "c" ? path[0] + "/" + path[1].ToLowerInvariant() : null;
        }

        private string? IdentityPath(string? alias) => alias is not null && _stateFolder() is { } folder
            ? Path.Combine(folder, "youtube-channels", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(alias))) + ".txt") : null;

        private string? ReadIdentity(string? alias)
        {
            if (alias is null)
                return null;
            if (GetFresh<string>("channel:" + alias) is { } cached)
                return cached;
            try
            {
                if (IdentityPath(alias) is { } path && File.Exists(path) && File.ReadAllText(path).Trim() is var id
                    && YouTubeChannelTask.IsChannelId(id))
                {
                    Put("channel:" + alias, id, TimeSpan.FromDays(7));
                    return id;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable identity cache is equivalent to a cache miss.
            }
            return null;
        }

        private void SaveIdentity(string? alias, string id)
        {
            if (alias is null || ReadIdentity(alias) == id)
                return;
            Put("channel:" + alias, id, TimeSpan.FromDays(7));
            try
            {
                if (IdentityPath(alias) is { } path)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path + ".tmp", id);
                    File.Move(path + ".tmp", path, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The running instance can still use its in-memory identity.
            }
        }

        internal static string? ReadKey(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                return null;
            try
            {
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
                    return null;
                using var input = File.OpenRead(path);
                if (input.Length is < 20 or > 256)
                    return null;
                using var reader = new StreamReader(input);
                var key = reader.ReadToEnd().Trim();
                return YouTubeApiKeyStore.IsValid(key) ? key : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private T? GetFresh<T>(object key) where T : class
            => _cache.TryGetValue(key, out Cached<T>? cached) && cached is not null && cached.Expires > _clock.GetUtcNow()
                ? cached.Value : null;

        private void Put<T>(object key, T value, TimeSpan freshness, TimeSpan? keep = null) where T : class
            => _cache.Set(key, new Cached<T>(value, _clock.GetUtcNow() + freshness),
                new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = keep ?? freshness });

        private static TimeSpan PlaylistLifetime(PluginConfiguration? config) =>
            TimeSpan.FromHours(Math.Clamp(config?.YouTubePlaylistRefreshHours ?? 6, 1, 168));

        private static PlaylistInfo ChannelInfo(JsonElement channel, string id, IReadOnlyList<PlaylistEntry> entries, bool partial)
        {
            var snippet = Property(channel, "snippet");
            return new PlaylistInfo
            {
                ChannelId = id, ChannelName = Text(snippet, "title"), Description = Text(snippet, "description"),
                ThumbnailUrl = Thumbnail(snippet), BannerUrl = Image(Text(Property(Property(channel, "brandingSettings"), "image"), "bannerExternalUrl")),
                Entries = entries, IsPartial = partial
            };
        }

        private static PlaylistEntry Entry(string id, JsonElement snippet, DateTimeOffset? published, double? duration = null, bool fromPlaylist = false)
            => new()
            {
                Id = id, VideoUrl = WatchUrl(id), IeKey = "Youtube", Title = Text(snippet, "title"),
                Description = Text(snippet, "description"), ThumbnailUrl = Thumbnail(snippet), PublishedAt = published, Duration = duration,
                ChannelId = Text(snippet, fromPlaylist ? "videoOwnerChannelId" : "channelId"),
                LiveStatus = Text(snippet, "liveBroadcastContent") switch
                {
                    "live" => LiveStatus.Live, "upcoming" => LiveStatus.Upcoming, _ => LiveStatus.NotLive
                }
            };

        private static string WatchUrl(string id) => "https://www.youtube.com/watch?v=" + id;

        private static JsonElement Property(JsonElement value, string property)
            => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) ? item : default;

        private static JsonElement.ArrayEnumerator Items(JsonElement value) => value.GetProperty("items").EnumerateArray();

        private static string? Text(JsonElement value, string property) => YouTubeCaption.ReadString(value, property);

        private static string? Thumbnail(JsonElement snippet)
        {
            var thumbnails = Property(snippet, "thumbnails");
            foreach (var size in new[] { "maxres", "standard", "high", "medium", "default" })
                if (Image(Text(Property(thumbnails, size), "url")) is { } candidate)
                    return candidate;
            return null;
        }

        private static string? Image(string? input) => Uri.TryCreate(input, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.IsDefaultPort
            && (uri.IdnHost.EndsWith(".ytimg.com", StringComparison.Ordinal)
                || uri.IdnHost.EndsWith(".googleusercontent.com", StringComparison.Ordinal)) ? input : null;

        private static DateTimeOffset? ParseDate(string? value)
            => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date.ToUniversalTime() : null;

        private static double? ReadDuration(string? value)
        {
            try { return value is null ? null : XmlConvert.ToTimeSpan(value).TotalSeconds; }
            catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
        }

        private static bool IsId(string? value, int min, int max) => value is not null && value.Length >= min && value.Length <= max
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

        /// <inheritdoc />
        public void Dispose()
        {
            _cache.Dispose();
            _requests.Dispose();
        }

        private sealed record Cached<T>(T Value, DateTimeOffset Expires);

        private sealed record ApiPayload(JsonElement Value);

        private sealed record ApiRequest(string Key, PluginConfiguration? Config, bool Manual);

        private sealed record RssFeed(PlaylistInfo Playlist, string? ETag);
    }

    /// <summary>Credential-free YouTube metadata statistics.</summary>
    public sealed record YouTubeMetadataStatus(string ApiKeyStatus, string PacificQuotaDay, int ApiUnitsUsed, int ApiBudget,
        int ImportReserve, int BackgroundUnitsAvailable, bool UpstreamQuotaExhausted, string? ApiIssue,
        long RssRequests, long FlatListings, long OEmbedRequests);
}
