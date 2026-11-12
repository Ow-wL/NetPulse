using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace NetPulseSpike
{
    public partial class App : Application
    {
        private MainWindow window;

        public App()
        {
            InitializeComponent();
            var trace = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulseSpike", "C-exceptions.txt");
            System.IO.File.WriteAllText(trace, "");
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                try { System.IO.File.AppendAllText(trace, DateTime.Now.ToString("HH:mm:ss.fff") + " " + e.Exception.GetType().Name + ": " + e.Exception.Message.Trim() + "\n" + e.Exception.StackTrace + "\n\n"); } catch { }
            };
            UnhandledException += (s, e) =>
            {
                var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulseSpike", "C-crash.txt");
                System.IO.File.WriteAllText(path, e.Message + "\n" + e.Exception);
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            window = new MainWindow(SpikeOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()));
            window.Activate();
        }
    }
}
