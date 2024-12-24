// Updated BepInExConfigTab.cs
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TaikoModManager
{
    public class BepInExConfigTab
    {
        private readonly string configPath;
        private readonly StackPanel optionsPanel;
        private Dictionary<string, TextBox> settingEditors;

        public BepInExConfigTab(string configPath, StackPanel optionsPanel)
        {
            this.configPath = configPath;
            this.optionsPanel = optionsPanel;
            this.settingEditors = new Dictionary<string, TextBox>();
        }

        public void LoadConfig()
        {
            optionsPanel.Children.Clear();
            settingEditors.Clear();

            if (!File.Exists(configPath))
            {
                optionsPanel.Children.Add(new TextBlock
                {
                    Text = "BepInEx.cfg not found. Please ensure the file exists.",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 14,
                    Margin = new System.Windows.Thickness(5)
                });
                return;
            }

            string[] configLines = File.ReadAllLines(configPath);
            TextBlock currentHeader = null;

            foreach (string line in configLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    // Add spacing for clarity
                    optionsPanel.Children.Add(new TextBlock { Text = "", Height = 10 });
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentHeader = new TextBlock
                    {
                        Text = line,
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 14,
                        Margin = new System.Windows.Thickness(5, 10, 5, 5)
                    };
                    optionsPanel.Children.Add(currentHeader);
                    continue;
                }

                if (line.StartsWith("#"))
                {
                    optionsPanel.Children.Add(new TextBlock
                    {
                        Text = line.TrimStart('#').Trim(),
                        Foreground = System.Windows.Media.Brushes.LightGray,
                        FontSize = 12,
                        Margin = new System.Windows.Thickness(5, 0, 5, 5)
                    });
                    continue;
                }

                if (line.Contains("="))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    // Create a label and a text box for each setting
                    var label = new TextBlock
                    {
                        Text = key,
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 12,
                        Margin = new System.Windows.Thickness(5, 5, 5, 0)
                    };

                    var textBox = new TextBox
                    {
                        Text = value,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize = 12,
                        Background = System.Windows.Media.Brushes.Black,
                        Foreground = System.Windows.Media.Brushes.White,
                        Margin = new System.Windows.Thickness(5, 0, 5, 10)
                    };

                    optionsPanel.Children.Add(label);
                    optionsPanel.Children.Add(textBox);
                    settingEditors[key] = textBox;
                }
            }
        }

        public void SaveConfig()
        {
            if (settingEditors.Count == 0)
            {
                MessageBox.Show("No settings to save. Load the config file first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var lines = new List<string>();
                string currentSection = null;

                foreach (string line in File.ReadAllLines(configPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    {
                        lines.Add(line);
                        continue;
                    }

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line;
                        lines.Add(line);
                        continue;
                    }

                    if (line.Contains("="))
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        string key = parts[0].Trim();

                        if (settingEditors.ContainsKey(key))
                        {
                            string value = settingEditors[key].Text;
                            lines.Add($"{key} = {value}");
                        }
                        else
                        {
                            lines.Add(line); // Preserve unknown lines
                        }
                    }
                    else
                    {
                        lines.Add(line);
                    }
                }

                File.WriteAllLines(configPath, lines);
                MessageBox.Show("Config saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (IOException ex)
            {
                MessageBox.Show($"Error saving config file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
