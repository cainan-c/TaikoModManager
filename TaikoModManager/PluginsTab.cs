using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Tomlyn;
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
        /// Install a plugin from a GitHub repo URL, saving its .dll and the associated .toml.
        /// </summary>
        public async Task InstallPlugin(string githubRepoUrl)
        {
            try
            {
                var (repoName, repoDescription) = await FetchRepoInfo(githubRepoUrl);
                var (latestVersion, assetDownloadUrl, assetFileName) = await FetchLatestReleaseInfo(githubRepoUrl);

                string tempFilePath = Path.Combine(Path.GetTempPath(), assetFileName);
                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(assetDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    await using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);
                    await response.Content.CopyToAsync(fs);
                }

                // .zip or .dll
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
        /// Checks for plugin updates by comparing local vs. latest version via the GitHub API.
        /// </summary>
        public async Task CheckForUpdates(Action<string> logAction)
        {
            foreach (string dllFile in Directory.GetFiles(_pluginsPath, "*.dll"))
            {
                string dllName = Path.GetFileName(dllFile);
                string tomlPath = Path.Combine(_dataPath, dllName + ".toml");
                if (!File.Exists(tomlPath)) continue;

                var tomlString = File.ReadAllText(tomlPath);
                var tomlModel = Toml.ToModel(tomlString);

                if (!tomlModel.TryGetValue("plugin", out var pluginObj)
                    || pluginObj is not TomlTable pluginSection)
                    continue;

                if (!pluginSection.TryGetValue("repo_url", out var repoUrlObj))
                    continue;

                string repoUrl = repoUrlObj?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl == "No Repo URL")
                    continue;

                string localVersion = "";
                if (pluginSection.TryGetValue("version", out var versionObj))
                    localVersion = versionObj?.ToString() ?? "";

                if (string.IsNullOrEmpty(localVersion)) continue;

                try
                {
                    var (latestVersion, _, _) = await FetchLatestReleaseInfo(repoUrl);
                    if (!string.IsNullOrEmpty(latestVersion) && latestVersion != localVersion)
                    {
                        logAction?.Invoke($"Plugin '{dllName}' has an update! Local: {localVersion}, Latest: {latestVersion}");
                    }
                }
                catch (Exception ex)
                {
                    logAction?.Invoke($"CheckForUpdates error on '{dllName}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Returns a detailed list of installed plugins (reading from .toml files).
        /// </summary>
        public List<PluginInfo> LoadPluginsDetailed()
        {
            var results = new List<PluginInfo>();
            if (!Directory.Exists(_pluginsPath))
                return results;

            // If you need subfolders, change SearchOption to AllDirectories
            foreach (string dllPath in Directory.GetFiles(_pluginsPath, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var info = new PluginInfo();
                string dllName = Path.GetFileName(dllPath);
                info.DllName = dllName;
                info.Name = dllName;    // fallback
                info.Description = "";
                info.RepoUrl = "No Repo URL"; // fallback

                // check data/dll.toml
                string tomlPath = Path.Combine(_dataPath, dllName + ".toml");
                if (File.Exists(tomlPath))
                {
                    var tomlString = File.ReadAllText(tomlPath);
                    var tomlModel = Toml.ToModel(tomlString);

                    if (tomlModel.TryGetValue("plugin", out var pluginObj) && pluginObj is TomlTable pluginSec)
                    {
                        if (pluginSec.TryGetValue("name", out var nmObj))
                            info.Name = nmObj?.ToString() ?? dllName;
                        if (pluginSec.TryGetValue("description", out var descObj))
                            info.Description = descObj?.ToString() ?? "";
                        if (pluginSec.TryGetValue("repo_url", out var repoUrlObj))
                            info.RepoUrl = repoUrlObj?.ToString() ?? "No Repo URL";
                    }
                }

                // BepInEx/config check
                string baseName = Path.GetFileNameWithoutExtension(dllName); // e.g. "RF.TekaTeka"
                string possibleConfig = Path.Combine(_gamePath, "BepInEx", "config", baseName + ".cfg");
                if (File.Exists(possibleConfig))
                {
                    info.IsConfigPresent = true;
                    info.ConfigPath = possibleConfig;
                    try
                    {
                        var lines = File.ReadAllLines(possibleConfig);
                        foreach (var line in lines)
                        {
                            if (line.Trim().StartsWith("Enabled", StringComparison.OrdinalIgnoreCase))
                            {
                                if (line.ToLower().Contains("true")) info.IsPluginEnabled = true;
                                else info.IsPluginEnabled = false;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // ignore config read errors
                    }
                }
                else
                {
                    info.IsConfigPresent = false;
                    info.IsPluginEnabled = false;
                }

                results.Add(info);
            }
            return results;
        }

        #region Internals

        /// <summary>
        /// Retrieve repository info (name, description) from the GitHub API.
        /// </summary>
        private async Task<(string repoName, string repoDescription)> FetchRepoInfo(string githubRepoUrl)
        {
            var (owner, repo) = ParseOwnerAndRepo(githubRepoUrl);
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}";
            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");
            string resp = await client.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(resp);
            string name = doc.RootElement.GetProperty("name").GetString();
            string desc = doc.RootElement.GetProperty("description").GetString();
            return (name ?? "Unknown", desc ?? "No description provided");
        }

        /// <summary>
        /// Retrieve the latest release info (tag name, asset URL, asset name) from the GitHub API.
        /// </summary>
        private async Task<(string tagName, string assetUrl, string assetName)> FetchLatestReleaseInfo(string githubRepoUrl)
        {
            var (owner, repo) = ParseOwnerAndRepo(githubRepoUrl);
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");
            string json = await client.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(json);
            string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "UnknownVersion";

            if (!doc.RootElement.TryGetProperty("assets", out JsonElement assets))
                throw new Exception("No assets found in latest release JSON!");

            foreach (var asset in assets.EnumerateArray())
            {
                string aname = asset.GetProperty("name").GetString();
                string url = asset.GetProperty("browser_download_url").GetString();

                if (aname.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    aname.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return (tagName, url, aname);
                }
            }
            throw new Exception("No .dll or .zip asset in latest release!");
        }

        /// <summary>
        /// Extract a .zip, then copy any .dll files into BepInEx\plugins and generate metadata files.
        /// </summary>
        private void ExtractAndInstallZip(string zipFilePath, string repoName, string repoDesc, string repoUrl, string version)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            try
            {
                ZipFile.ExtractToDirectory(zipFilePath, tempDir);
                foreach (string dll in Directory.GetFiles(tempDir, "*.dll", SearchOption.AllDirectories))
                {
                    InstallDll(dll, repoName, repoDesc, repoUrl, version);
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
                if (File.Exists(zipFilePath)) File.Delete(zipFilePath);
            }
        }

        /// <summary>
        /// Copies a single .dll to BepInEx\plugins, then creates/updates its .toml metadata file.
        /// </summary>
        private void InstallDll(string dllPath, string repoName, string repoDesc, string repoUrl, string version)
        {
            string dllName = Path.GetFileName(dllPath);
            string dest = Path.Combine(_pluginsPath, dllName);
            File.Copy(dllPath, dest, overwrite: true);

            if (File.Exists(dllPath))
            {
                try { File.Delete(dllPath); } catch { /* ignore */ }
            }

            string tomlFile = Path.Combine(_dataPath, dllName + ".toml");
            if (!File.Exists(tomlFile))
            {
                CreateMetadataFile(dest, repoName, repoDesc, repoUrl, version);
            }
            else
            {
                // (Optional) If the file already exists, you could update version/URL, etc.
            }
        }

        private void CreateMetadataFile(string dllFullPath, string pluginName, string pluginDesc, string repoUrl, string version)
        {
            string dllName = Path.GetFileName(dllFullPath);
            string tomlFile = Path.Combine(_dataPath, dllName + ".toml");
            if (File.Exists(tomlFile)) return; // skip if file already exists

            var pluginSec = new TomlTable
            {
                ["name"] = pluginName,
                ["description"] = pluginDesc,
                ["repo_url"] = repoUrl,
                ["version"] = version
            };
            var meta = new TomlTable { ["plugin"] = pluginSec };

            File.WriteAllText(tomlFile, Toml.FromModel(meta));
        }

        /// <summary>
        /// Converts a GitHub URL to (owner, repo). For example:
        ///   https://github.com/OWNER/REPO
        ///   https://github.com/OWNER/REPO/releases
        /// We'll just strip anything after the first 2 parts (OWNER and REPO).
        /// </summary>
        private (string owner, string repo) ParseOwnerAndRepo(string githubUrl)
        {
            // Remove trailing slash
            string trimmed = githubUrl.TrimEnd('/');

            // Look for "github.com/"
            int idx = trimmed.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                throw new InvalidOperationException("Invalid GitHub URL: " + githubUrl);

            // Substring after "github.com/"
            trimmed = trimmed.Substring(idx + "github.com/".Length);

            // Split on '/'
            var parts = trimmed.Split('/');
            if (parts.Length < 2)
                throw new InvalidOperationException("Invalid GitHub URL: " + githubUrl);

            string owner = parts[0];
            string repository = parts[1];

            return (owner, repository);
        }

        /// <summary>
        /// Update all plugins that have a different version in the latest release than locally.
        /// </summary>
        public async Task UpdateAllPluginsAsync(Action<string> logAction)
        {
            string[] tomlFiles = Directory.GetFiles(_dataPath, "*.toml", SearchOption.TopDirectoryOnly);
            foreach (string f in tomlFiles)
            {
                try
                {
                    var tomlStr = File.ReadAllText(f);
                    var tomlModel = Toml.ToModel(tomlStr);

                    if (!tomlModel.TryGetValue("plugin", out var pluginObj)
                        || pluginObj is not TomlTable pluginSec) continue;

                    if (!pluginSec.TryGetValue("repo_url", out var repoObj)) continue;
                    string repoUrl = repoObj?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl == "No Repo URL") continue;

                    string localVer = "";
                    if (pluginSec.TryGetValue("version", out var verObj))
                        localVer = verObj?.ToString() ?? "";

                    var (latestVer, _, _) = await FetchLatestReleaseInfo(repoUrl);
                    if (string.IsNullOrWhiteSpace(latestVer)
                        || latestVer.Equals("UnknownVersion", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!latestVer.Equals(localVer, StringComparison.OrdinalIgnoreCase))
                    {
                        logAction($"Updating plugin from {localVer} to {latestVer} for repo: {repoUrl}");
                        await InstallPlugin(repoUrl);
                        logAction($"Updated plugin (repo: {repoUrl}) to version {latestVer}.");
                    }
                }
                catch (Exception ex)
                {
                    logAction($"Error updating plugin via {f}: {ex.Message}");
                }
            }
            logAction("Plugin update check complete.");
        }

        #endregion
    }
}
