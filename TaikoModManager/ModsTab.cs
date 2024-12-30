using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;    // For MessageBox, if desired
using Tomlyn;           // For Toml parsing (adjust if you have a different TOML library)
using Tomlyn.Model;

namespace TaikoModManager
{
    public class ModsTab
    {
        private readonly string _gamePath;
        private readonly string _tekaTekaDllPath;    // BepInEx/plugins/RF.TekaTeka.dll
        private readonly string _tekaSongsPath;      // [GameDir]/TekaSongs

        public ModsTab(string gamePath)
        {
            _gamePath = gamePath;
            _tekaTekaDllPath = Path.Combine(gamePath, "BepInEx", "plugins", "RF.TekaTeka", "RF.TekaTeka.dll");
            _tekaSongsPath = Path.Combine(gamePath, "TekaSongs");
        }

        /// <summary>
        /// Checks if TekaTeka is installed (RF.TekaTeka.dll exists).
        /// </summary>
        public bool IsTekaTekaInstalled()
        {
            return File.Exists(_tekaTekaDllPath);
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
        /// enabled = true/false
        /// name = "..."
        /// version = "..."
        /// description = "..."
        /// </summary>
        private TekaTekaModInfo ParseModConfig(string configPath)
        {
            var modInfo = new TekaTekaModInfo
            {
                ConfigPath = configPath,
                // If TekaTeka is installed, we can enable/disable
                // but you can fine-tune this logic
                CanEnable = IsTekaTekaInstalled()
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
            }
            catch
            {
                // If there's a parse error, we fallback
                modInfo.Name = Path.GetFileNameWithoutExtension(configPath);
            }

            return modInfo;
        }
    }

    /// <summary>
    /// Data model for TekaTeka mods, similar to PluginInfo for BepInEx plugins.
    /// </summary>
    public class TekaTekaModInfo : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isModEnabled;

        public string ConfigPath { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }

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
