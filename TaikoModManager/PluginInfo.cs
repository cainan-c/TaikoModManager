using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaikoModManager
{
    public class PluginInfo : INotifyPropertyChanged
    {
        private bool _isPluginEnabled;
        private bool _isConfigPresent;
        private string _version;
        private string _author;

        public string DllName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        // e.g., "BepInEx/config/RF.TekaTeka.cfg"
        public string ConfigPath { get; set; }

        // The GitHub repository URL from .toml
        private string _repoUrl;
        public string RepoUrl
        {
            get => _repoUrl;
            set
            {
                _repoUrl = value;
                OnPropertyChanged();
            }
        }

        public string Version
        {
            get => _version;
            set
            {
                _version = value;
                OnPropertyChanged();
            }
        }

        public string Author
        {
            get => _author;
            set
            {
                _author = value;
                OnPropertyChanged();
            }
        }

        public bool IsConfigPresent
        {
            get => _isConfigPresent;
            set
            {
                _isConfigPresent = value;
                OnPropertyChanged();
            }
        }

        public bool IsPluginEnabled
        {
            get => _isPluginEnabled;
            set
            {
                if (_isPluginEnabled == value) return;
                _isPluginEnabled = value;
                OnPropertyChanged();

                // If the config file is found, update "Enabled = true/false" automatically
                if (IsConfigPresent && !string.IsNullOrEmpty(ConfigPath))
                {
                    UpdateEnabledInConfigFile(_isPluginEnabled);
                }
            }
        }

        /// <summary>
        /// Updates the "Enabled" line in the plugin's config file (if found).
        /// </summary>
        private void UpdateEnabledInConfigFile(bool newValue)
        {
            try
            {
                var lines = System.IO.File.ReadAllLines(ConfigPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Trim().StartsWith("Enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"Enabled = {newValue.ToString().ToLower()}";
                        break;
                    }
                }
                System.IO.File.WriteAllLines(ConfigPath, lines);
            }
            catch
            {
                // handle errors if needed
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
