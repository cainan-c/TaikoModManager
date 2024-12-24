using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaikoModManager
{
    public class PluginInfo : INotifyPropertyChanged
    {
        private bool _isPluginEnabled;
        private bool _isConfigPresent;

        // e.g., "RF.TekaTeka.dll"
        public string DllName { get; set; }

        // e.g., "TekaTeka"
        public string Name { get; set; }

        public string Description { get; set; }

        // Points to the "config/RF.TekaTeka.cfg" file if it exists
        public string ConfigPath { get; set; }

        public bool IsConfigPresent
        {
            get => _isConfigPresent;
            set
            {
                _isConfigPresent = value;
                OnPropertyChanged();
            }
        }

        // Bound to the checkbox in the XAML
        public bool IsPluginEnabled
        {
            get => _isPluginEnabled;
            set
            {
                if (_isPluginEnabled == value)
                    return;

                _isPluginEnabled = value;
                OnPropertyChanged();

                // Whenever user toggles, we update the plugin's config file
                if (IsConfigPresent && !string.IsNullOrEmpty(ConfigPath))
                {
                    UpdateEnabledInConfigFile(_isPluginEnabled);
                }
            }
        }

        // Minimal approach: rewrite "Enabled = true/false" in the config file
        private void UpdateEnabledInConfigFile(bool newValue)
        {
            try
            {
                // 1) Read all lines
                var lines = System.IO.File.ReadAllLines(ConfigPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    // If line starts with "Enabled"...
                    if (lines[i].Trim().StartsWith("Enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        // Replace with "Enabled = true" or "Enabled = false"
                        lines[i] = $"Enabled = {newValue.ToString().ToLower()}";
                        break;
                    }
                }

                // 2) Write updated lines
                System.IO.File.WriteAllLines(ConfigPath, lines);
            }
            catch
            {
                // In production, handle errors (e.g., display a message)
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
