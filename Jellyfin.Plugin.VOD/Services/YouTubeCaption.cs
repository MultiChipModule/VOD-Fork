using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.VOD.Services
{
    internal sealed record YouTubeCaption(Uri Url, CaptionKind Kind)
    {
        internal static bool IsYouTubePage(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (IsYouTubeHost(uri.IdnHost) || uri.IdnHost == "youtu.be");

        internal static bool IsTimedText(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && uri.UserInfo.Length == 0
            && IsYouTubeHost(uri.IdnHost)
            && uri.AbsolutePath == "/api/timedtext";

        internal static bool IsEnglish(string language) =>
            language.Equals("en", StringComparison.OrdinalIgnoreCase)
            || language.Equals("eng", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

        internal static IReadOnlyList<YouTubeCaption> ReadEnglish(JsonElement root)
        {
            var captions = new List<YouTubeCaption>();
            AddTracks(root, "subtitles", automatic: false, captions);
            AddTracks(root, "automatic_captions", automatic: true, captions);
            return captions.OrderBy(caption => caption.Kind).DistinctBy(caption => caption.Url).ToArray();
        }

        internal static string? ReadString(JsonElement value, string property) =>
            value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var field)
            && field.ValueKind == JsonValueKind.String
            && field.GetString() is { Length: > 0 } text
            && text != "NA"
                ? text
                : null;

        private static bool IsYouTubeHost(string host) =>
            host is "www.youtube.com" or "youtube.com" or "m.youtube.com";

        private static void AddTracks(JsonElement root, string property, bool automatic, List<YouTubeCaption> captions)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(property, out var tracks)
                || tracks.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var language in tracks.EnumerateObject())
            {
                if (!IsEnglish(language.Name) || language.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var track in language.Value.EnumerateArray())
                {
                    if (ReadString(track, "ext") != "vtt"
                        || !Uri.TryCreate(ReadString(track, "url"), UriKind.Absolute, out var uri)
                        || !IsTimedText(uri))
                    {
                        continue;
                    }

                    var query = QueryHelpers.ParseQuery(uri.Query);
                    var translated = query.TryGetValue("tlang", out var target)
                        && (!query.TryGetValue("lang", out var source)
                            || !string.Equals(source.ToString(), target.ToString(), StringComparison.OrdinalIgnoreCase));
                    var kind = translated
                        ? CaptionKind.Translated
                        : !automatic
                            ? CaptionKind.Creator
                            : language.Name.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)
                                ? CaptionKind.OriginalAutomatic
                                : CaptionKind.Automatic;
                    captions.Add(new YouTubeCaption(uri, kind));
                }
            }
        }
    }

    internal enum CaptionKind
    {
        Creator,
        OriginalAutomatic,
        Automatic,
        Translated
    }
}
