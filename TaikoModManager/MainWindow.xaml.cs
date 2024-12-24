using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace TaikoModManager
{
    public partial class MainWindow : Window
    {
        private string gamePath;
        private string bepInExConfigPath;
        private const string BepInExFolderName = "BepInEx";

        // BepInEx config tab manager, or null if not initialized
        private BepInExConfigTab configTab;

        // Our plugin manager
        private PluginsTab _pluginsTab;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1) Detect the game path
            gamePath = Utilities.DetectGamePath(); // Implement your own logic here
            if (string.IsNullOrEmpty(gamePath))
            {
                LogToConsole("Game path could not be detected. Please configure manually.");
                return;
            }

            LogToConsole($"Game detected at: {gamePath}");

            // 2) Check for BepInEx existence
            string bepInExPath = Path.Combine(gamePath, BepInExFolderName);
            bepInExConfigPath = Path.Combine(bepInExPath, "config", "BepInEx.cfg");

            if (!Directory.Exists(bepInExPath))
            {
                LogToConsole("BepInEx not found. Installing now...");
                await InstallBepInExAsync(gamePath);
            }

            if (!File.Exists(bepInExConfigPath))
            {
                LogToConsole("BepInEx.cfg not found. Run the game at least once to generate it.");
            }

            // 3) Create our plugin manager and load existing plugins
            _pluginsTab = new PluginsTab(gamePath);
            LoadPluginsToList();

            // 4) Optionally check for plugin updates on startup
            await _pluginsTab.CheckForUpdates(LogToConsole);
        }

        /// <summary>
        /// Downloads and extracts BepInEx if missing.
        /// Adjust the URL/version for your environment.
        /// </summary>
        private async Task InstallBepInExAsync(string gamePath)
        {
            string bepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip";
            string tempZipPath = Path.Combine(Path.GetTempPath(), "BepInEx.zip");

            try
            {
                LogToConsole($"Downloading BepInEx from: {bepInExUrl}");

                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(bepInExUrl);
                    response.EnsureSuccessStatusCode();
                    await File.WriteAllBytesAsync(tempZipPath, await response.Content.ReadAsByteArrayAsync());
                }

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

        /// <summary>
        /// Switch to the Plugins tab in the UI.
        /// </summary>
        private void SwitchToPluginsTab(object sender, RoutedEventArgs e)
        {
            PluginsTab.Visibility = Visibility.Visible;
            BepInExConfigTab.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Switch to the BepInEx Config tab in the UI.
        /// </summary>
        private void SwitchToBepInExConfigTab(object sender, RoutedEventArgs e)
        {
            PluginsTab.Visibility = Visibility.Collapsed;
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

        /// <summary>
        /// Opens the game directory in Explorer.
        /// </summary>
        private void OpenGameDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(gamePath))
            {
                MessageBox.Show("Game path is not set. Please ensure the game is properly configured.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                LogToConsole("Game path is null or empty.");
                return;
            }

            if (!Directory.Exists(gamePath))
            {
                MessageBox.Show($"The game directory does not exist:\n{gamePath}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                LogToConsole($"Game directory not found: {gamePath}");
                return;
            }

            try
            {
                string normalizedGamePath = gamePath.Replace(@"\\", @"\").Replace(@"/", @"\");
                System.Diagnostics.Process.Start("explorer.exe", normalizedGamePath);
                LogToConsole($"Game directory opened: {normalizedGamePath}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while trying to open the game directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                LogToConsole($"Error opening game directory: {ex.Message}");
            }
        }

        /// <summary>
        /// Launches the game EXE from the detected game path.
        /// </summary>
        private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
        {
            string gameExePath = Path.Combine(gamePath, "Taiko no Tatsujin Rhythm Festival.exe");
            if (File.Exists(gameExePath))
            {
                try
                {
                    System.Diagnostics.Process.Start(gameExePath);
                    LogToConsole("Game launched successfully.");
                }
                catch (Exception ex)
                {
                    LogToConsole($"Error launching game: {ex.Message}");
                }
            }
            else
            {
                LogToConsole("Game executable not found. Please verify the game path.");
            }
        }

        /// <summary>
        /// When the user clicks "Install Plugin", prompt for a GitHub URL, then install.
        /// </summary>
        private async void InstallPluginButton_Click(object sender, RoutedEventArgs e)
        {
            string githubUrl = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter the GitHub repository URL of the plugin:",
                "Install Plugin",
                "https://github.com/");

            if (string.IsNullOrWhiteSpace(githubUrl))
            {
                LogToConsole("Installation canceled: No URL provided.");
                return;
            }

            LogToConsole($"Starting plugin installation from: {githubUrl}");
            await _pluginsTab.InstallPlugin(githubUrl);
            LoadPluginsToList(); // refresh
        }

        private async void UpdatePluginsButton_Click(object sender, RoutedEventArgs e)
        {
            LogToConsole("Checking for plugin updates...");
            // We'll call a new method in PluginsTab that handles the actual updates
            await _pluginsTab.UpdateAllPluginsAsync(message => LogToConsole(message));

            // After updates, re-load the plugin list
            LogToConsole("Update process finished. Reloading plugin list...");
            LoadPluginsToList();
        }


        /// <summary>
        /// Enumerates plugins (Name + Description) and binds them to the PluginList ListView.
        /// </summary>
        private void LoadPluginsToList()
        {
            if (_pluginsTab == null) return;

            var pluginInfos = _pluginsTab.LoadPluginsDetailed();
            PluginList.ItemsSource = pluginInfos; // Bind the List<PluginInfo> to the ListView
        }

        /// <summary>
        /// Saves changes to BepInEx config, if available.
        /// </summary>
        private void SaveConfigChanges(object sender, RoutedEventArgs e)
        {
            if (configTab == null)
            {
                LogToConsole("ConfigTab is not initialized. Please load the BepInExConfigTab first.");
                return;
            }

            configTab.SaveConfig();
            LogToConsole("Configuration saved successfully.");
        }

        /// <summary>
        /// Appends a message to the console log text box.
        /// </summary>
        private void LogToConsole(string message)
        {
            ConsoleLog.AppendText(message + "\n");
            ConsoleLog.ScrollToEnd();
        }
    }
}
