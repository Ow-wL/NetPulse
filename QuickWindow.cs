using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace NetPulse
{
    internal sealed class QuickWindow
    {
        internal readonly Window Window;
        private readonly Window owner;
        private readonly TextBlock headline, advice, operation, siteResult;
        private readonly StackPanel results;
        private readonly Button run, copy, flush, siteCheck;
        private readonly TextBox site;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private QuickReport report;
        private bool busy;

        internal QuickWindow(Window owner)
        {
            this.owner = owner;
            Window = new Window
            {
                Owner = owner, Title = "NetPulse · 빠른 진단", WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize, Width = 870, Height = 742,
                MinWidth = 610, MinHeight = 480, FontSize = 14, FontFamily = owner.FontFamily,
                Foreground = Ui.Ink, Language = owner.Language, Background = (Brush)owner.Resources["Backdrop"],
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            WindowChrome.SetWindowChrome(Window, new WindowChrome
            {
                CaptionHeight = 44, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0)
            });
            Window.Closed += (sender, args) =>
            {
                lifetime.Cancel();
                if (!busy) lifetime.Dispose();
            };

            var frame = new DockPanel();
            Window.Content = new Border { BorderBrush = Ui.Brush("#D9DDE3"), BorderThickness = new Thickness(1), Child = frame };
            var bar = new DockPanel { Height = 44, Margin = new Thickness(22, 0, 10, 0) };
            DockPanel.SetDock(bar, Dock.Top);
            frame.Children.Add(bar);
            var close = new Button { Style = (Style)owner.Resources["CaptionButton"], Content = "\uE8BB",
                ToolTip = "닫기", VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(close, "닫기");
            close.Click += (sender, args) => Window.Close();
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);
            var brand = Ui.Text("NetPulse", 13, Ui.Muted, true);
            brand.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(brand);

            var footer = new DockPanel { Margin = new Thickness(28, 10, 28, 19) };
            DockPanel.SetDock(footer, Dock.Bottom);
            frame.Children.Add(footer);
            copy = Action("결과 복사", false, Copy);
            copy.IsEnabled = false;
            DockPanel.SetDock(copy, Dock.Right);
            footer.Children.Add(copy);
            operation = Wrap("진단은 현재 Windows 연결을 사용합니다.", 12, Ui.Muted);
            operation.VerticalAlignment = VerticalAlignment.Center;
            operation.Margin = new Thickness(0, 0, 10, 0);
            footer.Children.Add(operation);

            var content = new StackPanel { Margin = new Thickness(28, 15, 28, 7) };
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = owner.Resources["SlimScrollBar"];
            frame.Children.Add(scroll);

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
            run = Action("진단 실행", true, Run);
            run.VerticalAlignment = VerticalAlignment.Center;
            run.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(run, Dock.Right);
            header.Children.Add(run);
            var title = new StackPanel();
            title.Children.Add(Ui.Text("빠른 진단", 34, Ui.Ink, true));
            var sub = Wrap("어댑터부터 웹 연결까지 단계별로 점검합니다.", 14, Ui.Muted);
            sub.Margin = new Thickness(0, 5, 0, 0);
            title.Children.Add(sub);
            header.Children.Add(title);
            content.Children.Add(header);

            var summary = new StackPanel();
            headline = Wrap("연결 상태를 점검하세요", 20, Ui.Ink, true);
            advice = Wrap("진단 실행을 누르면 게이트웨이, 외부 IPv4, DNS, HTTPS와 네트워크 로그인 응답을 확인합니다.", 13, Ui.Muted);
            advice.Margin = new Thickness(0, 6, 0, 0);
            summary.Children.Add(headline);
            summary.Children.Add(advice);
            content.Children.Add(Panel(summary, 14));

            results = new StackPanel();
            var empty = Wrap("검사 결과가 여기에 표시됩니다.", 13, Ui.Muted);
            empty.Margin = new Thickness(3, 6, 3, 6);
            results.Children.Add(empty);
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(221) });
            var checks = Panel(results, 0);
            checks.VerticalAlignment = VerticalAlignment.Top;
            checks.Margin = new Thickness(0, 0, 12, 0);
            columns.Children.Add(checks);
            var tools = new StackPanel();
            Grid.SetColumn(tools, 1);
            columns.Children.Add(tools);
            content.Children.Add(columns);

            var help = new StackPanel();
            help.Children.Add(Ui.Text("연결 도구", 16, Ui.Ink, true));
            var controls = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            controls.Children.Add(Action("네트워크 설정", false, (sender, args) => OpenSettings("ms-settings:network-status")));
            controls.Children.Add(Action("Wi‑Fi 설정", false, (sender, args) => OpenSettings("ms-settings:network-wifi")));
            controls.Children.Add(Action("프록시 설정", false, (sender, args) => OpenSettings("ms-settings:network-proxy")));
            flush = Action("DNS 캐시 비우기", false, FlushDns);
            controls.Children.Add(flush);
            help.Children.Add(controls);
            tools.Children.Add(Panel(help, 12));

            var sitePanel = new StackPanel();
            sitePanel.Children.Add(Ui.Text("사이트 점검", 16, Ui.Ink, true));
            site = new TextBox
            {
                Margin = new Thickness(0, 8, 0, 7), Padding = new Thickness(7, 5, 7, 5), FontSize = 13,
                Background = Ui.Brush("#FAFBFC"), BorderBrush = Ui.Brush("#E2E5EA"),
                BorderThickness = new Thickness(1), MaxLength = 253,
                ToolTip = "예: example.com"
            };
            System.Windows.Automation.AutomationProperties.SetName(site, "점검할 도메인");
            sitePanel.Children.Add(site);
            siteCheck = Action("DNS와 HTTPS 확인", false, CheckSite);
            sitePanel.Children.Add(siteCheck);
            siteResult = Wrap("도메인 입력 · 예: example.com", 12, Ui.Muted);
            siteResult.Margin = new Thickness(0, 5, 0, 0);
            sitePanel.Children.Add(siteResult);
            tools.Children.Add(Panel(sitePanel, 0));
        }

        private async void Run(object sender, RoutedEventArgs args) { await Check(); }

        private async Task Check(string notice = null)
        {
            if (busy) return;
            busy = true;
            SetControls(false);
            headline.Text = "연결을 확인하는 중입니다";
            advice.Text = "각 검사 대상의 응답을 기다리고 있습니다.";
            operation.Text = notice ?? "검사 중";
            try
            {
                QuickReport previous = report;
                report = await QuickDiagnostics.RunAsync(lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                Render(report);
                operation.Text = notice ?? "검사 완료 · " + report.Time.ToString("HH:mm:ss");
                if (previous != null)
                {
                    int changed = 0;
                    for (int i = 0; i < report.Checks.Length; i++)
                        if (report.Checks[i].Level != previous.Checks[i].Level) changed++;
                    operation.Text += changed == 0 ? " · 이전 검사와 판정 동일" : " · 이전 검사 대비 " + changed + "개 판정 변경";
                }
                run.Content = "다시 검사";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (lifetime.IsCancellationRequested) return;
                headline.Text = "진단을 마치지 못했습니다";
                advice.Text = ex.Message;
                operation.Text = "다시 검사할 수 있습니다.";
            }
            finally
            {
                busy = false;
                if (lifetime.IsCancellationRequested) lifetime.Dispose();
                else SetControls(true);
            }
        }

        private void Render(QuickReport value)
        {
            headline.Text = value.Headline;
            advice.Text = value.Advice;
            results.Children.Clear();
            for (int i = 0; i < value.Checks.Length; i++)
            {
                var check = value.Checks[i];
                if (i > 0) results.Children.Add(new Border { Background = Ui.Brush("#E7E9ED"), Height = 1 });
                var row = new DockPanel { Margin = new Thickness(1, 9, 1, 9) };
                var color = check.Level == CheckLevel.Pass ? Ui.Brush("#177246") :
                    check.Level == CheckLevel.Review ? Ui.Brush("#8F5200") :
                    check.Level == CheckLevel.Fail ? Ui.Brush("#AB362A") : Ui.Muted;
                var status = Ui.Text(check.Status, 12, color, true);
                status.Margin = new Thickness(12, 2, 0, 0);
                DockPanel.SetDock(status, Dock.Right);
                row.Children.Add(status);
                var info = new StackPanel();
                info.Children.Add(Ui.Text(check.Name, 14, Ui.Ink, true));
                var detail = Wrap(check.Result, 12.5, Ui.Muted);
                detail.Margin = new Thickness(0, 3, 0, 0);
                info.Children.Add(detail);
                row.Children.Add(info);
                results.Children.Add(row);
            }
        }

        private void SetControls(bool enabled)
        {
            run.IsEnabled = flush.IsEnabled = siteCheck.IsEnabled = site.IsEnabled = enabled;
            copy.IsEnabled = enabled && report != null;
        }

        private void Copy(object sender, RoutedEventArgs args)
        {
            if (report == null) return;
            try { Clipboard.SetText(report.CopyText()); operation.Text = "진단 결과를 복사했습니다."; }
            catch (System.Runtime.InteropServices.ExternalException) { operation.Text = "클립보드를 사용할 수 없습니다."; }
        }

        private async void CheckSite(object sender, RoutedEventArgs args)
        {
            if (busy) return;
            string domain = site.Text.Trim().TrimEnd('.');
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.IndexOf('.') < 1)
            {
                siteResult.Text = "주소 대신 도메인만 입력하세요. 예: example.com";
                site.Focus();
                return;
            }
            busy = true;
            SetControls(false);
            siteResult.Text = "사이트에 연결하는 중입니다.";
            try
            {
                string result = await QuickDiagnostics.CheckSiteAsync(domain, lifetime.Token);
                if (!lifetime.IsCancellationRequested) siteResult.Text = result;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested) siteResult.Text = "검사를 마치지 못했습니다 · " + ex.Message;
            }
            finally
            {
                busy = false;
                if (lifetime.IsCancellationRequested) lifetime.Dispose();
                else SetControls(true);
            }
        }

        private void OpenSettings(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { operation.Text = "Windows 설정을 열지 못했습니다 · " + ex.Message; }
        }

        private async void FlushDns(object sender, RoutedEventArgs args)
        {
            if (busy || MessageBox.Show(Window, "Windows DNS 캐시를 비우고 연결을 다시 검사할까요?",
                "DNS 캐시 비우기", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            busy = true;
            SetControls(false);
            operation.Text = "Windows DNS 캐시를 비우는 중입니다.";
            string message;
            try { message = await Task.Run(() => RunFlushDns()); }
            catch (Exception ex) { message = "DNS 캐시 명령 실패 · " + ex.Message; }
            busy = false;
            if (lifetime.IsCancellationRequested) { lifetime.Dispose(); return; }
            await Check(message);
        }

        private static string RunFlushDns()
        {
            var encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), "/flushdns")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = encoding, StandardErrorEncoding = encoding
            };
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000)) return "DNS 캐시 명령의 응답 시간이 초과됐습니다.";
                string message = (error.GetAwaiter().GetResult() + "\n" + output.GetAwaiter().GetResult()).Trim();
                return process.ExitCode == 0 ? "DNS 캐시 명령 완료 · 재검사 결과를 확인하세요." :
                    "DNS 캐시 명령 실패 (" + process.ExitCode + ") · " + message;
            }
        }

        private Button Action(string label, bool primary, RoutedEventHandler handler)
        {
            var button = new Button { Content = label, Style = (Style)owner.Resources["Action"],
                Margin = new Thickness(0, 0, 7, 3) };
            if (primary)
            {
                button.Background = (Brush)owner.Resources["Blue"];
                button.BorderBrush = button.Background;
                button.Foreground = Brushes.White;
            }
            button.Click += handler;
            return button;
        }

        private Border Panel(UIElement content, int gap)
        {
            return new Border { Style = (Style)owner.Resources["Glass"], Padding = new Thickness(16, 12, 16, 12),
                Child = content, Margin = new Thickness(0, 0, 0, gap) };
        }

        private static TextBlock Wrap(string text, double size, Brush color, bool strong = false)
        {
            var block = Ui.Text(text, size, color, strong);
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }
    }
}
