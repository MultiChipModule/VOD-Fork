using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VOD.Configuration;
using Jellyfin.Plugin.VOD.Controllers;
using Jellyfin.Plugin.VOD.Services;
using Jellyfin.Plugin.VOD.Services.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.VOD.Tests.Services.Sync
{
    public sealed class YouTubeApiKeyStoreTests : IDisposable
    {
        private const string LegacyKey = "legacy-example-key-for-testing";
        private const string NewKey = "first-example-key-for-testing";
        private const string ReplacementKey = "replacement-example-key-for-testing";
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("vod-api-key-tests-");

        [Fact]
        public void StoreSurvivesRestartAndCanReplaceOrClearTheLegacyKey()
        {
            var legacyPath = WriteLegacyKey();
            var store = OpenStore();
            Assert.Equal(LegacyKey, store.Read(legacyPath, out var status));
            Assert.Equal("Configured •••••••• (legacy file)", status);

            Assert.True(store.TryWrite(NewKey));
            store = OpenStore();
            Assert.Equal(NewKey, store.Read(legacyPath, out status));
            Assert.Equal("Configured ••••••••", status);
            Assert.True(store.TryWrite(ReplacementKey));
            Assert.False(store.TryWrite("invalid"));
            Assert.Equal(ReplacementKey, OpenStore().Read(legacyPath, out _));

            var directory = Path.Combine(_folder.FullName, "youtube-api-secret");
            var secretFile = Path.Combine(directory, "api-key");
            var ciphertext = File.ReadAllText(secretFile);
            Assert.DoesNotContain(NewKey, ciphertext, StringComparison.Ordinal);
            Assert.DoesNotContain(ReplacementKey, ciphertext, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                var fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                Assert.Equal(fileMode, File.GetUnixFileMode(secretFile));
                Assert.Equal(fileMode | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
                Assert.Equal(fileMode | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(directory, "keys")));
            }

            Assert.True(store.TryWrite(null));
            Assert.Null(OpenStore().Read(legacyPath, out status));
            Assert.Equal("No API key configured", status);
            Assert.Equal(LegacyKey, File.ReadAllText(legacyPath));
        }

        [Fact]
        public void CorruptProtectedValueDoesNotActivateTheLegacyKey()
        {
            var legacy = WriteLegacyKey();
            var store = OpenStore();
            Assert.True(store.TryWrite(NewKey));
            File.WriteAllText(Path.Combine(_folder.FullName, "youtube-api-secret", "api-key"), "invalid protected value");

            Assert.Null(OpenStore().Read(legacy, out var status));
            Assert.Equal("Saved API key unavailable", status);
        }

        [Fact]
        public async Task AdminWriteEndpointsReturnNoKeyAndMetadataUsesItOnlyInTheApiHeader()
        {
            var config = new PluginConfiguration { YouTubeApiKeyFile = WriteLegacyKey() };
            var calls = 0;
            using var client = new HttpClient(new TestHandler(request =>
            {
                calls++;
                Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
                Assert.DoesNotContain(NewKey, request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
                Assert.True(request.Headers.TryGetValues("X-Goog-Api-Key", out var header));
                Assert.Equal(NewKey, Assert.Single(header));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"items":[{"id":"v0000000000","snippet":{"title":"Fixture video"}}]}"""),
                };
            }));
            using var provider = new YouTubeMetadataService(Substitute.For<IYtDlpService>(), () => config, client, TimeProvider.System, () => _folder.FullName);
            var controller = new VodController(null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<VodController>.Instance, provider);

            using var key = JsonDocument.Parse(JsonSerializer.Serialize(NewKey));
            Assert.IsType<NoContentResult>(controller.SaveYouTubeApiKey(key.RootElement));
            using var invalid = JsonDocument.Parse("""{"key":"unwanted format"}""");
            Assert.IsType<BadRequestObjectResult>(controller.SaveYouTubeApiKey(invalid.RootElement));

            var status = JsonSerializer.Serialize(provider.Status());
            Assert.Contains("Configured", status, StringComparison.Ordinal);
            Assert.DoesNotContain(NewKey, status, StringComparison.Ordinal);
            Assert.DoesNotContain(LegacyKey, status, StringComparison.Ordinal);
            Assert.NotNull(await provider.ListAsync("https://www.youtube.com/watch?v=v0000000000",
                null, false, false, CancellationToken.None));
            Assert.Equal(1, calls);

            Assert.IsType<NoContentResult>(controller.ClearYouTubeApiKey());
            Assert.Equal("No API key configured", provider.Status().ApiKeyStatus);

            foreach (var action in new[] { nameof(VodController.SaveYouTubeApiKey), nameof(VodController.ClearYouTubeApiKey) })
            {
                var protection = typeof(VodController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();
                Assert.Equal(Policies.RequiresElevation, protection?.Policy);
            }
        }

        private YouTubeApiKeyStore OpenStore() => new(() => _folder.FullName);

        private string WriteLegacyKey()
        {
            var path = Path.Combine(_folder.FullName, "legacy.txt");
            File.WriteAllText(path, LegacyKey);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }

        public void Dispose() => _folder.Delete(recursive: true);

        private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(reply(request));
        }
    }
}
