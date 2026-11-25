using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    internal sealed class YouTubeMetadataQuota(TimeProvider clock, Func<string?> pathProvider)
    {
        private static readonly TimeZoneInfo _pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        private readonly object _sync = new();
        private QuotaState? _state;
        private string? _path;

        public sealed record QuotaState(DateOnly Day, int Used, bool UpstreamExhausted);

        internal QuotaState Read()
        {
            lock (_sync)
                return Current();
        }

        internal bool TrySpend(int budget, int reserve, bool manual)
        {
            lock (_sync)
            {
                var state = Current();
                if (state.UpstreamExhausted || state.Used >= budget - (manual ? 0 : reserve))
                    return false;

                return Save(state with { Used = state.Used + 1 });
            }
        }

        internal void Exhausted()
        {
            lock (_sync)
                Save(Current() with { UpstreamExhausted = true });
        }

        private QuotaState Current()
        {
            var path = pathProvider();
            if (_state is null || !string.Equals(_path, path, StringComparison.Ordinal))
            {
                _path = path;
                _state = null;
                try
                {
                    if (path is not null && File.Exists(path))
                        _state = JsonSerializer.Deserialize<QuotaState>(File.ReadAllText(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Fail closed for this quota day if an existing ledger cannot be read.
                    _state = new QuotaState(Today(), 0, true);
                }
            }

            var day = Today();
            if (_state?.Day != day)
                _state = new QuotaState(day, 0, false);
            return _state;
        }

        private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _pacific).DateTime);

        private bool Save(QuotaState state)
        {
            if (_path is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(state));
                    File.Move(_path + ".tmp", _path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _state = state with { UpstreamExhausted = true };
                    return false;
                }
            }

            _state = state;
            return true;
        }
    }
}
