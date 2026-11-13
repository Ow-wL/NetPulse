using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace NetPulseSpike
{
    public partial class App : Application
    {
        private Window window;

        public App()
        {
            InitializeComponent();
            // 진단용 기록. 폴더를 먼저 만들고, 기록 실패가 앱 시작을 막지 않게 한다.
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulseSpike");
            var trace = System.IO.Path.Combine(folder, "C-exceptions.txt");
            try { System.IO.Directory.CreateDirectory(folder); System.IO.File.WriteAllText(trace, ""); } catch { }
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                try { System.IO.File.AppendAllText(trace, DateTime.Now.ToString("HH:mm:ss.fff") + " " + e.Exception.GetType().Name + ": " + e.Exception.Message.Trim() + "\n" + e.Exception.StackTrace + "\n\n"); } catch { }
            };
            UnhandledException += (s, e) =>
            {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "C-crash.txt"), e.Message + "\n" + e.Exception); } catch { }
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
            // --home: 사용자가 고른 흑요석 홈 시안을 재현하는 화면 (D1 확정 전 비교용)
            window = argv.Contains("--home") ? (Window)new HomeWindow(argv) : new MainWindow(SpikeOptions.Parse(argv));
            window.Activate();
        }
    }
}
