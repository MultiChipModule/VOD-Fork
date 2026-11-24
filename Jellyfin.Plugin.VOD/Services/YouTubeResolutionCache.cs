using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.VOD.Services
{
    internal sealed class YouTubeResolutionCache(TimeProvider clock) : IDisposable
    {
        private const int Capacity = 64;
        private readonly object _sync = new();
        private readonly Dictionary<(string Page, string Format), (StreamResolution Value, DateTimeOffset Expiry)> _cache = new();
        private readonly Dictionary<(string Page, string Format), Pending> _pending = new();
        private readonly SemaphoreSlim _slots = new(4, 4);

        internal async Task<StreamResolution?> ResolveAsync(string page, string format,
            Func<CancellationToken, Task<StreamResolution?>> resolve, CancellationToken token, Action<bool>? observeCache = null)
        {
            token.ThrowIfCancellationRequested();
            var key = (page, format);
            Pending pending;
            lock (_sync)
            {
                if (Find(key) is { } cached)
                {
                    observeCache?.Invoke(true);
                    return cached;
                }

                if (!_pending.TryGetValue(key, out pending!))
                {
                    pending = new Pending();
                    _pending.Add(key, pending);
                }

                pending.Users++;
            }

            try
            {
                await pending.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    lock (_sync)
                    {
                        if (Find(key) is { } cached)
                        {
                            observeCache?.Invoke(true);
                            return cached;
                        }
                    }

                    await _slots.WaitAsync(token).ConfigureAwait(false);
                    StreamResolution? resolution;
                    try
                    {
                        observeCache?.Invoke(false);
                        resolution = await resolve(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _slots.Release();
                    }

                    if (resolution is not null)
                    {
                        Remember(key, resolution);
                    }

                    return resolution;
                }
                finally
                {
                    pending.Gate.Release();
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (--pending.Users == 0)
                    {
                        _pending.Remove(key);
                        pending.Dispose();
                    }
                }
            }
        }

        internal void Invalidate(string page, string format, StreamResolution served)
        {
            lock (_sync)
            {
                var key = (page, format);
                if (_cache.TryGetValue(key, out var entry) && ReferenceEquals(entry.Value, served))
                {
                    _cache.Remove(key);
                }
            }
        }

        private StreamResolution? Find((string Page, string Format) key)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.Expiry > clock.GetUtcNow())
            {
                return entry.Value;
            }

            _cache.Remove(key);
            return null;
        }

        private void Remember((string Page, string Format) key, StreamResolution resolution)
        {
            var now = clock.GetUtcNow();
            var expiry = now.AddMinutes(5);
            if (Uri.TryCreate(resolution.Url, UriKind.Absolute, out var uri))
            {
                var query = QueryHelpers.ParseQuery(uri.Query);
                var segments = uri.AbsolutePath.Split('/');
                var expiryIndex = Array.IndexOf(segments, "expire");
                var text = query.TryGetValue("expire", out var queryExpiry) ? queryExpiry.ToString()
                    : expiryIndex >= 0 && expiryIndex + 1 < segments.Length ? segments[expiryIndex + 1] : null;
                if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
                    && unix is > 30 and < 253402300799)
                {
                    var signedExpiry = DateTimeOffset.FromUnixTimeSeconds(unix - 30);
                    expiry = signedExpiry < expiry ? signedExpiry : expiry;
                }
            }

            if (expiry <= now)
            {
                return;
            }

            lock (_sync)
            {
                if (_cache.Count >= Capacity)
                {
                    _cache.Remove(_cache.MinBy(entry => entry.Value.Expiry).Key);
                }

                _cache[key] = (resolution, expiry);
            }
        }

        public void Dispose() => _slots.Dispose();

        private sealed class Pending : IDisposable
        {
            internal readonly SemaphoreSlim Gate = new(1, 1);
            internal int Users;

            public void Dispose() => Gate.Dispose();
        }
    }
}
