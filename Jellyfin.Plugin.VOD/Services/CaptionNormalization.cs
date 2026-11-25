using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VOD.Services
{
    internal static partial class CaptionNormalization
    {
        internal static string PlainWebVtt(string text, bool singleLine = false)
        {
            var output = new StringBuilder(text.Length);
            var skipBlock = false;
            var inCue = false;
            var hasCueText = false;
            foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (singleLine && hasCueText)
                        output.Append('\n');
                    hasCueText = false;
                    skipBlock = false;
                    inCue = false;
                    output.Append('\n');
                    continue;
                }

                if (skipBlock)
                {
                    continue;
                }

                if (!inCue && (line == "STYLE" || line == "REGION"))
                {
                    skipBlock = true;
                    continue;
                }

                var timing = CueTiming().Match(line);
                if (timing.Success)
                {
                    inCue = true;
                    output.Append(timing.Value).Append('\n');
                }
                else if (singleLine && inCue)
                {
                    var plain = InlineTimingOrBreak().Replace(line, match => match.Value[1] == 'b'
                        || match.Value[1] == 'B' ? " " : string.Empty);
                    plain = Whitespace().Replace(PresentationTag().Replace(plain, string.Empty), " ").Trim();
                    if (plain.Length > 0)
                    {
                        if (hasCueText)
                            output.Append(' ');
                        output.Append(plain);
                        hasCueText = true;
                    }
                }
                else
                {
                    output.Append(inCue ? PresentationTag().Replace(line, string.Empty) : line).Append('\n');
                }
            }

            return output.ToString().TrimEnd('\n') + "\n";
        }

        [GeneratedRegex(@"<br\s*/?>|<(?:(?:\d{2,}:)?\d{2}:\d{2}\.\d{3})>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex InlineTimingOrBreak();

        [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
        private static partial Regex Whitespace();

        [GeneratedRegex(@"^(?:(?:\d{2,}:)?\d{2}:\d{2}\.\d{3})[ \t]+-->[ \t]+(?:(?:\d{2,}:)?\d{2}:\d{2}\.\d{3})", RegexOptions.CultureInvariant)]
        private static partial Regex CueTiming();

        [GeneratedRegex(@"</?(?:c(?:\.[^>\s]+)*|b|i|u|v|font)(?:[ \t]+[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex PresentationTag();
    }
}
