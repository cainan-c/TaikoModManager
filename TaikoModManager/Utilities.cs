using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace TaikoModManager
{
    public static class Utilities
    {
        public class ConfigSetting
        {
            public string Key { get; set; }
            public string CurrentValue { get; set; }
            public string Description { get; set; }
            public object UIElement { get; set; }
        }

        public static string DetectGamePath()
        {
            string steamConfigPath = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
            string taikoFolderName = @"steamapps\common\Taiko no Tatsujin Rhythm Festival";

            if (!File.Exists(steamConfigPath)) return null;

            foreach (var line in File.ReadLines(steamConfigPath))
            {
                if (line.Contains("path"))
                {
                    var match = Regex.Match(line, "\"path\"\\s*\"([^\"]+)\"");
                    if (match.Success)
                    {
                        var gamePath = Path.Combine(match.Groups[1].Value, taikoFolderName);
                        if (Directory.Exists(gamePath))
                        {
                            return gamePath;
                        }
                    }
                }
            }

            return null;
        }

        public static Dictionary<string, List<ConfigSetting>> ParseConfigFile(string path)
        {
            var sections = new Dictionary<string, List<ConfigSetting>>();
            string currentSection = null;
            List<string> comments = new List<string>();

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    comments.Clear();
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Trim('[', ']');
                    sections[currentSection] = new List<ConfigSetting>();
                    comments.Clear();
                }
                else if (line.StartsWith("#"))
                {
                    comments.Add(line.TrimStart('#').Trim());
                }
                else if (line.Contains("="))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length == 2 && currentSection != null)
                    {
                        sections[currentSection].Add(new ConfigSetting
                        {
                            Key = parts[0].Trim(),
                            CurrentValue = parts[1].Trim(),
                            Description = string.Join(Environment.NewLine, comments)
                        });
                    }

                    comments.Clear();
                }
            }

            return sections;
        }

        public static void WriteConfigFile(string path, Dictionary<string, List<ConfigSetting>> sections)
        {
            using (var writer = new StreamWriter(path))
            {
                foreach (var section in sections)
                {
                    writer.WriteLine($"[{section.Key}]");

                    foreach (var setting in section.Value)
                    {
                        if (!string.IsNullOrEmpty(setting.Description))
                        {
                            foreach (var line in setting.Description.Split(new[] { Environment.NewLine }, StringSplitOptions.None))
                            {
                                writer.WriteLine($"# {line}");
                            }
                        }

                        writer.WriteLine($"{setting.Key}={setting.CurrentValue}");
                    }

                    writer.WriteLine();
                }
            }
        }
    }
}
