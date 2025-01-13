using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;  // for OpenFileDialog
using Tomlyn;
using Tomlyn.Model;

namespace TaikoModManager
{
    public partial class MainWindow : Window
    {
        private string gamePath;
        private string bepInExConfigPath;
        private const string BepInExFolderName = "BepInEx";

        private BepInExConfigTab configTab;
        private PluginsTab _pluginsTab;
        private ModsTab _modsTab;

        // Paths
        private readonly string _dataPath;
        private readonly string _configFilePath;

        // If launched with taikomodmanager:https://..., we'll store it here
        private readonly string _incomingCustomUrl;

        // ------------------------
        // Default constructor
        // ------------------------
        public MainWindow()
        {
            InitializeComponent();

            // 1) Prepare data/config paths
            _dataPath = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(_dataPath);

            _configFilePath = Path.Combine(_dataPath, "config.toml");

            // We'll do further setup in MainWindow_Loaded
            Loaded += MainWindow_Loaded;
        }

        // --------------------------------
        // Constructor that takes a custom URL
        // --------------------------------
        public MainWindow(string incomingUrl) : this()
        {
            _incomingCustomUrl = incomingUrl;
        }

        // --------------------------------------------------------------------
        // Called when the window finishes loading
        // --------------------------------------------------------------------
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1) Load or detect game path
            gamePath = LoadOrDetectGamePath();
            if (string.IsNullOrEmpty(gamePath) ||
                !File.Exists(Path.Combine(gamePath, "Taiko no Tatsujin Rhythm Festival.exe")))
            {
                MessageBox.Show("Could not locate the game path. Exiting...",
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                Close();
                return;
            }
            LogToConsole($"Game path: {gamePath}");

            // 2) Check BepInEx
            string bepInExPath = Path.Combine(gamePath, BepInExFolderName);
            bepInExConfigPath = Path.Combine(bepInExPath, "config", "BepInEx.cfg");

            if (!Directory.Exists(bepInExPath))
            {
                LogToConsole("BepInEx not found. Installing now...");
                await InstallBepInExAsync(gamePath);
            }
            if (!File.Exists(bepInExConfigPath))
            {
                LogToConsole("BepInEx.cfg not found. Run the game once to generate it.");
            }

            // 3) Create managers
            _pluginsTab = new PluginsTab(gamePath);
            _modsTab = new ModsTab(gamePath);

            // Load Plugins
            LoadPluginsToList();

            // Optionally check plugin updates
            await _pluginsTab.CheckForUpdates(LogToConsole);

            // Check for application updates
            CheckForUpdatesAsync();

            // --------------------------------------------------------
            // 4) If we have a custom GitHub URL, prompt user
            // --------------------------------------------------------
            if (!string.IsNullOrEmpty(_incomingCustomUrl))
            {
                LogToConsole($"Detected custom URL: {_incomingCustomUrl}");

                try
                {
                    // Make sure FetchRepoInfo is public in PluginsTab
                    var (repoName, repoDescription, repoAuthor) =
                        await _pluginsTab.FetchRepoInfo(_incomingCustomUrl);

                    // Confirm install
                    MessageBoxResult mbResult = MessageBox.Show(
                        $"Would you like to install the plugin '{repoName}'?",
                        "Install Plugin",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (mbResult == MessageBoxResult.Yes)
                    {
                        // Install plugin
                        await _pluginsTab.InstallPlugin(_incomingCustomUrl);

                        MessageBox.Show($"Plugin '{repoName}' installed successfully!",
                                        "Success",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Information);

                        // Reload to see newly-installed plugin
                        LoadPluginsToList();
                    }
                }
                catch (Exception ex2)
                {
                    MessageBox.Show($"Error installing plugin from URL:\n{ex2.Message}",
                                    "Error",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                }
            }
            // 5) Add taikomodmanager: url association
            string exePath = System.IO.Path.Combine(AppContext.BaseDirectory, "TaikoModManager.exe");
            ProtocolRegistration.EnsureRegistered(exePath);
        }

        /// <summary>
        /// Loads the game path from data/config.toml if it exists,
        /// otherwise tries to auto-detect, or asks user to locate the .exe
        /// </summary>
        private string LoadOrDetectGamePath()
        {
            // If config.toml exists, parse it
            if (File.Exists(_configFilePath))
            {
                try
                {
                    string tomlText = File.ReadAllText(_configFilePath);
                    var tomlModel = Toml.ToModel(tomlText);

                    if (tomlModel.TryGetValue("game", out var gameObj) && gameObj is TomlTable gameSec)
                    {
                        if (gameSec.TryGetValue("path", out var pathObj))
                        {
                            string loadedPath = pathObj?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(loadedPath) && Directory.Exists(loadedPath))
                            {
                                return loadedPath;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error reading config.toml: {ex.Message}",
                                    "Error",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                }
            }

            // If we reach here, config either doesn't exist or is invalid
            // Try auto-detect
            string autoDetected = Utilities.DetectGamePath();
            if (!string.IsNullOrEmpty(autoDetected) && Directory.Exists(autoDetected))
            {
                // Save it to config.toml
                SaveGamePathToConfig(autoDetected);
                return autoDetected;
            }

            // If auto-detect fails, ask user via OpenFileDialog
            var ofd = new OpenFileDialog
            {
                Title = "Locate Taiko no Tatsujin Rhythm Festival.exe",
                Filter = "Taiko no Tatsujin|Taiko no Tatsujin Rhythm Festival.exe"
            };
            bool? result = ofd.ShowDialog();
            if (result == true)
            {
                string filePath = ofd.FileName; // full path to the exe
                string folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    // Save it to config
                    SaveGamePathToConfig(folder);
                    return folder;
                }
            }

            // If user cancels or fails to pick
            return null;
        }

        /// <summary>
        /// Writes game path to data/config.toml like:
        /// 
        /// [game]
        /// path = "C:\\Games\\Taiko"
        /// </summary>
        private void SaveGamePathToConfig(string path)
        {
            var tomlTable = new TomlTable
            {
                ["path"] = path
            };
            var root = new TomlTable
            {
                ["game"] = tomlTable
            };
            string text = Toml.FromModel(root);
            File.WriteAllText(_configFilePath, text);
        }

        private async Task InstallBepInExAsync(string gamePath)
        {
            string bepInExUrl =
                "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), "BepInEx.zip");

            try
            {
                LogToConsole($"Downloading BepInEx from: {bepInExUrl}");
                using HttpClient client = new HttpClient();
                var response = await client.GetAsync(bepInExUrl);
                response.EnsureSuccessStatusCode();
                await File.WriteAllBytesAsync(tempZipPath, await response.Content.ReadAsByteArrayAsync());

                LogToConsole("Successfully downloaded BepInEx.");
                LogToConsole($"Extracting BepInEx to: {gamePath}");
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, gamePath, true);
                LogToConsole("BepInEx installed successfully.");
            }
            catch (Exception ex)
            {
                LogToConsole($"Error installing BepInEx: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                    LogToConsole("Temporary BepInEx zip file cleaned up.");
                }
            }
        }

        private async void CheckForUpdatesAsync()
        {
            string repoUrl = "https://api.github.com/repos/cainan-c/TaikoModManager/releases/latest";

            try
            {
                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");

                string response = await client.GetStringAsync(repoUrl);
                using var doc = JsonDocument.Parse(response);

                string latestVersion = doc.RootElement.GetProperty("tag_name").GetString();
                string currentVersion = "1.1.0"; // app version

                LogToConsole("Checking for Updates...");

                if (latestVersion != currentVersion)
                {
                    string releaseNotes = doc.RootElement.GetProperty("body").GetString();
                    if (MessageBox.Show(
                        $"A new version ({latestVersion}) is available:\n\n{releaseNotes}\n\nDo you want to update now?",
                        "Update Available",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        try
                        {
                            string exeDirectory = Path.GetFullPath(AppContext.BaseDirectory);
                            string helperAppPath = Path.Combine(exeDirectory, "UpdateHelper.exe");

                            if (!File.Exists(helperAppPath))
                            {
                                throw new FileNotFoundException(
                                    "UpdateHelper.exe not found. Ensure it is located in the application directory.");
                            }

                            LogToConsole("Launching UpdateHelper...");
                            var processStartInfo = new ProcessStartInfo
                            {
                                FileName = helperAppPath,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };

                            Process.Start(processStartInfo);
                            LogToConsole("UpdateHelper launched. Closing application...");
                            Application.Current.Shutdown();
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Error launching UpdateHelper: {ex.Message}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                }
                else
                {
                    LogToConsole("No Updates Found. You are using the latest version.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking for updates: {ex.Message}", "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        /// <summary> Switch to TekaTeka Mods Tab. </summary>
        private void SwitchToTekaTekaModsTab(object sender, RoutedEventArgs e)
        {
            PluginsTab.Visibility = Visibility.Collapsed;
            BepInExConfigTab.Visibility = Visibility.Collapsed;
            TekaTekaModsTab.Visibility = Visibility.Visible;

            // Load mods
            LoadTekaTekaMods();
        }

        /// <summary> Switch to Plugins Tab. </summary>
        private void SwitchToPluginsTab(object sender, RoutedEventArgs e)
        {
            TekaTekaModsTab.Visibility = Visibility.Collapsed;
            BepInExConfigTab.Visibility = Visibility.Collapsed;
            PluginsTab.Visibility = Visibility.Visible;

            // Re-setup if needed
            if (PluginList.Items.Count > 0)
            {
                // Force container generation
                PluginList.UpdateLayout();
            }
        }

        /// <summary> Switch to BepInEx Config Tab. </summary>
        private void SwitchToBepInExConfigTab(object sender, RoutedEventArgs e)
        {
            PluginsTab.Visibility = Visibility.Collapsed;
            TekaTekaModsTab.Visibility = Visibility.Collapsed;
            BepInExConfigTab.Visibility = Visibility.Visible;

            if (configTab == null)
            {
                configTab = new BepInExConfigTab(bepInExConfigPath, ConfigOptionsPanel);
            }
            if (string.IsNullOrEmpty(bepInExConfigPath) || !File.Exists(bepInExConfigPath))
            {
                LogToConsole("BepInEx.cfg not found. Please verify the game path and try again.");
                return;
            }
            configTab.LoadConfig();
        }

        /// <summary> Open Game Directory. </summary>
        private void OpenGameDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(gamePath))
            {
                MessageBox.Show("Game path is not set.", "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                LogToConsole("Game path is null or empty.");
                return;
            }
            if (!Directory.Exists(gamePath))
            {
                MessageBox.Show($"Directory does not exist:\n{gamePath}", "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                LogToConsole($"Game directory not found: {gamePath}");
                return;
            }
            try
            {
                string normalizedGamePath = gamePath.Replace(@"\\", @"\").Replace(@"/", @"\");
                Process.Start("explorer.exe", normalizedGamePath);
                LogToConsole($"Game directory opened: {normalizedGamePath}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening game directory: {ex.Message}", "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                LogToConsole($"Error opening game directory: {ex.Message}");
            }
        }

        /// <summary> Launch Game. </summary>
        private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
        {
            string exePath = Path.Combine(gamePath, "Taiko no Tatsujin Rhythm Festival.exe");
            if (!File.Exists(exePath))
            {
                LogToConsole("Game exe not found.");
                return;
            }
            try
            {
                Process.Start(exePath);
                LogToConsole("Game launched successfully.");
            }
            catch (Exception ex)
            {
                LogToConsole($"Error launching game: {ex.Message}");
            }
        }

        /// <summary> Install Plugin (prompted by user in the UI). </summary>
        private async void InstallPluginButton_Click(object sender, RoutedEventArgs e)
        {
            string url = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter the GitHub repository URL of the plugin:",
                "Install Plugin",
                "https://github.com/");
            if (string.IsNullOrWhiteSpace(url))
            {
                LogToConsole("Installation canceled: No URL provided.");
                return;
            }
            LogToConsole($"Starting plugin installation from: {url}");
            await _pluginsTab.InstallPlugin(url);

            // Reload the plugin list to reflect new TOML data
            LoadPluginsToList();
        }

        private async void UpdatePluginsButton_Click(object sender, RoutedEventArgs e)
        {
            LogToConsole("Checking for plugin updates...");
            await _pluginsTab.UpdateAllPluginsAsync(msg => LogToConsole(msg));
            LogToConsole("Update finished. Reloading plugin list...");
            LoadPluginsToList();
        }

        /// <summary> Load plugins into PluginList. </summary>
        private void LoadPluginsToList()
        {
            if (_pluginsTab == null) return;

            var plugins = _pluginsTab.LoadPluginsDetailed();
            PluginList.ItemsSource = plugins;

            PluginList.ItemContainerGenerator.StatusChanged -= PluginList_StatusChanged;
            PluginList.ItemContainerGenerator.StatusChanged += PluginList_StatusChanged;
        }

        private void PluginList_StatusChanged(object sender, EventArgs e)
        {
            if (PluginList.ItemContainerGenerator.Status ==
                System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                SetupPluginContextMenu();  // Now the items exist
            }
        }

        private void OpenPluginsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string pluginsPath = Path.Combine(gamePath, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsPath))
            {
                MessageBox.Show("Plugins folder not found.", "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                return;
            }
            string normPath = pluginsPath.Replace(@"\\", @"\").Replace(@"/", @"\");
            Process.Start("explorer.exe", normPath);
            LogToConsole($"Plugins folder opened: {normPath}");
        }

        private void SaveConfigChanges(object sender, RoutedEventArgs e)
        {
            if (configTab == null)
            {
                LogToConsole("ConfigTab not initialized.");
                return;
            }
            configTab.SaveConfig();
            LogToConsole("Config saved successfully.");
        }

        /// <summary> Load TekaTeka Mods. </summary>
        private void LoadTekaTekaMods()
        {
            TekaTekaModsList.ItemsSource = null;

            if (!_modsTab.IsTekaTekaInstalled())
            {
                MessageBox.Show("TekaTeka not found. Install RF.TekaTeka.dll first.",
                                "TekaTeka Missing",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                return;
            }
            if (!_modsTab.IsTekaSongsInitialized())
            {
                MessageBox.Show("TekaTeka not initialized. Run the game to generate TekaSongs.",
                                "TekaSongs Missing",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                return;
            }
            var mods = _modsTab.LoadMods();
            TekaTekaModsList.ItemsSource = mods;

            // Hook container gen
            TekaTekaModsList.ItemContainerGenerator.StatusChanged -= TekaTekaModsList_StatusChanged;
            TekaTekaModsList.ItemContainerGenerator.StatusChanged += TekaTekaModsList_StatusChanged;
        }

        private void TekaTekaModsList_StatusChanged(object sender, EventArgs e)
        {
            if (TekaTekaModsList.ItemContainerGenerator.Status ==
                System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                SetupModsContextMenu();  // Now the items exist
            }
        }

        private void OpenModsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            _modsTab.OpenModsFolder();
        }

        private void CreateModButton_Click(object sender, RoutedEventArgs e)
        {
            var w = new Window
            {
                Title = "Create New Mod",
                Width = 400,
                Height = 360,
                Background = System.Windows.Media.Brushes.Black
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
            w.Content = stack;

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Folder Name (no spaces):",
                Foreground = System.Windows.Media.Brushes.White
            });
            var folderBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(folderBox);

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Mod Name:",
                Foreground = System.Windows.Media.Brushes.White
            });
            var modNameBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(modNameBox);

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Author:",
                Foreground = System.Windows.Media.Brushes.White
            });
            var authorBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(authorBox);

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Version:",
                Foreground = System.Windows.Media.Brushes.White
            });
            var versionBox = new System.Windows.Controls.TextBox { Text = "1.0", Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(versionBox);

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Description:",
                Foreground = System.Windows.Media.Brushes.White
            });
            var descBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(descBox);

            var createBtn = new System.Windows.Controls.Button
            {
                Content = "Create",
                Background = System.Windows.Media.Brushes.Gray,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(5)
            };
            createBtn.Click += (s2, e2) =>
            {
                string folderName = folderBox.Text.Trim();
                string modName = modNameBox.Text.Trim();
                string author = authorBox.Text.Trim();
                string version = versionBox.Text.Trim();
                string desc = descBox.Text.Trim();

                if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(modName))
                {
                    MessageBox.Show("Folder Name and Mod Name cannot be empty.", "Error",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                    return;
                }

                string modPath = Path.Combine(gamePath, "TekaSongs", folderName);
                if (!Directory.Exists(modPath))
                    Directory.CreateDirectory(modPath);

                // create config.toml
                string configPath = Path.Combine(modPath, "config.toml");
                var lines = new List<string>
                {
                    "enabled = false",
                    $"name = \"{modName}\"",
                    $"author = \"{author}\"",
                    $"version = \"{version}\"",
                    $"description = \"{desc}\""
                };
                File.WriteAllLines(configPath, lines);

                MessageBox.Show($"Mod '{modName}' created successfully!", "Success",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);

                w.Close();

                // Reload the TekaTeka mods list
                LoadTekaTekaMods();
            };
            stack.Children.Add(createBtn);

            w.ShowDialog();
        }

        /// <summary>
        /// Setup context menu for plugin items.
        /// </summary>
        private void SetupPluginContextMenu()
        {
            if (PluginList.ItemsSource is not IEnumerable<PluginInfo> plugins) return;

            foreach (PluginInfo p in plugins)
            {
                var container = (System.Windows.Controls.ListViewItem)
                    PluginList.ItemContainerGenerator.ContainerFromItem(p);
                if (container == null) continue;

                var ctx = new System.Windows.Controls.ContextMenu();

                // 1) Edit Config
                var editConfig = new System.Windows.Controls.MenuItem { Header = "Edit Config" };
                if (string.IsNullOrEmpty(p.ConfigPath) || !File.Exists(p.ConfigPath))
                    editConfig.IsEnabled = false;
                editConfig.Click += (s, e) => EditPluginConfig(p);
                ctx.Items.Add(editConfig);

                // 2) Open GitHub Repo
                var openRepo = new System.Windows.Controls.MenuItem { Header = "Open GitHub Repo" };
                if (!string.IsNullOrWhiteSpace(p.RepoUrl) && p.RepoUrl != "No Repo URL")
                {
                    openRepo.IsEnabled = true;
                }
                else
                {
                    openRepo.IsEnabled = false;
                }
                openRepo.Click += (s, e) => OpenRepoUrl(p.RepoUrl);
                ctx.Items.Add(openRepo);

                container.ContextMenu = ctx;
            }
        }

        private void OpenRepoUrl(string repoUrl)
        {
            if (string.IsNullOrWhiteSpace(repoUrl)) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = repoUrl,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening repo: {ex.Message}",
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        private void EditPluginConfig(PluginInfo plugin)
        {
            if (!File.Exists(plugin.ConfigPath))
            {
                MessageBox.Show("Config file not found!", "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                return;
            }
            var w = new Window
            {
                Title = $"Edit Config - {plugin.Name}",
                Width = 600,
                Height = 600,
                Background = System.Windows.Media.Brushes.Black
            };
            var panel = new System.Windows.Controls.StackPanel
            {
                Background = System.Windows.Media.Brushes.Black
            };
            w.Content = panel;

            var tempTab = new BepInExConfigTab(plugin.ConfigPath, panel);
            tempTab.LoadConfig();

            var saveBtn = new System.Windows.Controls.Button
            {
                Content = "Save",
                Background = System.Windows.Media.Brushes.Gray,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(5)
            };
            saveBtn.Click += (s2, e2) =>
            {
                tempTab.SaveConfig();
                // optionally close or leave open
            };
            panel.Children.Add(saveBtn);

            w.ShowDialog();
        }

        /// <summary>
        /// Setup context menu for each TekaTeka mod item.
        /// </summary>
        private void SetupModsContextMenu()
        {
            if (TekaTekaModsList.ItemsSource is not IEnumerable<TekaTekaModInfo> mods) return;

            foreach (TekaTekaModInfo m in mods)
            {
                var container = (System.Windows.Controls.ListViewItem)
                    TekaTekaModsList.ItemContainerGenerator.ContainerFromItem(m);
                if (container == null) continue;

                var ctx = new System.Windows.Controls.ContextMenu();

                // "Edit Config"
                var editConfig = new System.Windows.Controls.MenuItem { Header = "Edit Config" };
                if (!File.Exists(m.ConfigPath)) editConfig.IsEnabled = false;
                editConfig.Click += (s, e) => EditTekaTekaModConfig(m);
                ctx.Items.Add(editConfig);

                // "Open Mod Folder"
                var openFolder = new System.Windows.Controls.MenuItem { Header = "Open Mod Folder" };
                string modFolder = Path.GetDirectoryName(m.ConfigPath) ?? "";
                if (!Directory.Exists(modFolder))
                    openFolder.IsEnabled = false;
                openFolder.Click += (s, e) =>
                {
                    try
                    {
                        string normFolder = modFolder.Replace(@"\\", @"\").Replace(@"/", @"\");
                        Process.Start("explorer.exe", normFolder);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error opening mod folder: {ex.Message}",
                                        "Error",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Error);
                    }
                };
                ctx.Items.Add(openFolder);

                container.ContextMenu = ctx;
            }
        }

        private void EditTekaTekaModConfig(TekaTekaModInfo mod)
        {
            if (!File.Exists(mod.ConfigPath))
            {
                MessageBox.Show("config.toml not found!", "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                return;
            }
            var w = new Window
            {
                Title = $"Edit Mod Config - {mod.Name}",
                Width = 600,
                Height = 600,
                Background = System.Windows.Media.Brushes.Black
            };
            var panel = new System.Windows.Controls.StackPanel
            {
                Background = System.Windows.Media.Brushes.Black,
                Margin = new Thickness(10)
            };
            w.Content = panel;

            var lines = File.ReadAllLines(mod.ConfigPath);
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = string.Join(Environment.NewLine, lines),
                AcceptsReturn = true,
                AcceptsTab = true,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Black,
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Consolas")
            };
            panel.Children.Add(textBox);

            var saveBtn = new System.Windows.Controls.Button
            {
                Content = "Save",
                Margin = new Thickness(5),
                Background = System.Windows.Media.Brushes.Gray,
                Foreground = System.Windows.Media.Brushes.White
            };
            saveBtn.Click += (s2, e2) =>
            {
                File.WriteAllText(mod.ConfigPath, textBox.Text);
                MessageBox.Show("Mod config saved!", "Success",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                w.Close();
            };
            panel.Children.Add(saveBtn);

            w.ShowDialog();
        }

        /// <summary>
        /// Helper method to append log messages to a console UI element.
        /// </summary>
        private void LogToConsole(string message)
        {
            // If your XAML has a TextBox named "ConsoleLog"
            ConsoleLog.AppendText(message + Environment.NewLine);
            ConsoleLog.ScrollToEnd();
        }
    }
}
