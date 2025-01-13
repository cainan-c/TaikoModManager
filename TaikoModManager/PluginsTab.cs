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
                var (repoName, repoDescription, repoAuthor) = await FetchRepoInfo(githubRepoUrl);
                var (latestVersion, assetDownloadUrl, assetFileName) = await FetchLatestReleaseInfo(githubRepoUrl);

                string tempFilePath = Path.Combine(Path.GetTempPath(), assetFileName);
                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(assetDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    await using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);
                    await response.Content.CopyToAsync(fs);
                }

                // Handle .zip or .dll
                if (tempFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractAndInstallZip(tempFilePath, repoName, repoDescription, repoAuthor, githubRepoUrl, latestVersion);
                }
                else if (tempFilePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    string dllName = Path.GetFileName(tempFilePath);
                    string dllNameLower = dllName.ToLowerInvariant();

                    bool matchesRfOrCom = (dllNameLower.StartsWith("rf.") ||
                                           dllNameLower.StartsWith("com.") ||
                                           dllNameLower.Contains("rf"));

                    string folderName = matchesRfOrCom
                        ? Path.GetFileNameWithoutExtension(dllName)
                        : repoName;

                    string pluginFolder = Path.Combine(_pluginsPath, folderName);
                    Directory.CreateDirectory(pluginFolder);

                    InstallDll(tempFilePath, pluginFolder, folderName, repoDescription, repoAuthor, githubRepoUrl, latestVersion);
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
        /// Checks for plugin updates by comparing local vs. latest version via GitHub.
        /// </summary>
        public async Task CheckForUpdates(Action<string> logAction)
        {
            foreach (string pluginFolder in Directory.GetDirectories(_pluginsPath))
            {
                foreach (string dllFile in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    string dllName = Path.GetFileName(dllFile);
                    string tomlPath = Path.Combine(_dataPath, dllName + ".toml");
                    if (!File.Exists(tomlPath)) continue;

                    var tomlString = File.ReadAllText(tomlPath);
                    var tomlModel = Toml.ToModel(tomlString);

                    if (!tomlModel.TryGetValue("plugin", out var pluginObj) || pluginObj is not TomlTable pluginSection)
                        continue;

                    if (!pluginSection.TryGetValue("repo_url", out var repoUrlObj)) continue;
                    string repoUrl = repoUrlObj?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl == "No Repo URL") continue;

                    string localVersion = pluginSection.TryGetValue("version", out var versionObj) ? versionObj?.ToString() ?? "" : "";
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
        }

        /// <summary>
        /// Returns a detailed list of installed plugins (from .toml files).
        /// Only shows DLLs that start with "RF.", "com.", or contain "rf".
        /// </summary>
        public List<PluginInfo> LoadPluginsDetailed()
        {
            var results = new List<PluginInfo>();
            if (!Directory.Exists(_pluginsPath))
                return results;

            foreach (string pluginFolder in Directory.GetDirectories(_pluginsPath))
            {
                foreach (string dllPath in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    string dllName = Path.GetFileName(dllPath);
                    string dllNameLower = dllName.ToLowerInvariant();

                    if (!(dllNameLower.StartsWith("rf.") ||
                          dllNameLower.StartsWith("com.") ||
                          dllNameLower.Contains("rf")))
                    {
                        continue;
                    }

                    var info = new PluginInfo
                    {
                        DllName = dllName,
                        Name = dllName,
                        Description = "",
                        RepoUrl = "No Repo URL"
                    };

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
                            if (pluginSec.TryGetValue("version", out var versionObj))
                                info.Version = versionObj?.ToString() ?? "Unknown Version";
                            if (pluginSec.TryGetValue("author", out var authorObj))
                                info.Author = authorObj?.ToString() ?? "Unknown Author";
                        }
                    }

                    string baseName = Path.GetFileNameWithoutExtension(dllName);
                    string possibleConfig = Path.Combine(_gamePath, "BepInEx", "config", baseName + ".cfg");
                    if (File.Exists(possibleConfig))
                    {
                        info.IsConfigPresent = true;
                        info.ConfigPath = possibleConfig;
                        try
                        {
                            foreach (var line in File.ReadAllLines(possibleConfig))
                            {
                                if (line.Trim().StartsWith("Enabled", StringComparison.OrdinalIgnoreCase))
                                {
                                    info.IsPluginEnabled = line.ToLower().Contains("true");
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                    else
                    {
                        info.IsConfigPresent = false;
                        info.IsPluginEnabled = false;
                    }

                    results.Add(info);
                }
            }
            return results;
        }

        // --------------------------------------------------
        // Public so MainWindow can call it
        // --------------------------------------------------
        public async Task<(string repoName, string repoDescription, string repoAuthor)> FetchRepoInfo(string githubRepoUrl)
        {
            var (owner, repo) = ParseOwnerAndRepo(githubRepoUrl);
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}";
            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");
            string resp = await client.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(resp);
            string name = doc.RootElement.GetProperty("name").GetString();
            string desc = doc.RootElement.GetProperty("description").GetString();
            string author = doc.RootElement.GetProperty("owner").GetProperty("login").GetString();
            return (name ?? "Unknown",
                    desc ?? "No description provided",
                    author ?? "Unknown Author");
        }

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

        private void ExtractAndInstallZip(string zipFilePath, string repoName, string repoDesc, string repoAuthor,
                                          string repoUrl, string version)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                ZipFile.ExtractToDirectory(zipFilePath, tempDir);
                string[] extractedDlls = Directory.GetFiles(tempDir, "*.dll", SearchOption.AllDirectories);

                string primaryRfDll = null;
                foreach (string dllPath in extractedDlls)
                {
                    string dllName = Path.GetFileName(dllPath).ToLowerInvariant();
                    if (dllName.StartsWith("rf.") ||
                        dllName.StartsWith("com.") ||
                        dllName.Contains("rf"))
                    {
                        primaryRfDll = dllPath;
                        break;
                    }
                }

                string folderName;
                if (!string.IsNullOrEmpty(primaryRfDll))
                {
                    folderName = Path.GetFileNameWithoutExtension(primaryRfDll);
                }
                else
                {
                    folderName = repoName;
                }

                string pluginFolder = Path.Combine(_pluginsPath, folderName);
                Directory.CreateDirectory(pluginFolder);

                foreach (string dll in extractedDlls)
                {
                    InstallDll(dll, pluginFolder, folderName, repoDesc, repoAuthor, repoUrl, version);
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
                if (File.Exists(zipFilePath)) File.Delete(zipFilePath);
            }
        }

        private void InstallDll(string dllPath, string pluginFolder, string pluginName,
                                string pluginDesc, string pluginAuthor, string repoUrl, string version)
        {
            string dllName = Path.GetFileName(dllPath);
            string dest = Path.Combine(pluginFolder, dllName);
            File.Copy(dllPath, dest, overwrite: true);

            if (File.Exists(dllPath))
            {
                try { File.Delete(dllPath); } catch { /* ignore */ }
            }

            string tomlFile = Path.Combine(_dataPath, dllName + ".toml");
            if (!File.Exists(tomlFile))
            {
                CreateMetadataFile(dest, pluginName, pluginDesc, pluginAuthor, repoUrl, version);
            }
        }

        private void CreateMetadataFile(string dllFullPath, string pluginName,
                                        string pluginDesc, string pluginAuthor,
                                        string repoUrl, string version)
        {
            string dllName = Path.GetFileName(dllFullPath);
            string tomlFile = Path.Combine(_dataPath, dllName + ".toml");
            var pluginSec = new TomlTable
            {
                ["name"] = pluginName,
                ["author"] = pluginAuthor,
                ["description"] = pluginDesc,
                ["repo_url"] = repoUrl,
                ["version"] = version
            };
            var meta = new TomlTable { ["plugin"] = pluginSec };

            File.WriteAllText(tomlFile, Toml.FromModel(meta));
        }

        private (string owner, string repo) ParseOwnerAndRepo(string githubUrl)
        {
            string trimmed = githubUrl.TrimEnd('/');
            int idx = trimmed.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                throw new InvalidOperationException("Invalid GitHub URL: " + githubUrl);

            trimmed = trimmed.Substring(idx + "github.com/".Length);
            var parts = trimmed.Split('/');
            if (parts.Length < 2)
                throw new InvalidOperationException("Invalid GitHub URL: " + githubUrl);

            string owner = parts[0];
            string repository = parts[1];
            return (owner, repository);
        }

        public async Task UpdateAllPluginsAsync(Action<string> logAction)
        {
            foreach (string pluginFolder in Directory.GetDirectories(_pluginsPath))
            {
                foreach (string dllFile in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    string dllName = Path.GetFileName(dllFile);
                    string tomlPath = Path.Combine(_dataPath, dllName + ".toml");
                    if (!File.Exists(tomlPath)) continue;

                    var tomlString = File.ReadAllText(tomlPath);
                    var tomlModel = Toml.ToModel(tomlString);

                    if (!tomlModel.TryGetValue("plugin", out var pluginObj) || pluginObj is not TomlTable pluginSec)
                        continue;

                    if (!pluginSec.TryGetValue("repo_url", out var repoObj)) continue;
                    string repoUrl = repoObj?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl == "No Repo URL") continue;

                    string localVer = pluginSec.TryGetValue("version", out var verObj) ? verObj?.ToString() ?? "" : "";
                    var (latestVer, _, _) = await FetchLatestReleaseInfo(repoUrl);
                    if (!string.IsNullOrWhiteSpace(latestVer) && latestVer != localVer)
                    {
                        logAction($"Updating plugin '{dllName}' from {localVer} to {latestVer}.");
                        await InstallPlugin(repoUrl);

                        // Update version in TOML
                        pluginSec["version"] = latestVer;
                        File.WriteAllText(tomlPath, Toml.FromModel(new TomlTable { ["plugin"] = pluginSec }));
                        logAction($"Updated plugin '{dllName}' to version {latestVer}.");
                    }
                }
            }
            logAction("Plugin update check complete.");
        }
    }
}
