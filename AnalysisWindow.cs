using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace NetPulse
{
    internal sealed class AnalysisWindow
    {
        internal readonly Window Window;
        private readonly TextBlock summary, evidenceStatus;
        private readonly ComboBox runs;
        private readonly ListBox timeline;
        private readonly TextBox details;
        private readonly Button copy;
        private DiagnosticReport report;
        private int revision;

        internal AnalysisWindow(Window owner, string root, string selectedFolder)
        {
            Window = new Window
            {
                Owner = owner, Title = "NetPulse · 끊김 분석", WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
                Width = 955, Height = 725, MinWidth = 720, MinHeight = 525, FontSize = 14,
                FontFamily = owner.FontFamily, Foreground = Ui.Ink, Language = owner.Language,
                UseLayoutRounding = true, SnapsToDevicePixels = true, Background = Ui.Brush("#EDF3FB")
            };
            WindowChrome.SetWindowChrome(Window, new WindowChrome
            {
                CaptionHeight = 45, ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(14)
            });
            var backdrop = new Grid { Background = (Brush)owner.Resources["Backdrop"] };
            backdrop.Children.Add(new Border { IsHitTestVisible = false, Background = new RadialGradientBrush(
                Color.FromArgb(142, 161, 187, 255), Color.FromArgb(0, 161, 187, 255))
                { Center = new Point(0.92, 0.14), GradientOrigin = new Point(0.92, 0.14), RadiusX = 0.8, RadiusY = 0.75 } });
            Window.Content = new Border { Child = backdrop, BorderBrush = Ui.Brush("#CFDAEE"), BorderThickness = new Thickness(1) };

            var frame = new DockPanel();
            backdrop.Children.Add(frame);
            var titleBar = new DockPanel { Height = 45, Margin = new Thickness(23, 0, 10, 0) };
            DockPanel.SetDock(titleBar, Dock.Top);
            frame.Children.Add(titleBar);
            var close = new Button { Style = (Style)owner.Resources["CaptionButton"], Content = "\uE8BB", ToolTip = "닫기" };
            System.Windows.Automation.AutomationProperties.SetName(close, "닫기");
            close.Click += (sender, args) => Window.Close();
            DockPanel.SetDock(close, Dock.Right);
            titleBar.Children.Add(close);
            var brand = Ui.Text("NetPulse", 14, Ui.Ink, true);
            brand.VerticalAlignment = VerticalAlignment.Center;
            titleBar.Children.Add(brand);

            var content = new DockPanel { Margin = new Thickness(26, 10, 26, 22) };
            frame.Children.Add(content);
            var header = new StackPanel { Margin = new Thickness(1, 0, 1, 15) };
            header.Children.Add(Ui.Text("끊김 분석", 32, Ui.Ink, true));
            var subtitle = Ui.Text("같은 회차의 Ping과 근처 시각의 Windows 링크 기록을 대조합니다.", 13, Ui.Muted);
            subtitle.Margin = new Thickness(0, 5, 0, 0);
            header.Children.Add(subtitle);
            DockPanel.SetDock(header, Dock.Top);
            content.Children.Add(header);

            var glass = (Style)owner.Resources["Glass"];
            var action = (Style)owner.Resources["Action"];
            var choices = new StackPanel();
            var selector = new DockPanel();
            choices.Children.Add(selector);
            var browse = new Button { Content = "CSV 열기", Style = action, Margin = new Thickness(9, 0, 0, 0) };
            browse.Click += OpenFile;
            DockPanel.SetDock(browse, Dock.Right);
            selector.Children.Add(browse);
            runs = new ComboBox { Style = (Style)owner.Resources["GlassCombo"], MaxDropDownHeight = 285 };
            System.Windows.Automation.AutomationProperties.SetName(runs, "저장된 측정 선택");
            selector.Children.Add(runs);
            summary = Wrapped("저장된 측정을 선택하거나 samples.csv를 여세요.", 13, Ui.Ink, true);
            evidenceStatus = Wrapped("", 12.5, Ui.Muted);
            summary.Margin = new Thickness(0, 9, 0, 0);
            evidenceStatus.Margin = new Thickness(0, 4, 0, 0);
            choices.Children.Add(summary);
            choices.Children.Add(evidenceStatus);
            var selectionPanel = new Border { Style = glass, Child = choices, Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 13) };
            DockPanel.SetDock(selectionPanel, Dock.Top);
            content.Children.Add(selectionPanel);

            var panels = new Grid();
            panels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.88, GridUnitType.Star) });
            panels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            content.Children.Add(panels);

            var left = new DockPanel();
            var timelineTitle = Ui.Text("이상 타임라인", 17, Ui.Ink, true);
            timelineTitle.Margin = new Thickness(0, 0, 0, 7);
            DockPanel.SetDock(timelineTitle, Dock.Top);
            left.Children.Add(timelineTitle);
            timeline = new ListBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                ItemTemplate = TimelineTemplate(), ItemContainerStyle = TimelineStyle()
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(timeline, ScrollBarVisibility.Disabled);
            timeline.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = owner.Resources["SlimScrollBar"];
            timeline.SelectionChanged += (sender, args) => ShowIncident();
            left.Children.Add(timeline);
            panels.Children.Add(new Border { Style = glass, Child = left, Margin = new Thickness(0, 0, 11, 0),
                Padding = new Thickness(15, 13, 12, 13) });

            var right = new DockPanel();
            var detailHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            DockPanel.SetDock(detailHeader, Dock.Top);
            right.Children.Add(detailHeader);
            copy = new Button { Content = "복사", Style = action, IsEnabled = false,
                Padding = new Thickness(11, 3, 11, 3), MinHeight = 29, FontSize = 12.5 };
            DockPanel.SetDock(copy, Dock.Right);
            copy.Click += (sender, args) =>
            {
                try { Clipboard.SetText((report == null ? "" : report.Headline + "\n" + report.LogStatus + "\n\n") + details.Text); }
                catch (System.Runtime.InteropServices.ExternalException)
                { MessageBox.Show(Window, "클립보드를 사용할 수 없습니다. 잠시 후 다시 시도하세요.", "복사"); }
            };
            detailHeader.Children.Add(copy);
            var detailTitle = Ui.Text("회차와 근거", 17, Ui.Ink, true);
            detailTitle.VerticalAlignment = VerticalAlignment.Center;
            detailHeader.Children.Add(detailTitle);
            details = new TextBox
            {
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, FontSize = 13,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0)
            };
            details.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = owner.Resources["SlimScrollBar"];
            System.Windows.Automation.AutomationProperties.SetName(details, "선택한 이상 구간의 상세 근거");
            right.Children.Add(details);
            var detailPanel = new Border { Style = glass, Child = right, Padding = new Thickness(15, 13, 13, 13) };
            Grid.SetColumn(detailPanel, 1);
            panels.Children.Add(detailPanel);

            var saved = Directory.Exists(root) ? Directory.EnumerateDirectories(root)
                .Where(path => File.Exists(Path.Combine(path, "samples.csv")))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "samples.csv")))
                .Select(path => new RunChoice(Path.Combine(path, "samples.csv"))).ToArray() : new RunChoice[0];
            runs.ItemsSource = saved;
            runs.IsEnabled = saved.Length > 0;
            runs.SelectionChanged += async (sender, args) =>
            {
                var selected = runs.SelectedItem as RunChoice;
                if (selected != null) await LoadAsync(selected.Path);
            };
            Window.Closed += (sender, args) => revision++;
            Window.Loaded += (sender, args) =>
            {
                if (saved.Length == 0) return;
                var match = saved.FirstOrDefault(run => Path.GetDirectoryName(run.Path) == selectedFolder);
                runs.SelectedItem = match ?? saved[0];
            };
        }

        private async void OpenFile(object sender, RoutedEventArgs args)
        {
            var dialog = new OpenFileDialog { Title = "NetPulse 측정 CSV 열기", Filter = "CSV 파일 (*.csv)|*.csv",
                FileName = "samples.csv", CheckFileExists = true };
            if (dialog.ShowDialog(Window) != true) return;
            runs.SelectedIndex = -1;
            await LoadAsync(dialog.FileName);
        }

        private async Task LoadAsync(string path)
        {
            int request = ++revision;
            report = null;
            timeline.ItemsSource = null;
            details.Text = "";
            copy.IsEnabled = false;
            evidenceStatus.Text = "";
            summary.Text = "측정 기록을 읽는 중입니다.";
            try
            {
                var loaded = await Task.Run(() => DiagnosticReport.Read(path));
                if (request != revision) return;
                report = loaded;
                summary.Text = report.Headline;
                string file = Path.GetFileName(Path.GetDirectoryName(path));
                evidenceStatus.Text = report.LogStatus + "  ·  " + file;
                timeline.ItemsSource = report.Incidents;
                if (report.Incidents.Count > 0)
                    timeline.SelectedItem = report.Incidents.FirstOrDefault(item => item.Kind == "Loss") ?? report.Incidents[0];
                else ShowIncident();
            }
            catch (Exception ex)
            {
                if (request != revision) return;
                summary.Text = "이 측정 파일을 분석할 수 없습니다.";
                details.Text = ex.Message;
            }
        }

        private void ShowIncident()
        {
            if (report == null) return;
            var incident = timeline.SelectedItem as Incident;
            details.Text = incident == null ? NoIncident() : Describe(incident);
            details.ScrollToHome();
            copy.IsEnabled = true;
        }

        private string NoIncident()
        {
            DateTimeOffset first = report.Frames[0].Time;
            var routes = report.Targets.Select(ip => report.RouteAt(ip, first)).ToArray();
            string warning = routes.Select(route => DiagnosticReport.RouteNote(report.Mode, route))
                .FirstOrDefault(text => text.Length > 0);
            string intro = "손실, 50 ms 이상 지연, 검사 오류가 기록되지 않았습니다.";
            if (report.Skipped > 0) intro += "\n번호가 누락된 회차에서는 상태를 알 수 없습니다.";
            return intro + "\n\n첫 회차 Windows 경로\n" + DescribeRoutes(routes) +
                (warning == null ? "" : "\n" + warning);
        }

        private string Describe(Incident incident)
        {
            var lines = new List<string> { incident.Caption,
                incident.Start.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") };
            if (incident.FirstRound > 0)
                lines.Add(incident.LastRound == incident.FirstRound ? incident.FirstRound + "회차" :
                    incident.FirstRound + "–" + incident.LastRound + "회차");
            if (incident.Item != null && incident.Item.Kind == "RouteChange")
                lines.Add(DiagnosticReport.TargetName(incident.Item.Ip) + " → " + DiagnosticReport.RouteLabel(incident.Item));
            if (incident.Item != null && incident.Item.Kind == "Link")
                lines.Add(LinkLabel(incident.Item));

            var links = report.Context.Where(item => item.Kind == "Link" &&
                item.Time >= incident.Start.AddSeconds(-Evidence.MatchSeconds) &&
                item.Time <= incident.End.AddSeconds(Evidence.MatchSeconds)).OrderBy(item => item.Time).ToArray();
            if (incident.Patterns.Count > 0)
            {
                lines.Add("");
                lines.Add("우선 확인할 항목");
                foreach (Pattern pattern in incident.Patterns.OrderBy(item => item)) lines.Add(Guidance(pattern, links));
            }
            lines.Add("");
            lines.Add("근처 Windows 링크 기록 (±3초)");
            if (links.Length == 0)
                lines.Add(report.Context.Any(item => item.Kind == "LogQuery" && item.Status == "Read")
                    ? "해당 시각의 e2fexpress 27/32 기록을 찾지 못했습니다." : report.LogStatus);
            else
            {
                foreach (var link in links.Take(8)) lines.Add(LinkLabel(link));
                if (links.Length > 8) lines.Add("외 " + (links.Length - 8) + "건");
            }

            var routes = report.Targets.Select(ip => report.RouteAt(ip, incident.Start)).ToArray();
            lines.Add("");
            lines.Add("시작 시각의 Windows 경로");
            lines.Add(DescribeRoutes(routes));
            string warning = routes.Select(route => DiagnosticReport.RouteNote(report.Mode, route))
                .FirstOrDefault(text => text.Length > 0);
            if (warning != null) lines.Add(warning);

            var nearby = report.Frames.Where(frame => incident.FirstRound > 0
                ? frame.Round >= incident.FirstRound - 2 && frame.Round <= incident.LastRound + 2
                : frame.Time >= incident.Start.AddSeconds(incident.Kind == "Mark" ? -5 : -2) && frame.Time <= incident.End.AddSeconds(2)).ToArray();
            lines.Add("");
            lines.Add("주변 Ping 회차");
            if (nearby.Length == 0) lines.Add("해당 시각의 회차가 없습니다.");
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby.Length > 9 && i == 5) { lines.Add("…"); i = nearby.Length - 4; }
                PingFrame item = nearby[i];
                string prefix = item.Round >= incident.FirstRound && item.Round <= incident.LastRound ? "● " : "";
                lines.Add(prefix + item.Time.ToString("HH:mm:ss.fff") + " · " + item.Round + "회차");
                lines.Add(string.Join("   ", report.Targets.Select(ip => DescribeSample(item, ip))));
            }

            return string.Join("\n", lines);
        }

        private string DescribeRoutes(Evidence[] routes)
        {
            return string.Join("\n", report.Targets.Select((ip, index) =>
                DiagnosticReport.TargetName(ip) + " · " + DiagnosticReport.RouteLabel(routes[index])));
        }

        private static string DescribeSample(PingFrame frame, string ip)
        {
            Sample sample;
            string value = !frame.Samples.TryGetValue(ip, out sample) ? "미측정" :
                sample.Rtt.HasValue ? sample.Rtt + " ms" :
                sample.Error ? "검사 오류 (" + sample.Status + ")" : "손실 (" + sample.Status + ")";
            return DiagnosticReport.TargetName(ip) + " " + value;
        }

        private static string LinkLabel(Evidence item)
        {
            return item.Time.ToString("HH:mm:ss.fff") + " · " + item.Provider + " " + item.EventId +
                (item.EventId == 27 ? " 링크 다운" : " 링크 업");
        }

        private static string Guidance(Pattern pattern, Evidence[] links)
        {
            switch (pattern)
            {
                case Pattern.LocalMesh:
                    return links.Any(link => link.EventId == 27)
                        ? "세 대상이 동시에 응답하지 않았고 근처에 유선 링크 다운 기록이 있습니다. PC 랜카드, 케이블, 확장기 LAN 포트를 먼저 확인하세요."
                        : "세 대상이 동시에 응답하지 않았습니다. PC–확장기 LAN과 확장기 자체를 먼저 확인하세요.";
                case Pattern.MeshAfterExtender:
                    return "확장기는 응답했습니다. Mesh 백홀과 메인 공유기 상태를 먼저 확인하세요.";
                case Pattern.LocalWifi:
                    return "메인 공유기와 외부 IP가 함께 응답하지 않았습니다. PC의 Wi‑Fi 연결과 메인 공유기 상태를 먼저 확인하세요.";
                case Pattern.External:
                    return "내부 대상은 응답했습니다. 공유기 이후 경로와 외부 IP의 ICMP 응답을 확인하세요.";
                case Pattern.Incomplete:
                    return "검사 오류·미측정이 있는 회차는 응답 상태를 확정하기 어렵습니다.";
                case Pattern.RouteMismatch:
                    return "Windows 경로가 선택 모드와 다른 회차의 구간 판단은 보류합니다.";
                case Pattern.Spike:
                    return "50 ms 이상 지연이 관측된 대상을 확인하세요. 이 항목은 응답에 성공한 회차입니다.";
                default:
                    return "대상별 ICMP 응답 차이가 있습니다. 단일 구간을 특정하기 어려운 패턴입니다.";
            }
        }

        private static Style TimelineStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 4, 5)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 42.0));
            return style;
        }

        private static DataTemplate TimelineTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new Binding("Caption"));
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            panel.AppendChild(title);
            var time = new FrameworkElementFactory(typeof(TextBlock));
            time.SetBinding(TextBlock.TextProperty, new Binding("TimeLabel"));
            time.SetValue(TextBlock.FontSizeProperty, 12.0);
            time.SetValue(TextBlock.ForegroundProperty, Ui.Muted);
            panel.AppendChild(time);
            return new DataTemplate { VisualTree = panel };
        }

        private static TextBlock Wrapped(string text, double size, Brush color, bool strong = false)
        {
            var block = Ui.Text(text, size, color, strong);
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        private sealed class RunChoice
        {
            internal readonly string Path;
            internal RunChoice(string path) { Path = path; }

            public override string ToString()
            {
                string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path));
                string[] tokens = folder.Split('_');
                DateTime time;
                return tokens.Length >= 3 &&
                    DateTime.TryParseExact(tokens[0] + "_" + tokens[1], "yyyyMMdd_HHmmss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
                    ? time.ToString("yyyy-MM-dd HH:mm:ss") + " · " + DiagnosticReport.ModeName(tokens[2])
                    : folder;
            }
        }
    }
}
