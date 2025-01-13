using System;
using System.Windows;

namespace TaikoModManager
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            string incomingUrl = null;
            if (e.Args.Length > 0 && e.Args[0].StartsWith("taikomodmanager:", StringComparison.OrdinalIgnoreCase))
            {
                incomingUrl = e.Args[0].Replace("taikomodmanager:", "").Trim();
            }
            MainWindow main = new MainWindow(incomingUrl);
            main.Show();
        }

    }
}
