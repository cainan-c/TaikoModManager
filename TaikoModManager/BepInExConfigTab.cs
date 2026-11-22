using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaikoModManager
{
    /// <summary>
    /// Builds and saves a BepInEx-style INI configuration editor while preserving
    /// the source file's section, comment, and line ordering.
    /// </summary>
    public class BepInExConfigTab
    {
        private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        private static readonly Brush FieldBrush = new SolidColorBrush(Color.FromRgb(24, 24, 24));
        private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(66, 66, 66));
        private static readonly Brush PrimaryTextBrush = new SolidColorBrush(Color.FromRgb(242, 242, 242));
        private static readonly Brush SecondaryTextBrush = new SolidColorBrush(Color.FromRgb(184, 184, 184));

        private readonly string configPath;
        private readonly StackPanel optionsPanel;
        private readonly Dictionary<string, ConfigSettingEditor> settingEditors = new();

        public BepInExConfigTab(string configPath, StackPanel optionsPanel)
        {
            this.configPath = configPath;
            this.optionsPanel = optionsPanel;
        }

        public void LoadConfig()
        {
            optionsPanel.Children.Clear();
            settingEditors.Clear();

            if (!File.Exists(configPath))
            {
                optionsPanel.Children.Add(new TextBlock
                {
                    Text = "Config file not found.",
                    Foreground = PrimaryTextBrush,
                    FontSize = 14,
                    Margin = new Thickness(5)
                });
                return;
            }

            var currentSection = string.Empty;
            foreach (var rawLine in File.ReadAllLines(configPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    optionsPanel.Children.Add(new Border { Height = 7 });
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line[1..^1].Trim();
                    AddSectionHeader(currentSection);
                    continue;
                }

                if (line.StartsWith("#") || line.StartsWith(";"))
                {
                    AddComment(line[1..].Trim());
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    AddComment(line);
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                AddSetting(currentSection, key, value);
            }
        }

        public bool SaveConfig(bool showConfirmation = true)
        {
            if (settingEditors.Count == 0)
            {
                if (showConfirmation)
                {
                    MessageBox.Show("No settings to save. Load the config file first.", "No settings", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return false;
            }

            try
            {
                var savedLines = new List<string>();
                var currentSection = string.Empty;

                foreach (var rawLine in File.ReadAllLines(configPath))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line[1..^1].Trim();
                        savedLines.Add(rawLine);
                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        savedLines.Add(rawLine);
                        continue;
                    }

                    var key = line[..separatorIndex].Trim();
                    if (settingEditors.TryGetValue(CreateSettingId(currentSection, key), out var editor))
                    {
                        var originalKey = rawLine[..rawLine.IndexOf('=')].TrimEnd();
                        savedLines.Add($"{originalKey} = {editor.Value}");
                    }
                    else
                    {
                        savedLines.Add(rawLine);
                    }
                }

                File.WriteAllLines(configPath, savedLines);
                if (showConfirmation)
                {
                    MessageBox.Show("Config saved successfully!", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (showConfirmation)
                {
                    MessageBox.Show($"Error saving config file: {ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return false;
            }
        }

        private void AddSectionHeader(string section)
        {
            optionsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 12, 0, 5),
                Child = new TextBlock
                {
                    Text = section,
                    Foreground = PrimaryTextBrush,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold
                }
            });
        }

        private void AddComment(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            optionsPanel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = SecondaryTextBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 2, 8, 1)
            });
        }

        private void AddSetting(string section, string key, string value)
        {
            var isBoolean = bool.TryParse(value, out var booleanValue);
            var isQuotedString = !isBoolean && value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\"");
            var editorValue = isQuotedString ? UnescapeTomlString(value[1..^1]) : value;
            FrameworkElement editorControl;
            var content = new StackPanel();

            if (isBoolean)
            {
                var checkBox = new CheckBox
                {
                    Content = key,
                    IsChecked = booleanValue,
                    Foreground = PrimaryTextBrush,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                editorControl = checkBox;
                content.Children.Add(checkBox);
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = key,
                    Foreground = PrimaryTextBrush,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 6)
                });

                var textBox = new TextBox
                {
                    Text = editorValue,
                    Background = FieldBrush,
                    Foreground = PrimaryTextBrush,
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13
                };
                editorControl = textBox;
                content.Children.Add(textBox);
            }

            optionsPanel.Children.Add(new Border
            {
                Background = PanelBrush,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 3, 0, 3),
                Child = content
            });

            settingEditors[CreateSettingId(section, key)] = new ConfigSettingEditor(editorControl, isBoolean, isQuotedString);
        }

        private static string CreateSettingId(string section, string key) => $"{section}\u001f{key}";

        private static string UnescapeTomlString(string value) => value
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");

        private sealed class ConfigSettingEditor
        {
            private readonly FrameworkElement editor;
            private readonly bool isBoolean;
            private readonly bool isQuotedString;

            public ConfigSettingEditor(FrameworkElement editor, bool isBoolean, bool isQuotedString)
            {
                this.editor = editor;
                this.isBoolean = isBoolean;
                this.isQuotedString = isQuotedString;
            }

            public string Value
            {
                get
                {
                    if (isBoolean)
                    {
                        return ((CheckBox)editor).IsChecked == true ? "true" : "false";
                    }

                    var value = ((TextBox)editor).Text;
                    return isQuotedString
                        ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
                        : value;
                }
            }
        }
    }
}
