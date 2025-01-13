using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace UpdateHelper
{
    class Program
    {
        static async System.Threading.Tasks.Task Main(string[] args)
        {
            string repoUrl = "https://api.github.com/repos/cainan-c/TaikoModManager/releases/latest";
            string exeDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            string tempDirectory = Path.Combine(exeDirectory, "Update_Temp");
            string updateFilePath = Path.Combine(tempDirectory, "TaikoModManager.7z");
            string sevenZipPath = Path.Combine(exeDirectory, "7z", "7z.exe");
            string mainAppPath = Path.Combine(exeDirectory, "TaikoModManager.exe");

            try
            {
                Console.WriteLine("Starting update process...");

                // Step 1: Clean up temporary directory
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, true);

                Directory.CreateDirectory(tempDirectory);

                // Step 2: Fetch the latest release info
                Console.WriteLine("Fetching latest release information...");
                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateHelper/1.0");

                string response = await client.GetStringAsync(repoUrl);
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                var assets = doc.RootElement.GetProperty("assets").EnumerateArray();

                string downloadUrl = null;
                foreach (var asset in assets)
                {
                    string assetName = asset.GetProperty("name").GetString();
                    if (assetName.EndsWith(".7z"))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    throw new Exception("No .7z file found in the latest release.");
                }

                // Step 3: Download the update file
                Console.WriteLine("Downloading update...");
                await DownloadFileAsync(downloadUrl, updateFilePath);
                Console.WriteLine("Download complete.");

                // Step 4: Extract the .7z file
                Console.WriteLine("Extracting update...");
                ExtractWith7Zip(updateFilePath, exeDirectory, sevenZipPath);

                // Step 5: Clean up temporary files
                Console.WriteLine("Cleaning up...");
                Directory.Delete(tempDirectory, true);

                // Step 6: Restart the application
                Console.WriteLine("Restarting application...");
                Process.Start(mainAppPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during update: {ex.Message}");
            }
        }

        private static async System.Threading.Tasks.Task DownloadFileAsync(string url, string filePath)
        {
            using HttpClient client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);

            // Forcefully flush and release the file handle
            fileStream.Flush();
            fileStream.Close();

            Console.WriteLine($"File downloaded to {filePath}");
        }

        private static void ExtractWith7Zip(string archivePath, string outputPath, string sevenZipPath)
        {
            if (!File.Exists(sevenZipPath))
            {
                throw new FileNotFoundException("7z.exe not found. Ensure it is located in the 'res' folder.");
            }

            Console.WriteLine($"Starting extraction of: {archivePath} to {outputPath}");

            var processStartInfo = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments = $"x \"{archivePath}\" -o\"{outputPath}\" -y -xr!UpdateHelper.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                throw new Exception("Failed to start 7z extraction process.");
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            Console.WriteLine(output);
            if (!string.IsNullOrEmpty(error))
            {
                Console.WriteLine("7z Error Output:");
                Console.WriteLine(error);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"7z extraction failed with exit code {process.ExitCode}. Error details: {error}");
            }

            Console.WriteLine("Extraction completed successfully.");
        }
    }
}
