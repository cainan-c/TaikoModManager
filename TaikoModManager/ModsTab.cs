using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;    // For MessageBox, if desired
using Tomlyn;           // For Toml parsing
using Tomlyn.Model;

namespace TaikoModManager
{
    public class ModsTab
    {
        private readonly string _gamePath;
        private readonly string[] _tekaTekaDllPaths;
        private readonly string _tekaSongsPath;      // [GameDir]/TekaSongs

        public ModsTab(string gamePath)
        {
            _gamePath = gamePath;
            _tekaTekaDllPaths = new[]
            {
                // Original manual-install layout.
                Path.Combine(gamePath, "BepInEx", "plugins", "RF.TekaTeka", "RF.TekaTeka.dll"),
                // Taiko Mod Manager archive layout (post 3 is installed as TekaTeka).
                Path.Combine(gamePath, "BepInEx", "plugins", "TekaTeka", "RF.TekaTeka.dll"),
                Path.Combine(gamePath, "BepInEx", "plugins", "TekaTeka", "TekaTeka.dll")
            };
            _tekaSongsPath = Path.Combine(gamePath, "TekaSongs");
        }

        /// <summary>
        /// Checks both the legacy and manager-installed TekaTeka layouts.
        /// </summary>
        public bool IsTekaTekaInstalled()
        {
            return _tekaTekaDllPaths.Any(File.Exists);
        }

        /// <summary>
        /// Checks if TekaSongs/ folder exists in the game directory.
        /// </summary>
        public bool IsTekaSongsInitialized()
        {
            return Directory.Exists(_tekaSongsPath);
        }

        /// <summary>
        /// Enumerates all subfolders in TekaSongs, loads each mod's config.toml.
        /// Returns a list of TekaTekaModInfo objects for display/binding in the UI.
        /// </summary>
        public List<TekaTekaModInfo> LoadMods()
        {
            var result = new List<TekaTekaModInfo>();

            if (!IsTekaTekaInstalled() || !IsTekaSongsInitialized())
            {
                // No TekaTeka, or TekaSongs folder missing => we won't load mods
                return result;
            }

            // For each subfolder in TekaSongs
            string[] modFolders = Directory.GetDirectories(_tekaSongsPath);
            foreach (string folder in modFolders)
            {
                string configPath = Path.Combine(folder, "config.toml");
                if (!File.Exists(configPath))
                    continue; // skip if no config

                // Create a mod info object
                var modInfo = ParseModConfig(configPath);
                result.Add(modInfo);
            }

            return result;
        }

        /// <summary>
        /// Opens the TekaSongs folder in Explorer, if it exists.
        /// </summary>
        public void OpenModsFolder()
        {
            if (!IsTekaSongsInitialized())
            {
                MessageBox.Show("TekaTeka has not been initialized. Please run the game first to generate TekaSongs folder.",
                                "TekaSongs Missing",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                return;
            }

            // Sanitize the path before opening
            string normalizedTekaSongsPath = _tekaSongsPath.Replace(@"\\", @"\")
                                                           .Replace(@"/", @"\");

            System.Diagnostics.Process.Start("explorer.exe", normalizedTekaSongsPath);
        }

        /// <summary>
        /// Parses a "config.toml" for:
        /// enabled      = true/false
        /// name         = "..."
        /// version      = "..."
        /// description  = "..."
        /// author       = "..."
        /// </summary>
        private TekaTekaModInfo ParseModConfig(string configPath)
        {
            var modInfo = new TekaTekaModInfo
            {
                ConfigPath = configPath,
                CanEnable = IsTekaTekaInstalled(),
                Source = "External"
            };

            try
            {
                string tomlString = File.ReadAllText(configPath);
                var tomlModel = Toml.ToModel(tomlString);

                if (tomlModel.TryGetValue("enabled", out var enabledObj))
                {
                    modInfo.IsModEnabled = Convert.ToBoolean(enabledObj);
                }
                if (tomlModel.TryGetValue("name", out var nameObj))
                {
                    modInfo.Name = nameObj?.ToString() ?? "Unknown Mod";
                }
                if (tomlModel.TryGetValue("version", out var versionObj))
                {
                    modInfo.Version = versionObj?.ToString() ?? "0.0";
                }
                if (tomlModel.TryGetValue("description", out var descObj))
                {
                    modInfo.Description = descObj?.ToString() ?? "";
                }
                // NEW: author field
                if (tomlModel.TryGetValue("author", out var authorObj))
                {
                    modInfo.Author = authorObj?.ToString() ?? "";
                }
            }
            catch
            {
                // If there's a parse error, fallback
                modInfo.Name = Path.GetFileNameWithoutExtension(configPath);
            }

            ApplyArchiveMetadata(modInfo, Path.GetFileName(Path.GetDirectoryName(configPath) ?? string.Empty));

            return modInfo;
        }

        private void ApplyArchiveMetadata(TekaTekaModInfo modInfo, string installedFolder)
        {
            string metadataPath = ManagerCache.ArchiveSongMetadataPath(installedFolder);
            if (!File.Exists(metadataPath))
            {
                return;
            }

            try
            {
                var model = Toml.ToModel(File.ReadAllText(metadataPath));
                if (!model.TryGetValue("archive", out var archiveObj) || archiveObj is not TomlTable archive)
                {
                    return;
                }

                if (archive.TryGetValue("name", out var name)) modInfo.Name = name?.ToString() ?? modInfo.Name;
                if (archive.TryGetValue("author", out var author)) modInfo.Author = author?.ToString() ?? modInfo.Author;
                if (archive.TryGetValue("description", out var description)) modInfo.Description = description?.ToString() ?? modInfo.Description;
                if (archive.TryGetValue("last_updated", out var lastUpdated)) modInfo.LastUpdated = lastUpdated?.ToString() ?? string.Empty;
                if (archive.TryGetValue("preview_file", out var previewFile))
                {
                    string cachedPreview = Path.Combine(ManagerCache.TmmDirectory, Path.GetFileName(previewFile?.ToString() ?? string.Empty));
                    if (File.Exists(cachedPreview)) modInfo.PreviewUrl = cachedPreview;
                }
                if (string.IsNullOrWhiteSpace(modInfo.PreviewUrl) && archive.TryGetValue("preview_url", out var preview)) modInfo.PreviewUrl = preview?.ToString() ?? string.Empty;
                modInfo.Source = "TaikoModArchive";
            }
            catch
            {
                // A damaged archive cache entry should not hide the mod.
            }
        }
    }

    /// <summary>
    /// Data model for TekaTeka mods
    /// </summary>
    public class TekaTekaModInfo : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isModEnabled;

        public string ConfigPath { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Source { get; set; } = "External";
        public string PreviewUrl { get; set; } = string.Empty;
        public string LastUpdated { get; set; } = string.Empty;
        public string VersionDisplay =>
            string.Equals(Source, "TaikoModArchive", StringComparison.OrdinalIgnoreCase)
                ? string.IsNullOrWhiteSpace(LastUpdated)
                    ? "Last updated unavailable"
                    : $"Last updated {FormatArchiveTimestamp(LastUpdated)}"
                : $"Version {Version}";

        private static string FormatArchiveTimestamp(string timestamp) =>
            DateTimeOffset.TryParse(timestamp, out DateTimeOffset value)
                ? value.ToLocalTime().ToString("dd MMM yyyy, HH:mm")
                : timestamp;

        // NEW: the author property
        public string Author { get; set; }

        // If user can enable/disable this mod
        public bool CanEnable { get; set; }

        // Bound to the checkbox in the ListView
        public bool IsModEnabled
        {
            get => _isModEnabled;
            set
            {
                if (_isModEnabled == value) return;
                _isModEnabled = value;
                OnPropertyChanged();

                // Rewrite "enabled = true/false" in config
                UpdateEnabledInToml(value);
            }
        }

        private void UpdateEnabledInToml(bool newVal)
        {
            if (!File.Exists(ConfigPath)) return;
            try
            {
                var lines = File.ReadAllLines(ConfigPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"enabled = {newVal.ToString().ToLower()}";
                        break;
                    }
                }
                File.WriteAllLines(ConfigPath, lines);
            }
            catch
            {
                // handle errors as needed
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propName));
        }
    }
}
