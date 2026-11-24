using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Services;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Integration
{
    public sealed class YouTubeHlsPlaybackTests : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("vod-hls-").FullName;

        [Fact]
        public async Task RemovedSubtitleRowReproducesSilentAudioOnlyRemux()
        {
            await BuildInputsAsync();
            var original = await ProbeAsync("single.m3u8");
            Assert.Equal([MediaStreamType.Audio, MediaStreamType.Subtitle, MediaStreamType.Video], original.Select(stream => stream.Type));
            Assert.Equal(2, original[2].Index);
            Assert.False(StreamDescriber.HasCurrentYouTubeLayout(original));

            var broken = original.Where(stream => stream.Type != MediaStreamType.Subtitle).ToArray();
            Assert.False(StreamDescriber.HasCurrentYouTubeLayout(broken));
            var args = Map(broken);
            Assert.Equal(["-map", "0:1", "-map", "0:0", "-map", "-0:s"], args);
            await RunAsync("ffmpeg", ["-i", "single.m3u8", .. args, "-c", "copy", "broken.mkv"]);
            Assert.Equal(MediaStreamType.Audio, Assert.Single(await ProbeAsync("broken.mkv")).Type);
        }

        [Fact]
        public async Task CleanMastersRemuxAndTranscodeFromStartAndResumeWithBothExternalTracks()
        {
            await BuildInputsAsync();
            foreach (var (height, width, offset, transcode) in new[]
            {
                (180, 320, "0", false), (360, 640, "1.2", false), (180, 320, "0", true), (360, 640, "1.2", true)
            })
            {
                var filtered = HlsPlaylistFilter.FilterMaster(
                    await File.ReadAllTextAsync(Path.Combine(_directory, "master.m3u8")),
                    new Uri(Path.Combine(_directory, "master.m3u8")), height, "h264", [], [],
                    rewriteSubtitle: static _ => null);
                Assert.NotNull(filtered);
                Assert.DoesNotContain("SUBTITLES", filtered, StringComparison.Ordinal);
                Assert.DoesNotContain("wvtt", filtered, StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(_directory, "clean.m3u8"), filtered);
                var probed = await ProbeAsync("clean.m3u8");
                Assert.True(StreamDescriber.HasCurrentYouTubeLayout(probed));
                Assert.Equal([MediaStreamType.Audio, MediaStreamType.Video], probed.Select(stream => stream.Type));

                var advertised = probed.Concat(
                [
                    new MediaStream { Index = 1000, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "creator.vtt" },
                    new MediaStream { Index = 1001, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "automatic.vtt" }
                ]).ToArray();
                Assert.Equal(Map(probed), Map(advertised));
                Assert.True(StreamDescriber.HasCurrentYouTubeLayout(advertised));
                var output = $"clean-{height}-{transcode}.mkv";
                await RunAsync("ffmpeg", ["-ss", offset, "-i", "clean.m3u8", .. Map(advertised),
                    "-c:a", "copy", "-c:v", transcode ? "libx264" : "copy", "-threads", "1", output]);
                var result = await ProbeAsync(output);
                Assert.Contains(result, stream => stream.Type == MediaStreamType.Audio && stream.Codec == "aac");
                var video = Assert.Single(result, stream => stream.Type == MediaStreamType.Video);
                Assert.Equal("h264", video.Codec);
                Assert.Equal(width, video.Width);
                Assert.Equal(height, video.Height);
                await RunAsync("ffmpeg", ["-xerror", "-i", output, "-map", "0:v", "-frames:v", "8", "-f", "null", "-"]);
                var frames = await RunAsync("ffprobe", ["-select_streams", "v", "-count_frames",
                    "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", output]);
                Assert.True(int.Parse(frames.Trim(), CultureInfo.InvariantCulture) > 8);
                await AssertTimestampsAsync(output);
            }
        }

        [Fact]
        public async Task MuxedVariantKeepsVideoFirstWithTwoAudioTracks()
        {
            await RunAsync("ffmpeg", ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24",
                "-f", "lavfi", "-i", "sine=frequency=400:sample_rate=44100",
                "-f", "lavfi", "-i", "sine=frequency=800:sample_rate=44100",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-t", "3", "-c:v", "libx264",
                "-threads", "1", "-g", "24", "-c:a", "aac", "-f", "hls", "-hls_time", "1",
                "-hls_playlist_type", "vod", "muxed.m3u8"]);
            var probed = await ProbeAsync("muxed.m3u8");
            Assert.Equal([MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Audio],
                probed.Select(stream => stream.Type));
            Assert.True(StreamDescriber.HasCurrentYouTubeLayout(probed));
            await RunAsync("ffmpeg", ["-i", "muxed.m3u8", .. Map(probed), "-c", "copy", "muxed.mkv"]);
            Assert.Equal([MediaStreamType.Video, MediaStreamType.Audio],
                (await ProbeAsync("muxed.mkv")).Select(stream => stream.Type));
            await AssertTimestampsAsync("muxed.mkv");
        }

        [Fact]
        public async Task AudioOnlyEncodingPreservesCopyableVideo()
        {
            await RunAsync("ffmpeg", ["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=60",
                "-f", "lavfi", "-i", "sine=frequency=400:sample_rate=48000", "-t", "2", "-c:v", "libx264",
                "-threads", "1", "-c:a", "libopus", "input-opus.mkv"]);
            await RunAsync("ffmpeg", ["-i", "input-opus.mkv", "-map", "0:v", "-map", "0:a",
                "-c:v", "copy", "-c:a", "aac", "audio-only-encode.mp4"]);
            var output = await ProbeAsync("audio-only-encode.mp4");
            Assert.Equal("h264", Assert.Single(output, stream => stream.Type == MediaStreamType.Video).Codec);
            Assert.Equal("aac", Assert.Single(output, stream => stream.Type == MediaStreamType.Audio).Codec);
            var fps = await RunAsync("ffprobe", ["-select_streams", "v", "-show_entries", "stream=r_frame_rate",
                "-of", "csv=p=0", "audio-only-encode.mp4"]);
            Assert.Equal("60/1", fps.Trim());
            await RunAsync("ffmpeg", ["-xerror", "-i", "audio-only-encode.mp4", "-map", "0:v", "-frames:v", "8", "-f", "null", "-"]);
        }

        private async Task AssertTimestampsAsync(string file)
        {
            using var result = JsonDocument.Parse(await RunAsync("ffprobe",
                ["-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "json", file]));
            var packets = result.RootElement.GetProperty("packets").EnumerateArray()
                .GroupBy(packet => packet.GetProperty("stream_index").GetInt32()).ToArray();
            Assert.Equal(2, packets.Length);
            var starts = new List<double>();
            var finishes = new List<double>();
            foreach (var stream in packets)
            {
                var timing = stream.Select(packet => (
                    Pts: double.Parse(packet.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture),
                    Duration: double.Parse(packet.GetProperty("duration_time").GetString()!, CultureInfo.InvariantCulture)))
                    .ToArray();
                Assert.All(timing, packet => Assert.True(double.IsFinite(packet.Pts) && packet.Duration > 0));
                starts.Add(timing.Min(packet => packet.Pts));
                finishes.Add(timing.Max(packet => packet.Pts + packet.Duration));
            }

            Assert.InRange(Math.Abs(starts[0] - starts[1]), 0, 0.25);
            Assert.InRange(Math.Abs(finishes[0] - finishes[1]), 0, 0.25);
        }

        private static string[] Map(IReadOnlyList<MediaStream> streams)
        {
            var video = streams.First(stream => stream.Type == MediaStreamType.Video);
            var audio = streams.First(stream => stream.Type == MediaStreamType.Audio);
            return ["-map", $"0:{EncodingHelper.FindIndex(streams, video)}",
                "-map", $"0:{EncodingHelper.FindIndex(streams, audio)}", "-map", "-0:s"];
        }

        private async Task<MediaStream[]> ProbeAsync(string file)
        {
            using var result = JsonDocument.Parse(await RunAsync("ffprobe",
                ["-show_entries", "stream=index,codec_type,codec_name,width,height", "-of", "json", file]));
            return result.RootElement.GetProperty("streams").EnumerateArray().Select(stream => new MediaStream
            {
                Index = stream.GetProperty("index").GetInt32(),
                Type = Enum.Parse<MediaStreamType>(stream.GetProperty("codec_type").GetString()!, ignoreCase: true),
                Codec = stream.GetProperty("codec_name").GetString(),
                Width = stream.TryGetProperty("width", out var width) ? width.GetInt32() : null,
                Height = stream.TryGetProperty("height", out var height) ? height.GetInt32() : null
            }).ToArray();
        }

        private async Task BuildInputsAsync()
        {
            foreach (var size in new[] { "320x180", "640x360" })
            {
                await RunAsync("ffmpeg", ["-f", "lavfi", "-i", $"testsrc2=size={size}:rate=24", "-t", "4", "-an",
                    "-c:v", "libx264", "-threads", "1", "-g", "24", "-sc_threshold", "0", "-f", "hls", "-hls_time", "1",
                    "-hls_playlist_type", "vod", $"video-{size}.m3u8"]);
            }

            await RunAsync("ffmpeg", ["-f", "lavfi", "-i", "sine=frequency=400:sample_rate=44100", "-t", "4",
                "-vn", "-c:a", "aac", "-f", "hls", "-hls_time", "1", "-hls_playlist_type", "vod", "audio.m3u8"]);
            await File.WriteAllTextAsync(Path.Combine(_directory, "subtitle.vtt"),
                "WEBVTT\n\n00:00:00.000 --> 00:00:03.000\nHello\n");
            await File.WriteAllTextAsync(Path.Combine(_directory, "subtitle.m3u8"),
                "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nsubtitle.vtt\n#EXT-X-ENDLIST\n");
            await File.WriteAllTextAsync(Path.Combine(_directory, "master.m3u8"), """
                #EXTM3U
                #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="English",LANGUAGE="en",DEFAULT=YES,AUTOSELECT=YES,URI="audio.m3u8"
                #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="s",NAME="English",LANGUAGE="en-US",DEFAULT=YES,AUTOSELECT=YES,URI="subtitle.m3u8"
                #EXT-X-STREAM-INF:BANDWIDTH=400000,RESOLUTION=320x180,CODECS="avc1.64000D,mp4a.40.2,wvtt",AUDIO="a",SUBTITLES="s"
                video-320x180.m3u8
                #EXT-X-STREAM-INF:BANDWIDTH=850000,RESOLUTION=640x360,CODECS="avc1.64001E,mp4a.40.2,wvtt",AUDIO="a",SUBTITLES="s"
                video-640x360.m3u8

                """);
            // Reproduce the exact production input with one variant. The clean cases use both.
            var master = await File.ReadAllTextAsync(Path.Combine(_directory, "master.m3u8"));
            await File.WriteAllTextAsync(Path.Combine(_directory, "single.m3u8"),
                master[..master.IndexOf("#EXT-X-STREAM-INF:BANDWIDTH=850000", StringComparison.Ordinal)]);
        }

        private async Task<string> RunAsync(string executable, string[] arguments)
        {
            var binaries = Environment.GetEnvironmentVariable("VOD_TEST_FFMPEG_DIR");
            var command = new ProcessStartInfo(binaries is null ? executable : Path.Combine(binaries, executable))
            {
                WorkingDirectory = _directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[] { "-v", "error" }.Concat(arguments))
            {
                command.ArgumentList.Add(argument);
            }

            using var process = Process.Start(command)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            Assert.True(process.ExitCode == 0, await errors);
            return await output;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
