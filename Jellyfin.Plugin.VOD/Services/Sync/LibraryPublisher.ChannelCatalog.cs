using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VOD.Services.Sync
{
    public sealed partial class LibraryPublisher
    {
        internal const string ChannelCatalogProvider = "VodYouTubeCatalog";
        private const string CatalogDirectoryMarker = ".vod-youtube-catalog";
        private const string CatalogDirectoryMarkerText = "Folder managed by VOD-Fork";

        internal readonly record struct ChannelCatalogReport(int Created, int Updated, int Unchanged, int Removed);

        internal async Task<ChannelCatalogReport> PublishChannelCatalogAsync(
            Folder anchor, IReadOnlyList<ChannelNode> channels, CancellationToken cancellationToken)
        {
            int created = 0, updatedCount = 0, unchanged = 0, removed = 0;
            var expected = new HashSet<Guid>();
            for (var channelIndex = 0; channelIndex < channels.Count; channelIndex++)
            {
                var channel = channels[channelIndex];
                cancellationToken.ThrowIfCancellationRequested();
                var channelKey = $"vod-youtube:{anchor.Id:N}:{channel.Id}";
                var folderPath = CatalogPath(anchor.Path, channel.Id, playlist: false);
                EnsureCatalogDirectory(folderPath);
                var id = _libraryManager.GetNewItemId(folderPath, typeof(Folder));
                expected.Add(id);
                var existing = _libraryManager.GetItemById<Folder>(id);
                var folder = existing ?? new Folder { Id = id, ParentId = anchor.Id, Path = folderPath };
                var changed = ApplyCatalogMetadata(folder, channelKey, channel.Name, null, channel.Avatar, channel.Banner, CatalogSortName(channelIndex, channel.Name));
                if (existing is null)
                    created++;
                else if (changed)
                    updatedCount++;
                else
                    unchanged++;

                if (existing is null || changed)
                {
                    await CommitAsync(folder, anchor, existing is null, cancellationToken).ConfigureAwait(false);
                }

                var expectedPlaylists = new HashSet<Guid>();
                for (var playlistIndex = 0; playlistIndex < channel.Playlists.Count; playlistIndex++)
                {
                    var playlist = channel.Playlists[playlistIndex];
                    var key = $"{channelKey}:{playlist.Id}";
                    var playlistPath = CatalogPath(folderPath, playlist.Id, playlist: true);
                    EnsureCatalogDirectory(playlistPath);
                    var playlistId = _libraryManager.GetNewItemId(playlistPath, typeof(Playlist));
                    expectedPlaylists.Add(playlistId);
                    var oldPlaylist = _libraryManager.GetItemById<Playlist>(playlistId);
                    var item = oldPlaylist ?? new Playlist { Id = playlistId, ParentId = folder.Id, Path = playlistPath };
                    var updated = ApplyCatalogMetadata(item, key, playlist.Name, playlist.Description, playlist.Artwork,
                        null, CatalogSortName(playlistIndex, playlist.Name));
                    if (!item.OpenAccess)
                    {
                        item.OpenAccess = true;
                        updated = true;
                    }

                    if (item.PlaylistMediaType != MediaType.Video)
                    {
                        item.SetMediaType(MediaType.Video);
                        updated = true;
                    }

                    if (!item.LinkedChildren.Select(child => child.ItemId).SequenceEqual(playlist.Items.Select(itemId => (Guid?)itemId)))
                    {
                        item.LinkedChildren = playlist.Items.Select(itemId =>
                            new LinkedChild { Type = LinkedChildType.Manual, ItemId = itemId }).ToArray();
                        updated = true;
                    }

                    if (oldPlaylist is null)
                        created++;
                    else if (updated)
                        updatedCount++;
                    else
                        unchanged++;

                    if (oldPlaylist is null || updated)
                    {
                        await CommitAsync(item, folder, oldPlaylist is null, cancellationToken).ConfigureAwait(false);
                    }
                }

                removed += RemoveOldCatalogNodes(folder, expectedPlaylists, cancellationToken);
            }

            removed += RemoveOldCatalogNodes(anchor, expected, cancellationToken);
            return new ChannelCatalogReport(created, updatedCount, unchanged, removed);
        }

        private static string CatalogSortName(int index, string name) =>
            string.Create(CultureInfo.InvariantCulture, $"{index:D10} {name}");

        private int RemoveOldCatalogNodes(Folder parent, HashSet<Guid> expected, CancellationToken token)
        {
            var removed = 0;
            foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery { ParentId = parent.Id }))
            {
                token.ThrowIfCancellationRequested();
                if (!expected.Contains(item.Id) && item.GetProviderId(ChannelCatalogProvider) is not null)
                {
                    RemoveManagedDirectory(item.Path, parent.Path);
                    _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
                    removed++;
                }
            }

            return removed;
        }

        // Use the same path-based identity the library scanner derives for native folders.
        internal static string CatalogPath(string parent, string stableId, bool playlist)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stableId)));
            return Path.Combine(parent, "vod-" + hash + (playlist ? " [playlist]" : string.Empty));
        }

        private static void EnsureCatalogDirectory(string path)
        {
            var directory = Directory.CreateDirectory(path);
            var marker = Path.Combine(path, CatalogDirectoryMarker);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The YouTube channel catalog path is a symbolic link.");

            if (File.Exists(marker) && File.ReadAllText(marker) == CatalogDirectoryMarkerText)
                return;

            if (Directory.EnumerateFileSystemEntries(path).Any())
                throw new InvalidOperationException("The YouTube channel catalog path is already occupied.");

            File.WriteAllText(marker, CatalogDirectoryMarkerText);
        }

        private static void RemoveManagedDirectory(string? path, string? parent)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(parent) || !Directory.Exists(path)
                || !string.Equals(Path.GetDirectoryName(path), parent, StringComparison.Ordinal)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return;

            var marker = Path.Combine(path, CatalogDirectoryMarker);
            if (!File.Exists(marker) || File.ReadAllText(marker) != CatalogDirectoryMarkerText)
                return;

            foreach (var child in Directory.EnumerateDirectories(path))
                RemoveManagedDirectory(child, path);

            if (Directory.EnumerateFileSystemEntries(path).All(entry => string.Equals(entry, marker, StringComparison.Ordinal)))
            {
                File.Delete(marker);
                Directory.Delete(path);
            }
        }

        private static bool ApplyCatalogMetadata(BaseItem item, string key, string name, string? overview,
            string? artwork, string? banner, string sortName)
        {
            var changed = !string.Equals(item.Name, name, StringComparison.Ordinal)
                || !string.Equals(item.Overview, overview, StringComparison.Ordinal)
                || !string.Equals(item.ForcedSortName, sortName, StringComparison.Ordinal) || !item.IsLocked;
            item.IsLocked = true;
            item.Name = name;
            item.Overview = overview;
            item.ForcedSortName = sortName;
            changed |= SetProviderId(item, ChannelCatalogProvider, key);
            changed |= ApplyCatalogImage(item, ImageType.Primary, artwork, ChannelCatalogProvider + "Artwork");
            changed |= ApplyCatalogImage(item, ImageType.Banner, banner, ChannelCatalogProvider + "Banner");
            return changed;
        }

        private static bool ApplyCatalogImage(BaseItem item, ImageType type, string? url, string provider)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !(uri.IdnHost == "i.ytimg.com" || uri.IdnHost.EndsWith(".googleusercontent.com", StringComparison.Ordinal)
                    || uri.IdnHost.EndsWith(".ggpht.com", StringComparison.Ordinal)))
            {
                return false;
            }

            var updated = ApplyImage(item, type, url, item.GetProviderId(provider), null, out _);
            return updated && SetProviderId(item, provider, url!);
        }
    }
}
