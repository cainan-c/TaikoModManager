using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
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
        private string _githubApiKey = string.Empty;
        private string _launchType = "Steam";
        private CancellationTokenSource? _gameLogMonitorCancellation;
        private const string BepInExFolderName = "BepInEx";
        private const string SteamAppId = "2288630";

        private BepInExConfigTab configTab;
        private PluginsTab _pluginsTab;
        private ModsTab _modsTab;
        private TaikoModArchiveBrowser? _archiveBrowser;
        private bool _tekaTekaInstallPromptShown;
        private int _archivePage = 1;
        private int _archiveTotalPages = 1;
        private bool _archiveLoaded;
        private bool _archiveLoading;

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
            ManagerCache.MigrateLegacyMetadata();
            _dataPath = ManagerCache.RootDirectory;

            _configFilePath = Path.Combine(_dataPath, "config.toml");

            // We'll do further setup in MainWindow_Loaded
            Loaded += MainWindow_Loaded;
            Closed += (_, _) => _gameLogMonitorCancellation?.Cancel();
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
            _pluginsTab = new PluginsTab(gamePath, _githubApiKey);
            _modsTab = new ModsTab(gamePath);
            _archiveBrowser = new TaikoModArchiveBrowser(gamePath);

            // Load Plugins
            LoadPluginsToList();

            // Optionally check plugin updates
            try
            {
                await _pluginsTab.CheckForUpdates(LogToConsole);
            }
            catch (Exception ex)
            {
                LogToConsole($"Plugin update check failed safely: {ex.Message}");
            }

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

                    if (tomlModel.TryGetValue("github", out var githubObj) && githubObj is TomlTable githubSec &&
                        githubSec.TryGetValue("api_key", out var apiKeyObj))
                    {
                        _githubApiKey = apiKeyObj?.ToString()?.Trim() ?? string.Empty;
                    }

                    if (tomlModel.TryGetValue("game", out var gameObj) && gameObj is TomlTable gameSec)
                    {
                        if (gameSec.TryGetValue("launch_type", out var launchTypeObj))
                        {
                            _launchType = NormalizeLaunchType(launchTypeObj?.ToString());
                        }

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

        private void SaveGamePathToConfig(string path)
        {
            SaveManagerConfig(path, _githubApiKey, _launchType);
        }

        private void SaveManagerConfig(string path, string githubApiKey, string launchType)
        {
            var gameTable = new TomlTable
            {
                ["path"] = path,
                ["launch_type"] = NormalizeLaunchType(launchType)
            };
            var githubTable = new TomlTable
            {
                ["api_key"] = githubApiKey ?? string.Empty
            };
            var root = new TomlTable
            {
                ["game"] = gameTable,
                ["github"] = githubTable
            };
            string text = Toml.FromModel(root);
            File.WriteAllText(_configFilePath, text);
        }

        private static string NormalizeLaunchType(string? launchType) =>
            string.Equals(launchType, "Direct", StringComparison.OrdinalIgnoreCase) ? "Direct" : "Steam";

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

        private async void CheckForUpdatesAsync()
        {
            string repoUrl = "https://api.github.com/repos/cainan-c/TaikoModManager/releases/latest";

            try
            {
                using HttpClient client = CreateGitHubClient();

                string response = await client.GetStringAsync(repoUrl);
                using var doc = JsonDocument.Parse(response);

                string latestVersion = doc.RootElement.GetProperty("tag_name").GetString() ?? "latest";
                string latestCommit = doc.RootElement.TryGetProperty("target_commitish", out JsonElement targetCommitish)
                    ? targetCommitish.GetString() ?? string.Empty
                    : string.Empty;
                string currentCommit = GetCurrentBuildCommit();

                LogToConsole("Checking for Updates...");

                if (string.IsNullOrWhiteSpace(currentCommit) ||
                    !string.Equals(latestCommit, currentCommit, StringComparison.OrdinalIgnoreCase))
                {
                    string releaseNotes = doc.RootElement.GetProperty("body").GetString() ?? string.Empty;
                    string releaseUrl = doc.RootElement.TryGetProperty("html_url", out JsonElement htmlUrl)
                        ? htmlUrl.GetString() ?? "https://github.com/cainan-c/TaikoModManager/releases/latest"
                        : "https://github.com/cainan-c/TaikoModManager/releases/latest";
                    if (MessageBox.Show(
                        $"A newer build ({latestVersion}) is available on GitHub:\n\n{releaseNotes}\n\nOpen the GitHub release page?",
                        "Update Available",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = releaseUrl,
                                UseShellExecute = true
                            });
                            LogToConsole("Opened the latest GitHub release page.");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Could not open the GitHub release page: {ex.Message}", "Error",
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

        private static string GetCurrentBuildCommit()
        {
            string? informationalVersion = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            int separator = informationalVersion?.LastIndexOf('+') ?? -1;
            return separator >= 0 && separator < informationalVersion!.Length - 1
                ? informationalVersion[(separator + 1)..]
                : string.Empty;
        }

        private async void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.Source != MainTabs)
            {
                return;
            }

            if (_modsTab == null)
            {
                return;
            }

            if (MainTabs.SelectedIndex == 0)
            {
                if (await EnsureTekaTekaInstalledAsync())
                {
                    LoadTekaTekaMods();
                }
                return;
            }

            if (MainTabs.SelectedIndex == 2)
            {
                if (!_archiveLoaded)
                {
                    _ = LoadArchiveFeedAsync();
                }
                return;
            }

            if (MainTabs.SelectedIndex == 3)
            {
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
        }

        private void ManagerList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListView list)
            {
                return;
            }

            var item = System.Windows.Controls.ItemsControl.ContainerFromElement(
                list,
                e.OriginalSource as DependencyObject) as System.Windows.Controls.ListViewItem;

            if (item?.DataContext != null)
            {
                list.SelectedItem = item.DataContext;
            }
        }

        private async Task LoadArchiveFeedAsync()
        {
            if (_archiveBrowser == null || _archiveLoading)
            {
                return;
            }

            _archiveLoading = true;
            SetArchiveControlsEnabled(false);
            BrowseStatusText.Text = "Loading Rhythm Festival PC mods…";

            try
            {
                var sort = BrowseSortBox.SelectedIndex switch
                {
                    1 => TaikoArchiveSort.Downloads,
                    2 => TaikoArchiveSort.Likes,
                    _ => TaikoArchiveSort.Latest
                };
                string? postType = BrowseTypeBox.SelectedIndex switch
                {
                    1 => "Plugin",
                    2 => "Song",
                    _ => null
                };

                TaikoArchiveFeed feed = await _archiveBrowser.GetFeedAsync(_archivePage, 20, sort, BrowseSearchText.Text, postType);
                _archivePage = feed.Page;
                _archiveTotalPages = feed.TotalPages;

                IEnumerable<TaikoArchivePost> visiblePosts = feed.Posts;

                var posts = visiblePosts.ToList();
                BrowseModsList.ItemsSource = posts;
                BrowseModsList.SelectedIndex = posts.Count > 0 ? 0 : -1;
                BrowsePageText.Text = $"Page {_archivePage} / {_archiveTotalPages}";
                BrowseStatusText.Text = posts.Count == 0
                    ? "No matching Rhythm Festival PC mods found."
                    : $"{posts.Count} Rhythm Festival PC mod{(posts.Count == 1 ? string.Empty : "s")} shown";
                _archiveLoaded = true;
            }
            catch (Exception ex)
            {
                BrowseModsList.ItemsSource = null;
                BrowsePageText.Text = "Page —";
                BrowseStatusText.Text = "Could not load Taiko Mod Archive.";
                LogToConsole($"Taiko Mod Archive browser error: {ex.Message}");
            }
            finally
            {
                _archiveLoading = false;
                SetArchiveControlsEnabled(true);
            }
        }

        private void SetArchiveControlsEnabled(bool isEnabled)
        {
            BrowseSearchText.IsEnabled = isEnabled;
            BrowseSortBox.IsEnabled = isEnabled;
            BrowseTypeBox.IsEnabled = isEnabled;
            BrowseRefreshButton.IsEnabled = isEnabled;
            BrowsePreviousButton.IsEnabled = isEnabled && _archivePage > 1;
            BrowseNextButton.IsEnabled = isEnabled && _archivePage < _archiveTotalPages;
            BrowseInstallButton.IsEnabled = isEnabled;
        }

        private void BrowseSearchButton_Click(object sender, RoutedEventArgs e)
        {
            _archivePage = 1;
            _ = LoadArchiveFeedAsync();
        }

        private void BrowseRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _archiveLoaded = false;
            _ = LoadArchiveFeedAsync();
        }

        private void BrowseFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _archiveBrowser == null)
            {
                return;
            }

            _archivePage = 1;
            _ = LoadArchiveFeedAsync();
        }

        private void BrowsePreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (_archivePage <= 1)
            {
                return;
            }

            _archivePage--;
            _ = LoadArchiveFeedAsync();
        }

        private void BrowseNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_archivePage >= _archiveTotalPages)
            {
                return;
            }

            _archivePage++;
            _ = LoadArchiveFeedAsync();
        }

        private async void BrowseInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_archiveBrowser == null || BrowseModsList.SelectedItem is not TaikoArchivePost post)
            {
                MessageBox.Show("Select a mod to install first.", "Browse Mods", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int? fileIndex = ChooseArchiveFile(post);
            if (!fileIndex.HasValue)
            {
                return;
            }

            BrowseInstallButton.IsEnabled = false;
            BrowseDownloadProgress.Value = 0;
            BrowseDownloadProgress.IsIndeterminate = true;
            BrowseDownloadProgress.Visibility = Visibility.Visible;
            BrowseStatusText.Text = $"Downloading {post.Name}…";
            try
            {
                var downloadProgress = new Progress<double>(percentage =>
                {
                    if (double.IsNaN(percentage))
                    {
                        BrowseDownloadProgress.IsIndeterminate = true;
                        return;
                    }

                    BrowseDownloadProgress.IsIndeterminate = false;
                    BrowseDownloadProgress.Value = percentage;
                });
                string installPath = await _archiveBrowser.DownloadAndInstallAsync(post, fileIndex.Value, downloadProgress);
                BrowseStatusText.Text = $"Installed {post.Name}";
                LogToConsole($"Installed archive {post.TypeLabel.ToLowerInvariant()}: {post.Name} -> {installPath}");

                if (post.IsPlugin)
                {
                    LoadPluginsToList();
                }
                else if (_modsTab != null && _modsTab.IsTekaTekaInstalled() && _modsTab.IsTekaSongsInitialized())
                {
                    LoadTekaTekaMods();
                }
            }
            catch (Exception ex)
            {
                BrowseStatusText.Text = $"Could not install {post.Name}.";
                LogToConsole($"Archive install failed for {post.Name}: {ex.Message}");
                MessageBox.Show($"Could not install '{post.Name}': {ex.Message}",
                                "Install Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BrowseDownloadProgress.IsIndeterminate = false;
                BrowseDownloadProgress.Visibility = Visibility.Collapsed;
                BrowseInstallButton.IsEnabled = true;
            }
        }

        private int? ChooseArchiveFile(TaikoArchivePost post)
        {
            if (post.Files.Count == 0)
            {
                MessageBox.Show("This post has no downloadable files.", "Browse Mods", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            if (post.Files.Count == 1)
            {
                return 0;
            }

            var dialog = new Window
            {
                Title = $"Choose File - {post.Name}",
                Owner = this,
                Width = 560,
                Height = 370,
                MinWidth = 420,
                MinHeight = 280,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(19, 19, 19)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.Resources[typeof(System.Windows.Controls.Button)] = Resources[typeof(System.Windows.Controls.Button)];

            var root = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            dialog.Content = root;

            var heading = new System.Windows.Controls.TextBlock
            {
                Text = "Choose a file to install",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(heading);

            var files = new System.Windows.Controls.ListBox
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(1),
                SelectedIndex = 0
            };
            for (int index = 0; index < post.Files.Count; index++)
            {
                string fileName = index < post.FileNames.Count ? post.FileNames[index] : $"File {index + 1}";
                files.Items.Add(fileName);
            }
            System.Windows.Controls.Grid.SetRow(files, 1);
            root.Children.Add(files);

            var actions = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 90, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            actions.Children.Add(cancel);
            var install = new System.Windows.Controls.Button { Content = "Install", Width = 90, Height = 34 };
            install.Click += (_, _) => dialog.DialogResult = true;
            actions.Children.Add(install);
            System.Windows.Controls.Grid.SetRow(actions, 2);
            root.Children.Add(actions);

            return dialog.ShowDialog() == true ? files.SelectedIndex : null;
        }

        private void ManagerList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ListView list && list.SelectedItem != null)
            {
                list.Focus();
            }
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

        private void OpenModManagerConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "Mod Manager Config",
                Owner = this,
                Width = 680,
                Height = 540,
                MinWidth = 560,
                MinHeight = 400,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(19, 19, 19)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            dialog.Resources[typeof(System.Windows.Controls.Button)] = Resources[typeof(System.Windows.Controls.Button)];
            dialog.Resources[typeof(System.Windows.Controls.TextBox)] = Resources[typeof(System.Windows.Controls.TextBox)];
            dialog.Resources[typeof(System.Windows.Controls.TextBlock)] = Resources[typeof(System.Windows.Controls.TextBlock)];
            dialog.Resources["BrowseComboBoxToggle"] = Resources["BrowseComboBoxToggle"];
            dialog.Resources["BrowseComboBoxItem"] = Resources["BrowseComboBoxItem"];
            dialog.Resources["BrowseComboBox"] = Resources["BrowseComboBox"];

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(58) });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(62) });
            dialog.Content = root;

            var header = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 0, 16, 0),
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = "Mod Manager Config",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242))
                }
            };
            root.Children.Add(header);

            var content = new System.Windows.Controls.StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            System.Windows.Controls.Grid.SetRow(content, 1);
            root.Children.Add(content);

            content.Children.Add(new System.Windows.Controls.TextBlock { Text = "Game location", FontSize = 16, FontWeight = FontWeights.Bold });
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Choose the folder containing Taiko no Tatsujin Rhythm Festival.exe.",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 184, 184)),
                Margin = new Thickness(0, 3, 0, 8)
            });

            var gamePathRow = new System.Windows.Controls.Grid();
            gamePathRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gamePathRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(110) });
            var gamePathBox = new System.Windows.Controls.TextBox
            {
                Text = gamePath ?? string.Empty,
                Height = 36,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            gamePathRow.Children.Add(gamePathBox);
            var browseButton = new System.Windows.Controls.Button { Content = "Browse...", Height = 36, Margin = new Thickness(8, 0, 0, 0) };
            System.Windows.Controls.Grid.SetColumn(browseButton, 1);
            browseButton.Click += (s, e2) =>
            {
                var picker = new OpenFileDialog
                {
                    Title = "Locate Taiko no Tatsujin Rhythm Festival.exe",
                    Filter = "Taiko no Tatsujin Rhythm Festival|Taiko no Tatsujin Rhythm Festival.exe",
                    InitialDirectory = Directory.Exists(gamePathBox.Text) ? gamePathBox.Text : null
                };
                if (picker.ShowDialog() == true)
                {
                    gamePathBox.Text = Path.GetDirectoryName(picker.FileName) ?? gamePathBox.Text;
                }
            };
            gamePathRow.Children.Add(browseButton);
            content.Children.Add(gamePathRow);

            content.Children.Add(new System.Windows.Controls.Border { Height = 18 });
            content.Children.Add(new System.Windows.Controls.TextBlock { Text = "Launch type", FontSize = 16, FontWeight = FontWeights.Bold });
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Steam launches steam://run/2288630. Direct runs the game executable from the selected folder.",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 184, 184)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 8)
            });
            var launchTypeBox = new System.Windows.Controls.ComboBox
            {
                Style = (Style)dialog.Resources["BrowseComboBox"],
                Height = 36,
                SelectedItem = NormalizeLaunchType(_launchType)
            };
            launchTypeBox.Items.Add("Steam");
            launchTypeBox.Items.Add("Direct");
            launchTypeBox.SelectedItem = NormalizeLaunchType(_launchType);
            content.Children.Add(launchTypeBox);

            content.Children.Add(new System.Windows.Controls.Border { Height = 18 });
            content.Children.Add(new System.Windows.Controls.TextBlock { Text = "GitHub API key", FontSize = 16, FontWeight = FontWeights.Bold });
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Optional. Used for GitHub API and release requests to avoid anonymous rate limits. It is stored in this manager's local config.toml.",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 184, 184)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 8)
            });
            var apiKeyBox = new System.Windows.Controls.PasswordBox
            {
                Password = _githubApiKey,
                Height = 36,
                Padding = new Thickness(8, 6, 8, 6),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 24)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(1)
            };
            content.Children.Add(apiKeyBox);

            var footer = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 10, 14, 10)
            };
            System.Windows.Controls.Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footer.Child = actions;

            var cancelButton = new System.Windows.Controls.Button { Content = "Cancel", Height = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 0) };
            cancelButton.Click += (s, e2) => dialog.Close();
            actions.Children.Add(cancelButton);

            var saveButton = new System.Windows.Controls.Button { Content = "Save Changes", Height = 36, Padding = new Thickness(16, 0, 16, 0) };
            saveButton.Click += (s, e2) =>
            {
                var selectedGamePath = gamePathBox.Text.Trim();
                if (!File.Exists(Path.Combine(selectedGamePath, "Taiko no Tatsujin Rhythm Festival.exe")))
                {
                    MessageBox.Show("Choose the folder containing Taiko no Tatsujin Rhythm Festival.exe.", "Game location", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _githubApiKey = apiKeyBox.Password.Trim();
                _launchType = NormalizeLaunchType(launchTypeBox.SelectedItem?.ToString());
                SaveManagerConfig(selectedGamePath, _githubApiKey, _launchType);

                var gamePathChanged = !string.Equals(gamePath, selectedGamePath, StringComparison.OrdinalIgnoreCase);
                gamePath = selectedGamePath;
                bepInExConfigPath = Path.Combine(gamePath, BepInExFolderName, "config", "BepInEx.cfg");
                _pluginsTab = new PluginsTab(gamePath, _githubApiKey);
                _modsTab = new ModsTab(gamePath);
                _archiveBrowser = new TaikoModArchiveBrowser(gamePath);
                _archiveLoaded = false;
                _archivePage = 1;
                configTab = null;
                ConfigOptionsPanel.Children.Clear();
                LoadPluginsToList();

                LogToConsole(gamePathChanged ? $"Game location changed to: {gamePath}" : "Mod Manager configuration saved.");
                dialog.Close();
            };
            actions.Children.Add(saveButton);

            dialog.ShowDialog();
        }

        /// <summary> Launch Game. </summary>
        private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.Equals(_launchType, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"steam://run/{SteamAppId}",
                        UseShellExecute = true
                    });
                    LogToConsole("Requested game launch through Steam.");
                }
                catch (Exception ex)
                {
                    LogToConsole($"Error launching through Steam: {ex.Message}");
                }
                return;
            }

            string exePath = Path.Combine(gamePath, "Taiko no Tatsujin Rhythm Festival.exe");
            if (!File.Exists(exePath))
            {
                LogToConsole("Game exe not found.");
                return;
            }
            try
            {
                Process? gameProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = gamePath,
                    UseShellExecute = true
                });

                if (gameProcess == null)
                {
                    throw new InvalidOperationException("Windows did not return a game process.");
                }

                StartBepInExLogMonitor(gameProcess);
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
            LogToConsole("Checking plugin updates from GitHub and Taiko Mod Archive...");
            Exception? githubUpdateFailure = null;
            try
            {
                await _pluginsTab.UpdateAllPluginsAsync(msg => LogToConsole(msg));
            }
            catch (Exception ex)
            {
                githubUpdateFailure = ex;
                LogToConsole($"Plugin update failed: {ex.Message}");
            }

            await CheckTaikoModArchiveUpdatesAsync("Plugin", "plugin");
            LogToConsole("Plugin update check finished. Reloading plugin list...");
            LoadPluginsToList();

            if (githubUpdateFailure != null)
            {
                MessageBox.Show("A GitHub plugin update failed. Taiko Mod Archive updates were still checked; see the activity log for details.",
                                "Plugin update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Mirrors new BepInEx log lines into the manager activity consoles for games
        /// launched from this window. Existing lines are skipped, but a newly-created
        /// or truncated log is read from its beginning.
        /// </summary>
        private void StartBepInExLogMonitor(Process gameProcess)
        {
            _gameLogMonitorCancellation?.Cancel();
            _gameLogMonitorCancellation = new CancellationTokenSource();

            string logPath = Path.Combine(gamePath, BepInExFolderName, "LogOutput.log");
            _ = MonitorBepInExLogAsync(gameProcess, logPath, _gameLogMonitorCancellation.Token);
        }

        private async Task MonitorBepInExLogAsync(Process gameProcess, string logPath, CancellationToken cancellationToken)
        {
            long readPosition = 0;
            bool hasObservedLogFile = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested && !gameProcess.HasExited)
                {
                    string? newLogText = null;

                    try
                    {
                        if (File.Exists(logPath))
                        {
                            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                            if (!hasObservedLogFile)
                            {
                                readPosition = stream.Length;
                                hasObservedLogFile = true;
                            }
                            else
                            {
                                if (stream.Length < readPosition)
                                {
                                    readPosition = 0;
                                }

                                if (stream.Length > readPosition)
                                {
                                    stream.Seek(readPosition, SeekOrigin.Begin);
                                    using var reader = new StreamReader(stream);
                                    newLogText = await reader.ReadToEndAsync();
                                    readPosition = stream.Length;
                                }
                            }
                        }
                    }
                    catch (IOException)
                    {
                        // BepInEx can briefly hold the log while it rotates it; try again on the next pass.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // The game may still be creating the log. Keep monitoring while it is running.
                    }

                    if (!string.IsNullOrEmpty(newLogText))
                    {
                        await Dispatcher.InvokeAsync(() => AppendBepInExLog(newLogText));
                    }

                    await Task.Delay(250, cancellationToken);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() => LogToConsole("Game closed. Stopped following BepInEx log output."));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the window closes or another game launch replaces this monitor.
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() => LogToConsole($"Stopped following BepInEx log: {ex.Message}"));
                }
            }
            finally
            {
                gameProcess.Dispose();
            }
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

        private void RefreshPluginsButton_Click(object sender, RoutedEventArgs e)
        {
            LoadPluginsToList();
            LogToConsole("Plugin list refreshed.");
        }

        private void SortPluginsByEnabled_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (PluginList.ItemsSource is not IEnumerable<PluginInfo> plugins)
            {
                return;
            }

            PluginList.ItemsSource = plugins
                .OrderByDescending(plugin => plugin.IsPluginEnabled)
                .ThenBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void SortPluginsByName_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (PluginList.ItemsSource is not IEnumerable<PluginInfo> plugins)
            {
                return;
            }

            PluginList.ItemsSource = plugins
                .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                LogToConsole("TekaTeka is not installed.");
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

        /// <summary>Offers the official Rhythm Festival PC archive package on first access to the mods tab.</summary>
        private async Task<bool> EnsureTekaTekaInstalledAsync()
        {
            if (_modsTab == null || _modsTab.IsTekaTekaInstalled())
            {
                return true;
            }

            if (_tekaTekaInstallPromptShown)
            {
                return false;
            }

            _tekaTekaInstallPromptShown = true;
            MessageBoxResult choice = MessageBox.Show(
                "TekaTeka is required to use song mods. Would you like to install it now from Taiko Mod Archive?\n\nIt will be installed to BepInEx\\plugins\\TekaTeka.",
                "Install TekaTeka",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
            {
                LogToConsole("TekaTeka installation was skipped.");
                return false;
            }

            if (_archiveBrowser == null)
            {
                LogToConsole("Taiko Mod Archive is not ready yet; TekaTeka could not be installed.");
                return false;
            }

            try
            {
                const long tekaTekaPostId = 3;
                TaikoArchivePost post = await _archiveBrowser.GetPostAsync(tekaTekaPostId);
                if (!post.IsPlugin || post.Files.Count == 0)
                {
                    throw new InvalidOperationException("The TekaTeka archive package is unavailable.");
                }

                LogToConsole("Installing TekaTeka from Taiko Mod Archive...");
                string installPath = await _archiveBrowser.DownloadAndInstallAsync(post, 0);
                LogToConsole($"Installed TekaTeka to: {installPath}");
                LoadPluginsToList();
                MessageBox.Show("TekaTeka was installed. Launch the game once to create the TekaSongs folder, then add your song mods.",
                                "TekaTeka Installed", MessageBoxButton.OK, MessageBoxImage.Information);
                return _modsTab.IsTekaTekaInstalled();
            }
            catch (Exception ex)
            {
                LogToConsole($"Could not install TekaTeka: {ex.Message}");
                MessageBox.Show($"Could not install TekaTeka from Taiko Mod Archive: {ex.Message}",
                                "TekaTeka Install Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
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

        private void RefreshModsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_modsTab == null)
            {
                return;
            }

            LoadTekaTekaMods();
            LogToConsole("TekaTeka mod list refreshed.");
        }

        private async void CheckModsUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            await CheckTaikoModArchiveUpdatesAsync("Song", "song mod");
            LoadTekaTekaMods();
        }

        /// <summary>
        /// Checks installed Taiko Mod Archive items by their cached post ID. Archive
        /// packages do not have release versions, so their API timestamp is the
        /// update marker stored alongside the downloaded package metadata.
        /// </summary>
        private async Task CheckTaikoModArchiveUpdatesAsync(string expectedPostType, string itemLabel)
        {
            if (_archiveBrowser == null)
            {
                LogToConsole("Taiko Mod Archive is not ready yet.");
                return;
            }

            ManagerCache.EnsureDirectories();
            int checkedCount = 0;
            int initializedCount = 0;
            int failedCount = 0;
            var updates = new List<string>();

            foreach (string metadataPath in Directory.EnumerateFiles(ManagerCache.TmmDirectory, "*.toml", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var model = Toml.ToModel(File.ReadAllText(metadataPath));
                    if (!model.TryGetValue("archive", out var archiveObj) || archiveObj is not TomlTable archive ||
                        !archive.TryGetValue("post_id", out var postIdObj) ||
                        !long.TryParse(postIdObj?.ToString(), out long postId))
                    {
                        continue;
                    }

                    string cachedPostType = archive.TryGetValue("post_type", out var postTypeObj)
                        ? postTypeObj?.ToString() ?? string.Empty
                        : string.Empty;
                    if (!string.Equals(cachedPostType, expectedPostType, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    checkedCount++;
                    TaikoArchivePost currentPost = await _archiveBrowser.GetPostAsync(postId);
                    if (!string.Equals(currentPost.PostType, expectedPostType, StringComparison.OrdinalIgnoreCase) ||
                        currentPost.LastUpdated == default)
                    {
                        continue;
                    }

                    string currentTimestamp = currentPost.LastUpdated.ToUniversalTime().ToString("O");
                    if (!archive.TryGetValue("last_updated", out var cachedTimestampObj) ||
                        !DateTimeOffset.TryParse(cachedTimestampObj?.ToString(), out DateTimeOffset cachedTimestamp))
                    {
                        // Metadata created before update tracking needs one baseline
                        // check. It is not reported as an update because its prior
                        // archive timestamp was never recorded.
                        archive["last_updated"] = currentTimestamp;
                        File.WriteAllText(metadataPath, Toml.FromModel(model));
                        initializedCount++;
                        continue;
                    }

                    if (currentPost.LastUpdated > cachedTimestamp)
                    {
                        updates.Add($"{currentPost.Name} — last updated {FormatArchiveTimestamp(currentPost.LastUpdated)}");
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    LogToConsole($"Could not check TMM cache '{Path.GetFileName(metadataPath)}': {ex.Message}");
                }
            }

            if (checkedCount == 0)
            {
                const string noItems = "No Taiko Mod Archive downloads of this type are installed yet.";
                LogToConsole(noItems);
                MessageBox.Show(noItems, "Taiko Mod Archive updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (updates.Count == 0)
            {
                string message = $"No newer Taiko Mod Archive {itemLabel} updates were found.";
                if (initializedCount > 0)
                {
                    message += $" Update tracking was set up for {initializedCount} existing item(s).";
                }
                if (failedCount > 0)
                {
                    message += $" {failedCount} item(s) could not be checked; see the activity log.";
                }
                LogToConsole(message);
                MessageBox.Show(message, "Taiko Mod Archive updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string updateMessage = $"Taiko Mod Archive has {updates.Count} update(s) available:\n\n" +
                                   string.Join(Environment.NewLine, updates) +
                                   "\n\nOpen Browse Mods to download the newer package.";
            LogToConsole($"Taiko Mod Archive found {updates.Count} {itemLabel} update(s).");
            MessageBox.Show(updateMessage, "Taiko Mod Archive updates", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string FormatArchiveTimestamp(DateTimeOffset timestamp) =>
            timestamp.ToLocalTime().ToString("dd MMM yyyy, HH:mm");

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

                ctx.Items.Add(new System.Windows.Controls.Separator());
                var deletePlugin = new System.Windows.Controls.MenuItem
                {
                    Header = "Delete Plugin",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 107))
                };
                deletePlugin.Click += (s, e) => DeletePlugin(p);
                ctx.Items.Add(deletePlugin);

                container.ContextMenu = ctx;
            }
        }

        private void SortModsByEnabled_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (TekaTekaModsList.ItemsSource is not IEnumerable<TekaTekaModInfo> mods)
            {
                return;
            }

            TekaTekaModsList.ItemsSource = mods
                .OrderByDescending(mod => mod.IsModEnabled)
                .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void SortModsByName_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (TekaTekaModsList.ItemsSource is not IEnumerable<TekaTekaModInfo> mods)
            {
                return;
            }

            TekaTekaModsList.ItemsSource = mods
                .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void DeletePlugin(PluginInfo plugin)
        {
            string pluginsRoot = Path.Combine(gamePath, "BepInEx", "plugins");
            string? pluginDirectory = FindPluginDirectory(pluginsRoot, plugin.DllName);

            if (pluginDirectory == null)
            {
                MessageBox.Show("The plugin files could not be found. Refresh the list and try again.",
                                "Plugin Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Delete '{plugin.Name}'?\n\nThis removes the plugin folder, its manager metadata, and its config file if one exists. This cannot be undone.",
                "Delete Plugin", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                if (!IsPathInsideDirectory(pluginsRoot, pluginDirectory))
                {
                    throw new InvalidOperationException("The plugin folder is outside the managed plugins directory.");
                }

                string archiveMetadataPath = ManagerCache.ArchivePluginMetadataPath(plugin.DllName);
                Directory.Delete(pluginDirectory, recursive: true);

                string metadataPath = ManagerCache.GitHubPluginMetadataPath(plugin.DllName);
                if (File.Exists(metadataPath) && IsPathInsideDirectory(ManagerCache.GitHubDirectory, metadataPath))
                {
                    File.Delete(metadataPath);
                }
                if (File.Exists(archiveMetadataPath) && IsPathInsideDirectory(ManagerCache.TmmDirectory, archiveMetadataPath))
                {
                    File.Delete(archiveMetadataPath);
                }

                string configRoot = Path.Combine(gamePath, "BepInEx", "config");
                if (!string.IsNullOrWhiteSpace(plugin.ConfigPath) && File.Exists(plugin.ConfigPath) &&
                    IsPathInsideDirectory(configRoot, plugin.ConfigPath))
                {
                    File.Delete(plugin.ConfigPath);
                }

                LogToConsole($"Deleted plugin: {plugin.Name}");
                LoadPluginsToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not delete the plugin: {ex.Message}",
                                "Delete Plugin Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string? FindPluginDirectory(string pluginsRoot, string dllName)
        {
            if (!Directory.Exists(pluginsRoot))
            {
                return null;
            }

            foreach (string directory in Directory.GetDirectories(pluginsRoot))
            {
                if (File.Exists(Path.Combine(directory, dllName)))
                {
                    return directory;
                }
            }

            return null;
        }

        private static bool IsPathInsideDirectory(string rootDirectory, string targetPath)
        {
            string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(targetPath);
            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
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
                Owner = this,
                Width = 760,
                Height = 760,
                MinWidth = 560,
                MinHeight = 500,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(19, 19, 19)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            w.Resources[typeof(System.Windows.Controls.Button)] = Resources[typeof(System.Windows.Controls.Button)];
            w.Resources[typeof(System.Windows.Controls.TextBox)] = Resources[typeof(System.Windows.Controls.TextBox)];
            w.Resources[typeof(System.Windows.Controls.TextBlock)] = Resources[typeof(System.Windows.Controls.TextBlock)];
            w.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)];

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(58) });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(62) });
            w.Content = root;

            var header = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 0, 16, 0),
                Child = new System.Windows.Controls.StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new System.Windows.Controls.TextBlock
                        {
                            Text = plugin.Name,
                            FontSize = 18,
                            FontWeight = FontWeights.Bold,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242))
                        },
                        new System.Windows.Controls.TextBlock
                        {
                            Text = "Plugin configuration",
                            FontSize = 12,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 184, 184)),
                            Margin = new Thickness(0, 2, 0, 0)
                        }
                    }
                }
            };
            System.Windows.Controls.Grid.SetRow(header, 0);
            root.Children.Add(header);

            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(14, 4, 14, 14)
            };

            var scrollViewer = new System.Windows.Controls.ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            };
            System.Windows.Controls.Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);

            var tempTab = new BepInExConfigTab(plugin.ConfigPath, panel);
            tempTab.LoadConfig();

            var footer = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 10, 14, 10)
            };
            System.Windows.Controls.Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            var actions = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            footer.Child = actions;

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (s2, e2) => w.Close();
            actions.Children.Add(cancelBtn);

            var saveBtn = new System.Windows.Controls.Button
            {
                Content = "Save Changes",
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0)
            };
            saveBtn.Click += (s2, e2) =>
            {
                if (!tempTab.SaveConfig(showConfirmation: false))
                {
                    MessageBox.Show("The config could not be saved. Check that the file is not in use.", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                LogToConsole($"Saved plugin config: {Path.GetFileName(plugin.ConfigPath)}");
                LoadPluginsToList();
                w.Close();
            };
            actions.Children.Add(saveBtn);

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

                ctx.Items.Add(new System.Windows.Controls.Separator());
                var deleteMod = new System.Windows.Controls.MenuItem
                {
                    Header = "Delete Mod",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 107))
                };
                deleteMod.Click += (s, e) => DeleteTekaTekaMod(m);
                ctx.Items.Add(deleteMod);

                container.ContextMenu = ctx;
            }
        }

        private void DeleteTekaTekaMod(TekaTekaModInfo mod)
        {
            string? modDirectory = Path.GetDirectoryName(mod.ConfigPath);
            string modsRoot = Path.Combine(gamePath, "TekaSongs");

            if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory) ||
                !IsPathInsideDirectory(modsRoot, modDirectory))
            {
                MessageBox.Show("The mod folder could not be found. Refresh the list and try again.",
                                "Mod Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Delete '{mod.Name}'?\n\nThis removes the entire mod folder and cannot be undone.",
                "Delete Mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                string archiveMetadataPath = ManagerCache.ArchiveSongMetadataPath(Path.GetFileName(modDirectory));
                Directory.Delete(modDirectory, recursive: true);
                if (File.Exists(archiveMetadataPath) && IsPathInsideDirectory(ManagerCache.TmmDirectory, archiveMetadataPath))
                {
                    File.Delete(archiveMetadataPath);
                }
                LogToConsole($"Deleted mod: {mod.Name}");
                LoadTekaTekaMods();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not delete the mod: {ex.Message}",
                                "Delete Mod Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
                Owner = this,
                Width = 720,
                Height = 650,
                MinWidth = 540,
                MinHeight = 450,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(19, 19, 19)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            w.Resources[typeof(System.Windows.Controls.Button)] = Resources[typeof(System.Windows.Controls.Button)];
            w.Resources[typeof(System.Windows.Controls.TextBox)] = Resources[typeof(System.Windows.Controls.TextBox)];
            w.Resources[typeof(System.Windows.Controls.TextBlock)] = Resources[typeof(System.Windows.Controls.TextBlock)];
            w.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)];

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(58) });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(62) });
            w.Content = root;

            var header = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 0, 16, 0),
                Child = new System.Windows.Controls.StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new System.Windows.Controls.TextBlock
                        {
                            Text = mod.Name,
                            FontSize = 18,
                            FontWeight = FontWeights.Bold,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 242, 242))
                        },
                        new System.Windows.Controls.TextBlock
                        {
                            Text = "TekaTeka mod configuration",
                            FontSize = 12,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 184, 184)),
                            Margin = new Thickness(0, 2, 0, 0)
                        }
                    }
                }
            };
            System.Windows.Controls.Grid.SetRow(header, 0);
            root.Children.Add(header);

            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(14, 4, 14, 14)
            };

            var scrollViewer = new System.Windows.Controls.ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            };
            System.Windows.Controls.Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);

            var tempTab = new BepInExConfigTab(mod.ConfigPath, panel);
            tempTab.LoadConfig();

            var footer = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 66, 66)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(14, 10, 14, 10)
            };
            System.Windows.Controls.Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            var actions = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            footer.Child = actions;

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (s2, e2) => w.Close();
            actions.Children.Add(cancelBtn);

            var saveBtn = new System.Windows.Controls.Button
            {
                Content = "Save Changes",
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0)
            };
            saveBtn.Click += (s2, e2) =>
            {
                if (!tempTab.SaveConfig(showConfirmation: false))
                {
                    MessageBox.Show("The config could not be saved. Check that the file is not in use.", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                LogToConsole($"Saved mod config: {Path.GetFileName(mod.ConfigPath)}");
                LoadTekaTekaMods();
                w.Close();
            };
            actions.Children.Add(saveBtn);

            w.ShowDialog();
        }

        /// <summary>
        /// Helper method to append log messages to a console UI element.
        /// </summary>
        private void LogToConsole(string message)
        {
            ConsoleLog.AppendText(message + Environment.NewLine);
            ConsoleLog.ScrollToEnd();

            PluginConsoleLog.AppendText(message + Environment.NewLine);
            PluginConsoleLog.ScrollToEnd();
        }

        private void AppendBepInExLog(string logText)
        {
            ConsoleLog.AppendText(logText);
            ConsoleLog.ScrollToEnd();

            PluginConsoleLog.AppendText(logText);
            PluginConsoleLog.ScrollToEnd();
        }
    }
}
