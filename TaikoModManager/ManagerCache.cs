using System;
using System.IO;

namespace TaikoModManager
{
    /// <summary>Centralises manager-owned metadata while keeping the main config at data/config.toml.</summary>
    public static class ManagerCache
    {
        public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, "data");
        public static string GitHubDirectory => Path.Combine(RootDirectory, "GitHub");
        public static string TmmDirectory => Path.Combine(RootDirectory, "TMM");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(GitHubDirectory);
            Directory.CreateDirectory(TmmDirectory);
        }

        /// <summary>Moves pre-folder cache entries created by older manager versions into Data/GitHub.</summary>
        public static void MigrateLegacyMetadata()
        {
            EnsureDirectories();
            foreach (string file in Directory.GetFiles(RootDirectory, "*.toml", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(file), "config.toml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destination = Path.Combine(GitHubDirectory, Path.GetFileName(file));
                if (File.Exists(destination))
                {
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    int number = 1;
                    do
                    {
                        destination = Path.Combine(GitHubDirectory, $"{baseName}.legacy-{number++}.toml");
                    }
                    while (File.Exists(destination));
                }
                File.Move(file, destination);
            }
        }

        public static string GitHubPluginMetadataPath(string dllName) =>
            Path.Combine(GitHubDirectory, dllName + ".toml");

        /// <summary>Archive plugin metadata follows the primary DLL's filename, just like GitHub plugin metadata.</summary>
        public static string ArchivePluginMetadataPath(string dllName) =>
            Path.Combine(TmmDirectory, dllName + ".toml");

        public static string ArchiveSongMetadataPath(string installedFolder) =>
            Path.Combine(TmmDirectory, $"song-{installedFolder}.toml");

        /// <summary>One locally converted preview image per archive post.</summary>
        public static string ArchivePreviewImagePath(long postId) =>
            Path.Combine(TmmDirectory, $"preview-{postId}.png");
    }
}
