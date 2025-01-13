using Microsoft.Win32;
using System;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace TaikoModManager
{
    public static class ProtocolRegistration
    {
        /// <summary>
        /// Checks if "taikomodmanager:" is registered under HKCU,
        /// and if not (or if out-of-date), updates it to point to the given exePath.
        /// </summary>
        public static void EnsureRegistered(string exeFullPath)
        {
            // Our protocol name is "taikomodmanager"
            const string PROTOCOL = "taikomodmanager";
            string baseKeyPath = $@"Software\Classes\{PROTOCOL}";

            try
            {
                // We'll open the base key. If it doesn't exist, we'll create it.
                using (var baseKey = Registry.CurrentUser.OpenSubKey(baseKeyPath, writable: true)
                                    ?? Registry.CurrentUser.CreateSubKey(baseKeyPath))
                {
                    if (baseKey == null)
                    {
                        MessageBox.Show($"Could not open or create registry key for '{PROTOCOL}'.",
                                        "Protocol Registration Error",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Error);
                        return;
                    }

                    // Check existing value
                    // (default) = "URL: Taiko Mod Manager"
                    // "URL Protocol" = "" means Windows treats it as a URL scheme
                    string currentExePath = GetCurrentCommand(baseKey);
                    bool needsUpdate = string.IsNullOrEmpty(currentExePath)
                                       || !currentExePath.Equals(exeFullPath, StringComparison.OrdinalIgnoreCase);

                    if (!needsUpdate)
                    {
                        // Already pointing to the correct exe path
                        return;
                    }

                    // Otherwise, we re-register or update
                    baseKey.SetValue("", "URL: Taiko Mod Manager");
                    baseKey.SetValue("URL Protocol", "");

                    // Optional: Set default icon
                    using (var iconKey = baseKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey?.SetValue("", $"\"{exeFullPath}\",0");
                    }

                    using (var shellKey = baseKey.CreateSubKey("shell"))
                    {
                        shellKey?.SetValue("", "open");
                        using (var openKey = shellKey.CreateSubKey("open"))
                        {
                            using (var commandKey = openKey.CreateSubKey("command"))
                            {
                                // Important: wrap exe path in quotes, then \"%1\" for the URL
                                commandKey?.SetValue("", $"\"{exeFullPath}\" \"%1\"");
                            }
                        }
                    }

                    MessageBox.Show("Successfully registered taikomodmanager: protocol under HKCU!",
                                    "Protocol Registered",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to register taikomodmanager protocol:\n{ex.Message}",
                                "Protocol Registration Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Returns the EXE path from the "command" subkey (if any).
        /// If missing or invalid, returns null/empty.
        /// </summary>
        private static string GetCurrentCommand(RegistryKey baseKey)
        {
            // shell\open\command => "C:\Path\To\EXE" "%1"
            using (var shellKey = baseKey.OpenSubKey("shell"))
            {
                using (var openKey = shellKey?.OpenSubKey("open"))
                {
                    using (var cmdKey = openKey?.OpenSubKey("command"))
                    {
                        if (cmdKey == null) return null;
                        var value = cmdKey.GetValue("") as string;
                        if (string.IsNullOrEmpty(value)) return null;

                        // Typically looks like:  "C:\Path\To\TaikoModManager.exe" "%1"
                        // Let's parse out the path before the "%1"
                        // We'll just do a rough trim, ignoring advanced edge cases
                        int idx = value.IndexOf("\" \"%1\"", StringComparison.OrdinalIgnoreCase);
                        if (idx > 0)
                        {
                            // e.g. "C:\My Path\TaikoModManager.exe"
                            return value.Substring(0, idx).Trim(' ', '"');
                        }
                        return null;
                    }
                }
            }
        }
    }
}
