using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    internal sealed class YouTubeApiKeyStore(Func<string?> dataFolder)
    {
        private const string Cleared = "cleared";
        private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private readonly object _sync = new();
        private IDataProtector? _protector;

        internal string? Read(string? legacyPath, out string status)
        {
            lock (_sync)
            {
                if (dataFolder() is { } folder)
                {
                    var directory = Path.Combine(folder, "youtube-api-secret");
                    try
                    {
                        var stored = File.ReadAllText(Path.Combine(directory, "api-key"));
                        if (stored == Cleared)
                        {
                            status = "No API key configured";
                            return null;
                        }

                        var key = GetProtector(directory).Unprotect(stored);
                        if (IsValid(key))
                        {
                            status = "Configured ••••••••";
                            return key;
                        }

                        status = "Saved API key unavailable";
                        return null;
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // A fresh installation or upgrade still uses the legacy source.
                    }
                    catch (Exception ex) when (IsStorageFailure(ex))
                    {
                        status = "Saved API key unavailable";
                        return null;
                    }
                }

                var legacy = YouTubeMetadataService.ReadKey(legacyPath);
                status = legacy is not null ? "Configured •••••••• (legacy file)"
                    : string.IsNullOrWhiteSpace(legacyPath) ? "No API key configured" : "Legacy API key file unusable";
                return legacy;
            }
        }

        internal static bool IsValid(string key) =>
            key.Length is >= 20 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

        internal bool TryWrite(string? key)
        {
            if (key is not null && !IsValid(key))
                return false;

            lock (_sync)
            {
                if (dataFolder() is not { } folder)
                    return false;

                var directory = Path.Combine(folder, "youtube-api-secret");
                var path = Path.Combine(directory, "api-key");
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    EnsurePrivateDirectory(directory);
                    var stored = key is null ? Cleared : GetProtector(directory).Protect(key);
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows())
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                    using (var output = new FileStream(temporary, options))
                    using (var writer = new StreamWriter(output))
                        writer.Write(stored);
                    File.Move(temporary, path, overwrite: true);
                    return true;
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception cleanup) when (IsStorageFailure(cleanup))
                    {
                        // Any leftover file is owner-only and contains protected data.
                    }

                    return false;
                }
            }
        }

        private IDataProtector GetProtector(string directory)
        {
            if (_protector is null)
            {
                EnsurePrivateDirectory(directory);
                var ring = Path.Combine(directory, "keys");
                EnsurePrivateDirectory(ring);
                _protector = DataProtectionProvider.Create(new DirectoryInfo(ring))
                    .CreateProtector("VOD-Fork.YouTubeDataApiKey.v1");
            }

            return _protector;
        }

        private static void EnsurePrivateDirectory(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, PrivateDirectory);
                File.SetUnixFileMode(directory, PrivateDirectory);
            }
        }

        private static bool IsStorageFailure(Exception error) =>
            error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or FormatException;
    }
}
