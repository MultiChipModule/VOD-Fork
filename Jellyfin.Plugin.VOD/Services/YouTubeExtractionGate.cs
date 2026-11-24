using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;

namespace Jellyfin.Plugin.VOD.Services
{
    internal sealed class YouTubeExtractionGate(TimeProvider clock, Func<PluginConfiguration?> configuration,
        Func<string?>? statePath = null)
    {
        private readonly object _sync = new();
        private readonly Queue<DateTimeOffset> _background = new();
        private DateTimeOffset _nextStart;
        private DateTimeOffset _restrictedUntil;
        private int _active;
        private int _waitingPlayback;
        private int _restrictions;
        private string? _restriction;
        private bool _readState;

        private sealed record RestrictionState(string Reason, DateTimeOffset Until, int Count);

        private string? Restore()
        {
            var path = statePath?.Invoke();
            if (_readState || path is null)
                return path;

            _readState = true;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<RestrictionState>(File.ReadAllText(path)) is { } state)
                {
                    _restrictedUntil = state.Until;
                    _restrictions = Math.Clamp(state.Count, 0, 12);
                    _restriction = state.Reason is "Bot challenge" or "Rate limited" ? state.Reason : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // The in-process gate still enforces restrictions if its state cannot be read.
            }

            return path;
        }

        internal sealed record Snapshot(int Active, int WaitingPlayback, int BackgroundLastHour,
            string? Restriction, DateTimeOffset? CooldownUntil, DateTimeOffset? NextRetry, DateTimeOffset? NextBackgroundRetry);

        internal Snapshot Status()
        {
            lock (_sync)
            {
                Restore();
                var now = clock.GetUtcNow();
                Prune(now);
                var next = _restrictedUntil > _nextStart ? _restrictedUntil : _nextStart;
                var limit = BackgroundLimit;
                var background = _background.Count >= limit
                    ? _background.ElementAt(_background.Count - limit).AddHours(1) : next;
                return new Snapshot(_active, _waitingPlayback, _background.Count, _restriction,
                    _restrictedUntil > now ? _restrictedUntil : null, next > now ? next : null,
                    background > now && background > next ? background : next > now ? next : null);
            }
        }

        private int BackgroundLimit => Math.Clamp(configuration()?.YouTubeBackgroundPerHour ?? 60, 1, 360);

        internal async Task<IDisposable?> EnterAsync(bool playback, CancellationToken token)
        {
            if (playback)
            {
                lock (_sync)
                {
                    _waitingPlayback++;
                }
            }

            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var (lease, denied) = TryEnter(playback);
                    if (lease is not null || denied)
                    {
                        return lease;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250), clock, token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (playback)
                {
                    lock (_sync)
                    {
                        _waitingPlayback--;
                    }
                }
            }
        }

        internal (IDisposable? Lease, bool Denied) TryEnter(bool playback)
        {
            lock (_sync)
            {
                Restore();
                var now = clock.GetUtcNow();
                Prune(now);
                var config = configuration();
                if (now < _restrictedUntil || (!playback && _background.Count >= BackgroundLimit))
                {
                    return (null, true);
                }

                if (now < _nextStart || _active >= Math.Clamp(config?.YouTubeConcurrentExtractions ?? 1, 1, 4)
                    || (!playback && _waitingPlayback > 0))
                {
                    return (null, false);
                }

                _active++;
                _nextStart = now.AddSeconds(Math.Clamp(config?.YouTubeExtractionIntervalSeconds ?? 10, 1, 300));
                if (!playback)
                {
                    _background.Enqueue(now);
                }

                return (new Lease(this), false);
            }
        }

        internal void Restricted(string reason)
        {
            lock (_sync)
            {
                var path = Restore();
                var now = clock.GetUtcNow();
                if (_restrictedUntil > now)
                {
                    return;
                }

                var initial = Math.Clamp(configuration()?.YouTubeRestrictionMinutes ?? 60, 1, 1440);
                _restrictedUntil = now.AddMinutes(Math.Min(1440, initial * Math.Pow(2, Math.Min(_restrictions, 11))));
                _restrictions = Math.Min(12, _restrictions + 1);
                _restriction = reason;
                if (path is null)
                    return;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path + ".tmp",
                        JsonSerializer.Serialize(new RestrictionState(reason, _restrictedUntil, _restrictions)));
                    File.Move(path + ".tmp", path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep enforcing the cooldown in memory even if the disk is unavailable.
                }
            }
        }

        internal static string? DetectRestriction(string stderr)
        {
            if (stderr.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("confirm you’re not a bot", StringComparison.OrdinalIgnoreCase))
            {
                return "Bot challenge";
            }

            return stderr.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) ? "Rate limited" : null;
        }

        private void Prune(DateTimeOffset now)
        {
            while (_background.TryPeek(out var start) && start.AddHours(1) <= now)
            {
                _background.Dequeue();
            }
        }

        private sealed class Lease(YouTubeExtractionGate owner) : IDisposable
        {
            private YouTubeExtractionGate? _owner = owner;

            public void Dispose()
            {
                var gate = Interlocked.Exchange(ref _owner, null);
                if (gate is not null)
                {
                    lock (gate._sync)
                    {
                        gate._active--;
                    }
                }
            }
        }
    }
}
