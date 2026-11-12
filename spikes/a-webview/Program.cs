// M0 스파이크 A. 얇은 WinForms 창이 WebView2에 로컬 웹 화면을 띄우고, 측정은 .NET 쪽에서 돈다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NetPulseSpike
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HostForm(SpikeOptions.Parse(args)));
        }
    }

    internal sealed class HostForm : Form
    {
        private const string AppHost = "app.netpulse.invalid";
        private readonly SpikeOptions options;
        private readonly ResultLog log;
        private readonly WebView2 view = new WebView2();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly MeasureLoop loop = new MeasureLoop();
        private bool ready;
        private volatile string probesJson;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left, Right, Top, Bottom; }

        public HostForm(SpikeOptions options)
        {
            this.options = options;
            log = new ResultLog("A-" + RuntimeLabel());
            Text = "NetPulse Spike A";
            ClientSize = new Size(1280, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black;
            view.Dock = DockStyle.Fill;
            view.DefaultBackgroundColor = Color.Transparent;
            Controls.Add(view);
            log.Write("start", "runtime", RuntimeLabel(), "options", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
        }

        private static string RuntimeLabel()
        {
            return RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal) ? "net48" : "net10";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 창 뒤 재질(가산점 항목). 22621 이상에서 Mica, 다크 프레임.
            int dark = options.Theme == "light" ? 0 : 1;
            int mica = 2;
            int r1 = DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            int r2 = DwmSetWindowAttribute(Handle, 38, ref mica, 4);
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            int r3 = DwmExtendFrameIntoClientArea(Handle, ref margins);
            log.Write("dwm", "darkMode", r1, "backdrop", r2, "extend", r3);
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var settings = WindowsSettings.Read();
            log.Write("windowsSettings", "values", string.Join(",", settings.Select(p => p.Key + "=" + p.Value)));

            var envOptions = new CoreWebView2EnvironmentOptions();
            if (options.CdpPort > 0)
                envOptions.AdditionalBrowserArguments = "--remote-debugging-port=" + options.CdpPort.ToString(CultureInfo.InvariantCulture);
            // 사용자 데이터는 실행 폴더가 아니라 LocalAppData에 둔다 (쓰기 금지 폴더에서 시작, P7).
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(log.Folder, "WebView2"), envOptions);
            await view.EnsureCoreWebView2Async(env);
            var core = view.CoreWebView2;
            log.Write("webview2", "browserVersion", env.BrowserVersionString);

            core.Settings.AreDevToolsEnabled = options.CdpPort > 0;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(AppHost,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
            // 신뢰 경계: 앱 화면 밖으로의 이동과 새 창을 막는다.
            core.NavigationStarting += (s, a) =>
            {
                if (!a.Uri.StartsWith("https://" + AppHost + "/", StringComparison.Ordinal))
                {
                    a.Cancel = true;
                    log.Write("blockedNavigation", "scheme", new Uri(a.Uri).Scheme);
                }
            };
            core.NewWindowRequested += (s, a) => { a.Handled = true; };
            core.WebMessageReceived += OnWebMessage;

            // 회차마다 예정·실제 시각을 남긴다 (P4를 원본에서 다시 계산할 수 있게).
            loop.RoundCompleted += round => log.Write("round", "index", round.Index, "scheduledMs", Math.Round(round.ScheduledMs, 1),
                "actualMs", Math.Round(round.ActualMs, 1), "rttMs", round.RttMs.HasValue ? (object)round.RttMs.Value : null);
            loop.RoundCompleted += round => BeginInvokeSafe(() => SendRound(round));
            _ = loop.RunAsync(stop.Token);
            _ = Task.Run(() =>
            {
                var probes = Probes.RunAll();
                log.Write("probes", "values", string.Join(",", probes.Select(p => p.Key + "=" + p.Value)));
                probesJson = "{\"type\":\"probes\",\"text\":" + ResultLog.Json(ProbeSummary.ForDisplay(probes)) + "}";
                BeginInvokeSafe(() => { if (ready) Post(probesJson); });
            });

            var query = new List<string> { "theme=" + options.Theme };
            if (options.ReduceMotion || settings["clientAreaAnimation"] == "off") query.Add("reduceMotion=1");
            if (options.ReduceTransparency || settings["enableTransparency"] == "0") query.Add("reduceTransparency=1");
            if (options.Bench) query.Add("bench=1");
            if (options.GlassTest) query.Add("glassTest=1");
            core.Navigate("https://" + AppHost + "/index.html?" + string.Join("&", query));

            if (options.HideTest) _ = RunHideTestAsync();
            if (options.RunSeconds > 0) _ = CloseAfterAsync(options.RunSeconds);
        }

        private async Task RunHideTestAsync()
        {
            await Task.Delay(10000);
            long before = loop.Rounds, missedBefore = loop.Missed;
            log.Write("hide", "rounds", before);
            Hide();
            await Task.Delay(30000);
            Show();
            log.Write("show", "rounds", loop.Rounds, "roundsWhileHidden", loop.Rounds - before, "missedWhileHidden", loop.Missed - missedBefore,
                "latestIndex", loop.Latest == null ? -1 : loop.Latest.Index);
            if (loop.Latest != null) SendRound(loop.Latest);
        }

        private async Task CloseAfterAsync(int seconds)
        {
            await Task.Delay(seconds * 1000);
            Close();
        }

        private void BeginInvokeSafe(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void SendRound(Round r)
        {
            if (!ready) return;
            var sb = new StringBuilder("{\"type\":\"round\",\"index\":");
            sb.Append(r.Index).Append(",\"late\":").Append(ResultLog.Json(Math.Round(r.ActualMs - r.ScheduledMs, 1)))
              .Append(",\"rtt\":").Append(r.RttMs.HasValue ? r.RttMs.Value.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"series\":[").Append(string.Join(",", r.Series.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))).Append("]}");
            Post(sb.ToString());
        }

        private void Post(string json)
        {
            try { view.CoreWebView2?.PostWebMessageAsJson(json); } catch (InvalidOperationException) { }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // 신뢰 경계: 앱 출처에서 온 메시지만, 정해진 형식만 받는다.
            if (!e.Source.StartsWith("https://" + AppHost + "/", StringComparison.Ordinal)) { log.Write("rejectedMessage", "reason", "origin"); return; }
            string text;
            try { text = e.TryGetWebMessageAsString(); } catch (ArgumentException) { log.Write("rejectedMessage", "reason", "type"); return; }
            string[] parts = text.Split('|');
            switch (parts[0])
            {
                case "ready":
                    ready = true;
                    log.Write("ready", "msSinceProcessStart", Math.Round(StartupClock.MillisecondsSinceProcessStart()));
                    if (loop.Latest != null) SendRound(loop.Latest);
                    if (probesJson != null) Post(probesJson);
                    break;
                case "frames" when parts.Length == 5:
                    log.Write("frames", "label", parts[1], "avgFps", double.Parse(parts[2], CultureInfo.InvariantCulture),
                        "p95Ms", double.Parse(parts[3], CultureInfo.InvariantCulture), "count", int.Parse(parts[4], CultureInfo.InvariantCulture));
                    break;
                case "mark" when parts.Length == 2 && parts[1].Length <= 40:
                    log.Write("mark", "label", parts[1]);
                    break;
                default:
                    log.Write("rejectedMessage", "reason", "unknown");
                    break;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            stop.Cancel();
            log.Write("end", "rounds", loop.Rounds, "missed", loop.Missed, "maxLateMs", Math.Round(loop.MaxLateMs, 1));
            log.Dispose();
            base.OnFormClosing(e);
        }
    }
}
