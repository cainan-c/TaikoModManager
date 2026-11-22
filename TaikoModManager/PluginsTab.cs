using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        private readonly string _githubApiKey;

        public PluginsTab(string gamePath, string githubApiKey = "")
        {
            _gamePath = gamePath;
            _pluginsPath = Path.Combine(_gamePath, "BepInEx", "plugins");
            _githubApiKey = githubApiKey?.Trim() ?? "";

            Directory.CreateDirectory(_pluginsPath);
            ManagerCache.EnsureDirectories();
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
                using (HttpClient client = CreateGitHubClient())
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

                    InstallDll(tempFilePath, pluginFolder, folderName, repoDescription, repoAuthor, githubRepoUrl, latestVersion, createMetadata: true);
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
            try
            {
                foreach (string pluginFolder in Directory.GetDirectories(_pluginsPath))
                {
                    foreach (string dllFile in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
                    {
                        string dllName = Path.GetFileName(dllFile);
                        try
                        {
                            string tomlPath = ManagerCache.GitHubPluginMetadataPath(dllName);
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

                            var (latestVersion, _, _) = await FetchLatestReleaseInfo(repoUrl);
                            if (!string.IsNullOrEmpty(latestVersion) && latestVersion != localVersion)
                            {
                                logAction?.Invoke($"Plugin '{dllName}' has an update! Local: {localVersion}, Latest: {latestVersion}");
                            }
                        }
                        catch (Exception ex)
                        {
                            logAction?.Invoke($"Update check skipped for '{dllName}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"Plugin update check could not start: {ex.Message}");
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
                string? managedPrimaryDll = FindManagedPrimaryDll(pluginFolder);
                foreach (string dllPath in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    string dllName = Path.GetFileName(dllPath);
                    string dllNameLower = dllName.ToLowerInvariant();
                    string tmmMetadataPath = ManagerCache.ArchivePluginMetadataPath(dllName);
                    string githubMetadataPath = ManagerCache.GitHubPluginMetadataPath(dllName);

                    if (!string.IsNullOrWhiteSpace(managedPrimaryDll) &&
                        !string.Equals(dllName, managedPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!(dllNameLower.StartsWith("rf.") ||
                          dllNameLower.StartsWith("com.") ||
                          dllNameLower.Contains("rf")) &&
                        string.IsNullOrWhiteSpace(managedPrimaryDll))
                    {
                        continue;
                    }

                    var info = new PluginInfo
                    {
                        DllName = dllName,
                        Name = dllName,
                        Description = "",
                        RepoUrl = "No Repo URL",
                        Source = "External"
                    };

                    if (File.Exists(tmmMetadataPath))
                    {
                        ApplyArchiveMetadata(info, tmmMetadataPath);
                    }
                    else
                    {
                        string tomlPath = githubMetadataPath;
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
                                info.Source = "GitHub";
                            }
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

        private static string? FindManagedPrimaryDll(string pluginFolder)
        {
            foreach (string dllPath in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string dllName = Path.GetFileName(dllPath);
                foreach (string metadataPath in new[]
                         {
                             ManagerCache.ArchivePluginMetadataPath(dllName),
                             ManagerCache.GitHubPluginMetadataPath(dllName)
                         })
                {
                    if (!File.Exists(metadataPath))
                    {
                        continue;
                    }

                    try
                    {
                        var model = Toml.ToModel(File.ReadAllText(metadataPath));
                        string sectionName = metadataPath.StartsWith(ManagerCache.TmmDirectory, StringComparison.OrdinalIgnoreCase)
                            ? "archive"
                            : "plugin";
                        if (model.TryGetValue(sectionName, out var sectionObj) && sectionObj is TomlTable section &&
                            section.TryGetValue("primary_dll", out var primaryDllObj) &&
                            !string.IsNullOrWhiteSpace(primaryDllObj?.ToString()))
                        {
                            return primaryDllObj.ToString();
                        }

                        // Older cache files have no reliable package-primary marker.
                        // Let the normal plugin-name rules decide which legacy entries should be visible.
                    }
                    catch
                    {
                        // Ignore malformed cache metadata and treat the DLL as external.
                    }
                }
            }

            return null;
        }

        private static void ApplyArchiveMetadata(PluginInfo info, string metadataPath)
        {
            try
            {
                var model = Toml.ToModel(File.ReadAllText(metadataPath));
                if (!model.TryGetValue("archive", out var archiveObj) || archiveObj is not TomlTable archive)
                {
                    return;
                }

                if (archive.TryGetValue("name", out var name)) info.Name = name?.ToString() ?? info.Name;
                if (archive.TryGetValue("author", out var author)) info.Author = author?.ToString() ?? info.Author;
                if (archive.TryGetValue("description", out var description)) info.Description = description?.ToString() ?? info.Description;
                if (archive.TryGetValue("last_updated", out var lastUpdated)) info.LastUpdated = lastUpdated?.ToString() ?? string.Empty;
                if (archive.TryGetValue("preview_file", out var previewFile))
                {
                    string cachedPreview = Path.Combine(ManagerCache.TmmDirectory, Path.GetFileName(previewFile?.ToString() ?? string.Empty));
                    if (File.Exists(cachedPreview)) info.PreviewUrl = cachedPreview;
                }
                if (string.IsNullOrWhiteSpace(info.PreviewUrl) && archive.TryGetValue("preview_url", out var preview)) info.PreviewUrl = preview?.ToString() ?? string.Empty;
                info.Source = "TaikoModArchive";
            }
            catch
            {
                // A damaged cache entry should never prevent an installed plugin from appearing.
            }
        }

        // --------------------------------------------------
        // Public so MainWindow can call it
        // --------------------------------------------------
        public async Task<(string repoName, string repoDescription, string repoAuthor)> FetchRepoInfo(string githubRepoUrl)
        {
            var (owner, repo) = ParseOwnerAndRepo(githubRepoUrl);
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}";
            using HttpClient client = CreateGitHubClient();
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
            using HttpClient client = CreateGitHubClient();
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

                string? primaryDll = primaryRfDll ?? extractedDlls.FirstOrDefault();
                foreach (string dll in extractedDlls)
                {
                    bool isPrimaryDll = string.Equals(dll, primaryDll, StringComparison.OrdinalIgnoreCase);
                    InstallDll(dll, pluginFolder, folderName, repoDesc, repoAuthor, repoUrl, version, isPrimaryDll);
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
                if (File.Exists(zipFilePath)) File.Delete(zipFilePath);
            }
        }

        private void InstallDll(string dllPath, string pluginFolder, string pluginName,
                                string pluginDesc, string pluginAuthor, string repoUrl, string version, bool createMetadata)
        {
            string dllName = Path.GetFileName(dllPath);
            string dest = Path.Combine(pluginFolder, dllName);
            File.Copy(dllPath, dest, overwrite: true);

            if (File.Exists(dllPath))
            {
                try { File.Delete(dllPath); } catch { /* ignore */ }
            }

            string tomlFile = ManagerCache.GitHubPluginMetadataPath(dllName);
            if (createMetadata && !File.Exists(tomlFile))
            {
                CreateMetadataFile(dest, pluginName, pluginDesc, pluginAuthor, repoUrl, version, dllName);
            }
        }

        private void CreateMetadataFile(string dllFullPath, string pluginName,
                                        string pluginDesc, string pluginAuthor,
                                        string repoUrl, string version, string primaryDllName)
        {
            string dllName = Path.GetFileName(dllFullPath);
            string tomlFile = ManagerCache.GitHubPluginMetadataPath(dllName);
            var pluginSec = new TomlTable
            {
                ["name"] = pluginName,
                ["author"] = pluginAuthor,
                ["description"] = pluginDesc,
                ["repo_url"] = repoUrl,
                ["version"] = version,
                ["primary_dll"] = primaryDllName
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

        private HttpClient CreateGitHubClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");

            if (!string.IsNullOrWhiteSpace(_githubApiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _githubApiKey);
            }

            return client;
        }

        public async Task UpdateAllPluginsAsync(Action<string> logAction)
        {
            foreach (string pluginFolder in Directory.GetDirectories(_pluginsPath))
            {
                foreach (string dllFile in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    string dllName = Path.GetFileName(dllFile);
                    string tomlPath = ManagerCache.GitHubPluginMetadataPath(dllName);
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
