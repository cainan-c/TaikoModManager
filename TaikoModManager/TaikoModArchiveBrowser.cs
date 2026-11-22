using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ImageMagick;
using Tomlyn;
using Tomlyn.Model;

namespace TaikoModManager
{
    public enum TaikoArchiveSort
    {
        Latest,
        Downloads,
        Likes
    }

    /// <summary>Client and installer for Taiko Mod Archive's Rhythm Festival PC catalogue.</summary>
    public sealed class TaikoModArchiveBrowser
    {
        private const string ApiBase = "https://taikomodarchive.com/api/v1";
        private const string RhythmFestivalPc = "RhythmFestivalPC";
        private readonly string _gamePath;
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public TaikoModArchiveBrowser(string gamePath)
        {
            _gamePath = gamePath;
        }

        /// <summary>Gets the current archive record for an installed post.</summary>
        public async Task<TaikoArchivePost> GetPostAsync(long postId)
        {
            if (postId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(postId));
            }

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");
            using HttpResponseMessage response = await client.GetAsync($"{ApiBase}/posts/{postId}");
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            TaikoArchivePost? post = JsonSerializer.Deserialize<TaikoArchivePost>(json, _jsonOptions);
            return post ?? throw new InvalidOperationException("Taiko Mod Archive returned an empty post.");
        }

        /// <summary>Finds the current install for an archive post using manager-owned metadata.</summary>
        public TaikoArchiveInstalledItem? FindInstalledPost(TaikoArchivePost post)
        {
            ManagerCache.EnsureDirectories();
            string installRoot = post.IsPlugin
                ? Path.Combine(_gamePath, "BepInEx", "plugins")
                : Path.Combine(_gamePath, "TekaSongs");

            foreach (string metadataPath in Directory.EnumerateFiles(ManagerCache.TmmDirectory, "*.toml", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var model = Toml.ToModel(File.ReadAllText(metadataPath));
                    if (!model.TryGetValue("archive", out var archiveObj) || archiveObj is not TomlTable archive ||
                        !archive.TryGetValue("post_id", out var postIdObj) ||
                        !long.TryParse(postIdObj?.ToString(), out long cachedPostId) || cachedPostId != post.Id ||
                        !archive.TryGetValue("post_type", out var postTypeObj) ||
                        !string.Equals(postTypeObj?.ToString(), post.PostType, StringComparison.OrdinalIgnoreCase) ||
                        !archive.TryGetValue("installed_folder", out var folderObj))
                    {
                        continue;
                    }

                    string folderName = Path.GetFileName(folderObj?.ToString() ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(folderName))
                    {
                        continue;
                    }

                    string installPath = Path.Combine(installRoot, folderName);
                    if (Directory.Exists(installPath))
                    {
                        return new TaikoArchiveInstalledItem(installPath, metadataPath);
                    }
                }
                catch
                {
                    // Ignore damaged cache entries and continue checking the remaining metadata.
                }
            }

            return null;
        }

        public async Task<TaikoArchiveFeed> GetFeedAsync(int page, int pageSize, TaikoArchiveSort sort, string search, string? postType = null)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 50);
            string safeSearch = Uri.EscapeDataString(search?.Trim() ?? string.Empty);
            string sortValue = sort switch
            {
                TaikoArchiveSort.Downloads => "download_count:desc",
                TaikoArchiveSort.Likes => "like_count:desc",
                _ => "time:desc"
            };

            string filter = $"post_game={RhythmFestivalPc}";
            if (string.Equals(postType, "Plugin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(postType, "Song", StringComparison.OrdinalIgnoreCase))
            {
                filter += $" AND post_type={postType}";
            }
            else
            {
                // Keep the combined feed limited to the two installable kinds. This
                // prevents the archive's mixed catalogue page from dropping plugins.
                filter += " AND (post_type=Plugin OR post_type=Song)";
            }

            string categoryFilter = Uri.EscapeDataString(filter);
            int offset = (page - 1) * pageSize;
            string postsUrl = $"{ApiBase}/posts?sort={Uri.EscapeDataString(sortValue)}&filter={categoryFilter}&query={safeSearch}&offset={offset}&limit={pageSize}";
            string countUrl = $"{ApiBase}/posts/count?filter={categoryFilter}&query={safeSearch}";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");

            using HttpResponseMessage postsResponse = await client.GetAsync(postsUrl);
            postsResponse.EnsureSuccessStatusCode();
            string postsJson = await postsResponse.Content.ReadAsStringAsync();
            var posts = JsonSerializer.Deserialize<List<TaikoArchivePost>>(postsJson, _jsonOptions) ?? new List<TaikoArchivePost>();

            using HttpResponseMessage countResponse = await client.GetAsync(countUrl);
            countResponse.EnsureSuccessStatusCode();
            string countText = await countResponse.Content.ReadAsStringAsync();
            if (!int.TryParse(countText, out int totalPosts))
            {
                totalPosts = posts.Count;
            }

            // The server filter is deliberately always present. The client only exposes the two installable types.
            posts = posts.Where(post => post.IsPlugin || post.IsSongMod).ToList();

            return new TaikoArchiveFeed
            {
                Posts = posts,
                Page = page,
                TotalPages = Math.Max(1, (int)Math.Ceiling(totalPosts / (double)pageSize))
            };
        }

        public async Task<string> DownloadAndInstallAsync(TaikoArchivePost post, int fileIndex,
                                                          IProgress<double>? downloadProgress = null,
                                                          TaikoArchiveInstalledItem? replaceExisting = null)
        {
            if (!string.Equals(post.PostGame, RhythmFestivalPc, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only Rhythm Festival PC posts can be installed.");
            }
            if (!post.IsPlugin && !post.IsSongMod)
            {
                throw new InvalidOperationException("Only Plugin and Song Mod posts are supported.");
            }
            if (fileIndex < 0 || fileIndex >= post.Files.Count)
            {
                throw new InvalidOperationException("The selected archive file is unavailable.");
            }

            Uri downloadUri = new(post.Files[fileIndex]);
            if (downloadUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("The archive download must use HTTPS.");
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "TaikoModManager", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                string downloadName = GetDownloadName(post, fileIndex);
                string archivePath = Path.Combine(tempRoot, downloadName);

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");
                    using HttpResponseMessage response = await client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    long? totalBytes = response.Content.Headers.ContentLength;
                    downloadProgress?.Report(double.NaN);
                    await using Stream input = await response.Content.ReadAsStreamAsync();
                    byte[] buffer = new byte[81_920];
                    long downloadedBytes = 0;
                    int bytesRead;
                    while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead));
                        downloadedBytes += bytesRead;
                        if (totalBytes is long total && total > 0)
                        {
                            downloadProgress?.Report(downloadedBytes * 100d / total);
                        }
                    }
                    downloadProgress?.Report(100d);
                }

                string extractedPath = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(extractedPath);
                await ExtractArchiveAsync(archivePath, extractedPath, post.IsPlugin);

                string contentRoot = post.IsPlugin
                    ? FindPluginContentRoot(extractedPath)
                    : FindSongModContentRoot(extractedPath);
                string? primaryDllName = post.IsPlugin
                    ? Path.GetFileName(Directory.GetFiles(contentRoot, "*.dll", SearchOption.TopDirectoryOnly).First())
                    : null;

                string installRoot = post.IsPlugin
                    ? Path.Combine(_gamePath, "BepInEx", "plugins")
                    : Path.Combine(_gamePath, "TekaSongs");
                Directory.CreateDirectory(installRoot);

                string folderName = GetSafeFolderName(post.Name, post.Id);
                string installPath = GetUniqueDirectoryPath(installRoot, folderName);
                string? backupPath = null;

                if (replaceExisting != null)
                {
                    string existingPath = Path.GetFullPath(replaceExisting.InstallPath);
                    if (!IsPathWithinDirectory(existingPath, installRoot) || !Directory.Exists(existingPath))
                    {
                        throw new InvalidOperationException("The existing archive install is no longer available to replace.");
                    }

                    installPath = existingPath;
                    backupPath = Path.Combine(installRoot, $".{Path.GetFileName(existingPath)}.tmm-backup-{Guid.NewGuid():N}");
                    Directory.Move(existingPath, backupPath);
                }

                try
                {
                    CopyDirectory(contentRoot, installPath);
                    string previewFileName = await CachePreviewAsync(post);
                    string metadataPath = WriteArchiveMetadata(post, installPath, primaryDllName, previewFileName);
                    if (replaceExisting != null &&
                        !string.Equals(replaceExisting.MetadataPath, metadataPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(replaceExisting.MetadataPath))
                    {
                        File.Delete(replaceExisting.MetadataPath);
                    }

                    if (backupPath != null && Directory.Exists(backupPath))
                    {
                        Directory.Delete(backupPath, recursive: true);
                    }

                    return installPath;
                }
                catch
                {
                    if (backupPath != null && Directory.Exists(backupPath))
                    {
                        if (Directory.Exists(installPath))
                        {
                            Directory.Delete(installPath, recursive: true);
                        }
                        Directory.Move(backupPath, installPath);
                    }
                    throw;
                }
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        private static string GetDownloadName(TaikoArchivePost post, int index)
        {
            string name = index < post.FileNames.Count ? post.FileNames[index] : $"archive-{post.Id}-{index}";
            name = Path.GetFileName(name);
            return string.IsNullOrWhiteSpace(name) ? $"archive-{post.Id}-{index}.zip" : name;
        }

        private static async Task<string> CachePreviewAsync(TaikoArchivePost post)
        {
            if (string.IsNullOrWhiteSpace(post.PreviewUrl) ||
                !Uri.TryCreate(post.PreviewUrl, UriKind.Absolute, out Uri? previewUri) ||
                previewUri.Scheme != Uri.UriSchemeHttps)
            {
                return string.Empty;
            }

            ManagerCache.EnsureDirectories();
            string previewPath = ManagerCache.ArchivePreviewImagePath(post.Id);
            if (File.Exists(previewPath))
            {
                return Path.GetFileName(previewPath);
            }

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");
                byte[] imageBytes = await client.GetByteArrayAsync(previewUri);
                await Task.Run(() =>
                {
                    using var image = new MagickImage(imageBytes);
                    image.Write(previewPath, MagickFormat.Png);
                });
                return Path.GetFileName(previewPath);
            }
            catch
            {
                // The installed item still has its cached text metadata if its image cannot be saved.
                return string.Empty;
            }
        }

        private static string WriteArchiveMetadata(TaikoArchivePost post, string installPath, string? primaryDllName, string previewFileName)
        {
            ManagerCache.EnsureDirectories();
            string installedFolder = Path.GetFileName(installPath);
            var archive = new TomlTable
            {
                ["source"] = "TaikoModArchive",
                ["post_id"] = post.Id,
                ["post_type"] = post.PostType,
                ["last_updated"] = post.LastUpdated.ToUniversalTime().ToString("O"),
                ["name"] = post.Name,
                ["author"] = post.AuthorName,
                ["description"] = post.Text,
                ["preview_url"] = post.PreviewUrl,
                ["preview_file"] = previewFileName,
                ["installed_folder"] = installedFolder,
                ["primary_dll"] = primaryDllName ?? string.Empty
            };
            var metadata = new TomlTable { ["archive"] = archive };
            string metadataPath = post.IsPlugin
                ? ManagerCache.ArchivePluginMetadataPath(primaryDllName!)
                : ManagerCache.ArchiveSongMetadataPath(installedFolder);
            File.WriteAllText(metadataPath, Toml.FromModel(metadata));
            return metadataPath;
        }

        private static bool IsPathWithinDirectory(string path, string directory)
        {
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task ExtractArchiveAsync(string archivePath, string destination, bool isPlugin)
        {
            string extension = Path.GetExtension(archivePath);
            if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (!isPlugin)
                {
                    throw new InvalidOperationException("Song Mods must be distributed as an archive containing config.toml.");
                }
                File.Copy(archivePath, Path.Combine(destination, Path.GetFileName(archivePath)));
                return;
            }

            if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, destination);
                return;
            }

            string sevenZipPath = Path.Combine(AppContext.BaseDirectory, "7z", "7z.exe");
            if (!File.Exists(sevenZipPath))
            {
                throw new InvalidOperationException($"Unsupported archive format '{extension}'. The bundled 7z extractor is unavailable.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                WorkingDirectory = destination,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("x");
            startInfo.ArgumentList.Add(archivePath);
            startInfo.ArgumentList.Add($"-o{destination}");
            startInfo.ArgumentList.Add("-y");

            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Could not start the archive extractor.");
            }

            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Could not extract {Path.GetFileName(archivePath)}. {error}".Trim());
            }
        }

        private static string FindPluginContentRoot(string extractedPath)
        {
            string? dll = Directory.GetFiles(extractedPath, "*.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (dll == null)
            {
                throw new InvalidOperationException("The Plugin archive does not contain a .dll file.");
            }
            return Path.GetDirectoryName(dll)!;
        }

        private static string FindSongModContentRoot(string extractedPath)
        {
            string? config = Directory.GetFiles(extractedPath, "config.toml", SearchOption.AllDirectories).FirstOrDefault();
            if (config == null)
            {
                throw new InvalidOperationException("The Song Mod archive does not contain config.toml.");
            }
            return Path.GetDirectoryName(config)!;
        }

        private static string GetSafeFolderName(string name, long id)
        {
            string safeName = string.Concat((name ?? string.Empty).Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().Trim('.');
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = $"Archive Mod {id}";
            }
            return safeName.Length <= 80 ? safeName : safeName[..80].TrimEnd();
        }

        private static string GetUniqueDirectoryPath(string root, string folderName)
        {
            string candidate = Path.Combine(root, folderName);
            int number = 2;
            while (Directory.Exists(candidate))
            {
                candidate = Path.Combine(root, $"{folderName} ({number++})");
            }
            return candidate;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    public sealed class TaikoArchiveFeed
    {
        public List<TaikoArchivePost> Posts { get; init; } = new();
        public int Page { get; init; }
        public int TotalPages { get; init; }
    }

    public sealed record TaikoArchiveInstalledItem(string InstallPath, string MetadataPath);

    public sealed class TaikoArchivePost
    {
        public long Id { get; set; }
        public string Name { get; set; } = "Untitled Mod";
        public string Text { get; set; } = string.Empty;
        [JsonPropertyName("time")]
        public DateTimeOffset LastUpdated { get; set; }
        [JsonPropertyName("post_type")]
        public string PostType { get; set; } = string.Empty;
        [JsonPropertyName("post_game")]
        public string PostGame { get; set; } = string.Empty;
        [JsonPropertyName("download_count")]
        public long DownloadCount { get; set; }
        [JsonPropertyName("like_count")]
        public long LikeCount { get; set; }
        public List<string> Images { get; set; } = new();
        public List<string> Files { get; set; } = new();
        [JsonPropertyName("file_names")]
        public List<string> FileNames { get; set; } = new();
        public List<TaikoArchiveAuthor> Authors { get; set; } = new();

        public bool IsPlugin => string.Equals(PostType, "Plugin", StringComparison.OrdinalIgnoreCase);
        public bool IsSongMod => string.Equals(PostType, "Song", StringComparison.OrdinalIgnoreCase);
        public string TypeLabel => IsPlugin ? "PLUGIN" : "SONG MOD";
        public string AuthorName => Authors.FirstOrDefault()?.DisplayNameOrName ?? "Unknown author";
        public string PreviewUrl => Images.FirstOrDefault() ?? string.Empty;
        public string Stats => $"{DownloadCount:N0} downloads  •  {LikeCount:N0} likes";
    }

    public sealed class TaikoArchiveAuthor
    {
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;
        public string DisplayNameOrName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    }
}
