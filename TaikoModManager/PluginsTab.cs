using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;               // For MessageBox
using Tomlyn;                      // For Toml parsing/generation
using Tomlyn.Model;
using System.Collections.Generic;

namespace TaikoModManager
{
    public class PluginsTab
    {
        private readonly string _gamePath;
        private readonly string _pluginsPath;
        private readonly string _dataPath;

        public PluginsTab(string gamePath)
        {
            _gamePath = gamePath;
            _pluginsPath = Path.Combine(_gamePath, "BepInEx", "plugins");
            _dataPath = Path.Combine(AppContext.BaseDirectory, "data");

            Directory.CreateDirectory(_pluginsPath);
            Directory.CreateDirectory(_dataPath);
        }

        /// <summary>
        /// Installs a plugin from a user-provided GitHub repository URL.
        /// Fetches repo/release info, downloads the .dll or .zip,
        /// and copies it to BepInEx/plugins with a matching .toml.
        /// </summary>
        public async Task InstallPlugin(string githubRepoUrl)
        {
            try
            {
                // 1) Fetch the repo info (name, description) from /repos/{owner}/{repo}
                var (repoName, repoDescription) = await FetchRepoInfo(githubRepoUrl);

                // 2) Fetch latest release info (version, direct asset URL, original asset name)
                var (latestVersion, assetDownloadUrl, assetFileName) = await FetchLatestReleaseInfo(githubRepoUrl);

                // 3) Download the asset to a temp file *using the original file name*
                string tempFilePath = Path.Combine(Path.GetTempPath(), assetFileName);
                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(assetDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    await using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);
                    await response.Content.CopyToAsync(fs);
                }

                // 4) Install
                if (tempFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractAndInstallZip(tempFilePath, repoName, repoDescription, githubRepoUrl, latestVersion);
                }
                else if (tempFilePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    InstallDll(tempFilePath, repoName, repoDescription, githubRepoUrl, latestVersion);
                }
                else
                {
                    throw new InvalidOperationException($"Unknown file type: {assetDownloadUrl}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error installing plugin: {ex.Message}",
                                "Plugin Installation Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Check each plugin's .toml for a repo_url + version; if there's an update, log/notify.
        /// </summary>
        public async Task CheckForUpdates(Action<string> logAction)
        {
            foreach (string dllFile in Directory.GetFiles(_pluginsPath, "*.dll"))
            {
                string dllName = Path.GetFileName(dllFile);
                string tomlPath = Path.Combine(_dataPath, dllName + ".toml");

                if (!File.Exists(tomlPath))
                    continue; // no metadata => skip

                var tomlString = File.ReadAllText(tomlPath);
                var tomlModel = Toml.ToModel(tomlString);

                if (!tomlModel.TryGetValue("plugin", out var pluginObj) ||
                    pluginObj is not TomlTable pluginSection)
                    continue;

                // See if there's a repo_url
                if (!pluginSection.TryGetValue("repo_url", out var repoUrlObj))
                    continue;

                string repoUrl = repoUrlObj?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl == "No Repo URL")
                    continue;

                // See if there's a local version
                string localVersion = "";
                if (pluginSection.TryGetValue("version", out var versionObj))
                    localVersion = versionObj?.ToString() ?? "";

                if (string.IsNullOrEmpty(localVersion))
                {
                    // We can't compare if no version in the .toml
                    continue;
                }

                // Fetch the latest release version from GitHub
                try
                {
                    var (latestVersion, _, _) = await FetchLatestReleaseInfo(repoUrl);
                    if (!string.IsNullOrEmpty(latestVersion) && latestVersion != localVersion)
                    {
                        logAction?.Invoke($"Plugin '{dllName}' has an update! Local version: {localVersion}, Latest: {latestVersion}");
                    }
                }
                catch (Exception ex)
                {
                    // Possibly a network error or no releases found
                    logAction?.Invoke($"CheckForUpdates error on '{dllName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Returns a list of PluginInfo objects (Name + Description) for display in the UI.
        /// </summary>
        public List<PluginInfo> LoadPluginsDetailed()
        {
            var results = new List<PluginInfo>();
            if (!Directory.Exists(_pluginsPath))
                return results;

            foreach (string dllPath in Directory.GetFiles(_pluginsPath, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var info = new PluginInfo();
                string dllName = Path.GetFileName(dllPath);
                info.DllName = dllName;

                // We'll store some default fallback
                info.Name = dllName;
                info.Description = "";

                // 1) Look for .toml metadata to get nicer name/desc
                string tomlPath = Path.Combine(_dataPath, dllName + ".toml");
                if (File.Exists(tomlPath))
                {
                    var tomlString = File.ReadAllText(tomlPath);
                    var tomlModel = Toml.ToModel(tomlString);

                    if (tomlModel.TryGetValue("plugin", out var pluginObj) && pluginObj is TomlTable pluginSection)
                    {
                        if (pluginSection.TryGetValue("name", out var pluginNameObj))
                            info.Name = pluginNameObj?.ToString() ?? dllName;

                        if (pluginSection.TryGetValue("description", out var pluginDescObj))
                            info.Description = pluginDescObj?.ToString() ?? "";
                    }
                }

                // 2) Check if there's a config file in BepInEx/config
                //    For example, if the DLL is "RF.TekaTeka.dll", config might be "RF.TekaTeka.cfg"
                string baseName = Path.GetFileNameWithoutExtension(dllName); // e.g. "RF.TekaTeka"
                string possibleConfig = Path.Combine(_gamePath, "BepInEx", "config", baseName + ".cfg");
                if (File.Exists(possibleConfig))
                {
                    info.IsConfigPresent = true;
                    info.ConfigPath = possibleConfig;

                    // 3) Attempt to parse "Enabled = true/false"
                    try
                    {
                        var lines = File.ReadAllLines(possibleConfig);
                        foreach (var line in lines)
                        {
                            if (line.Trim().StartsWith("Enabled", StringComparison.OrdinalIgnoreCase))
                            {
                                // e.g. "Enabled = true"
                                if (line.ToLower().Contains("true"))
                                    info.IsPluginEnabled = true;
                                else
                                    info.IsPluginEnabled = false;

                                break;
                            }
                        }
                    }
                    catch
                    {
                        // If there's an error reading, we can ignore or log
                    }
                }
                else
                {
                    info.IsConfigPresent = false;
                    info.IsPluginEnabled = false; // no config => default to false
                }

                results.Add(info);
            }

            return results;
        }


        #region Internals

        /// <summary>
        /// Fetch repository name + description from /repos/{owner}/{repo}
        /// </summary>
        private async Task<(string repoName, string repoDescription)> FetchRepoInfo(string githubRepoUrl)
        {
            var (owner, repo) = ParseOwnerAndRepo(githubRepoUrl);
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}";

            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");

            string responseBody = await client.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(responseBody);

            string name = doc.RootElement.GetProperty("name").GetString();
            string description = doc.RootElement.GetProperty("description").GetString();

            return (name ?? "Unknown", description ?? "No description provided");
        }

        /// <summary>
        /// Fetch the latest release from /repos/{owner}/{repo}/releases/latest
        /// and return (version, assetDownloadUrl, assetFileName).
        /// </summary>
        private async Task<(string tagName, string assetUrl, string assetName)> FetchLatestReleaseInfo(string githubRepoUrl)
        {
            var (owner, repo) = ParseOwnerAndRepo(githubRepoUrl);
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");

            string json = await client.GetStringAsync(apiUrl);
            using JsonDocument doc = JsonDocument.Parse(json);

            string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "UnknownVersion";

            // Assets array
            if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets))
                throw new Exception("No assets found in the latest release!");

            // We look for a .dll or .zip file
            foreach (var asset in assets.EnumerateArray())
            {
                string assetName = asset.GetProperty("name").GetString();
                string downloadUrl = asset.GetProperty("browser_download_url").GetString();

                if (assetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return (tagName, downloadUrl, assetName);
                }
            }

            throw new Exception("No .dll or .zip asset found in the latest release!");
        }

        /// <summary>
        /// Extract the .zip, install all .dll files found, then remove the temp folder.
        /// </summary>
        private void ExtractAndInstallZip(string zipFilePath, string repoName, string repoDescription,
            string repoUrl, string version)
        {
            string extractionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(extractionPath);

            try
            {
                ZipFile.ExtractToDirectory(zipFilePath, extractionPath);

                foreach (string dllFile in Directory.GetFiles(extractionPath, "*.dll", SearchOption.AllDirectories))
                {
                    InstallDll(dllFile, repoName, repoDescription, repoUrl, version);
                }
            }
            finally
            {
                Directory.Delete(extractionPath, true);
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                }
            }
        }

        /// <summary>
        /// Copies a single .dll into BepInEx/plugins and creates a .toml if missing.
        /// </summary>
        private void InstallDll(string dllPath, string repoName, string repoDescription, string repoUrl, string version)
        {
            string dllName = Path.GetFileName(dllPath);
            string destinationPath = Path.Combine(_pluginsPath, dllName);

            File.Copy(dllPath, destinationPath, overwrite: true);

            if (File.Exists(dllPath))
            {
                try { File.Delete(dllPath); } catch { /* ignore */ }
            }

            // If there's no toml, create a new one with the metadata
            string tomlFile = Path.Combine(_dataPath, dllName + ".toml");
            if (!File.Exists(tomlFile))
            {
                CreateMetadataFile(destinationPath, repoName, repoDescription, repoUrl, version);
            }
            // else optionally update the existing .toml
        }

        private void CreateMetadataFile(string dllFullPath, string pluginName, string pluginDescription, string repoUrl, string version)
        {
            string dllName = Path.GetFileName(dllFullPath);
            string tomlFile = Path.Combine(_dataPath, dllName + ".toml");
            if (File.Exists(tomlFile)) return;

            var metadata = new TomlTable();
            var pluginSection = new TomlTable
            {
                ["name"] = pluginName,
                ["description"] = pluginDescription,
                ["repo_url"] = repoUrl,
                ["version"] = version
            };
            metadata["plugin"] = pluginSection;

            File.WriteAllText(tomlFile, Toml.FromModel(metadata));
        }

        /// <summary>
        /// Helper to parse {owner}/{repo} from "https://github.com/owner/repo".
        /// </summary>
        private (string owner, string repo) ParseOwnerAndRepo(string githubRepoUrl)
        {
            var parts = githubRepoUrl.TrimEnd('/').Split('/');
            if (parts.Length < 2)
                throw new InvalidOperationException("Invalid GitHub URL: " + githubRepoUrl);

            string repo = parts[^1];
            string owner = parts[^2];
            return (owner, repo);
        }
        public async Task UpdateAllPluginsAsync(Action<string> logAction)
        {
            // 1) Iterate all .toml files in the data folder 
            //    (one per plugin, e.g. "RF.TekaTeka.dll.toml")
            string[] tomlFiles = Directory.GetFiles(_dataPath, "*.toml", SearchOption.TopDirectoryOnly);

            foreach (string tomlPath in tomlFiles)
            {
                try
                {
                    var tomlContent = File.ReadAllText(tomlPath);
                    var tomlModel = Toml.ToModel(tomlContent);

                    // Ensure we have a [plugin] section
                    if (!tomlModel.TryGetValue("plugin", out var pluginObj)
                        || pluginObj is not TomlTable pluginSection)
                    {
                        continue;
                    }

                    // Extract repo_url and version
                    if (!pluginSection.TryGetValue("repo_url", out var repoObj))
                        continue; // no repo => can't update

                    string repoUrl = repoObj?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl == "No Repo URL")
                        continue; // skip if invalid

                    string localVersion = "";
                    if (pluginSection.TryGetValue("version", out var versionObj))
                        localVersion = versionObj?.ToString() ?? "";

                    // 2) Check if the remote version is newer
                    var (latestVersion, _, _) = await FetchLatestReleaseInfo(repoUrl);
                    if (string.IsNullOrWhiteSpace(latestVersion)
                        || latestVersion.Equals("UnknownVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // Can't determine latest version => skip
                    }

                    // If they differ, we attempt an update
                    if (!latestVersion.Equals(localVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        logAction?.Invoke(
                            $"Updating plugin from {localVersion} to {latestVersion} for repo: {repoUrl}"
                        );

                        // 3) Reinstall the plugin from the same GitHub repo
                        await InstallPlugin(repoUrl);

                        logAction?.Invoke($"Updated plugin (repo: {repoUrl}) to version {latestVersion}.");
                    }
                }
                catch (Exception ex)
                {
                    logAction?.Invoke($"Error updating plugin via {tomlPath}: {ex.Message}");
                }
            }

            logAction?.Invoke("Plugin update check complete.");
        }
        #endregion
    }
}
