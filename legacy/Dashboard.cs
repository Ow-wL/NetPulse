using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace NetPulse
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            var application = new Application();
            try
            {
                application.Run(new Dashboard(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "결과")).Window);
            }
            catch (Exception ex)
            {
                MessageBox.Show("프로그램을 시작하지 못했습니다.\n\n" + ex.Message,
                    "네트워크 모니터", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal sealed class Dashboard
    {
        private static readonly Target Extender = new Target("Mesh 확장기", "192.0.2.155");
        internal static readonly Target Router = new Target("메인 공유기", "192.0.2.1");
        internal static readonly Target Internet = new Target("외부 인터넷", "1.1.1.1");
        private readonly Target[] targets = { Extender, Router, Internet };
        private readonly Card[] cards;
        private readonly string root;
        private readonly DispatcherTimer ticker;
        private readonly RadioButton meshMode, wifiMode;
        private readonly TextBox minutesBox;
        private readonly Button startButton, stopButton, compareButton, analysisButton, markButton;
        private readonly TextBlock detailText, roundsText, elapsedText, saveText;
        private readonly ProgressBar progress;
        private Session session;
        private CancellationTokenSource stop;
        private bool closeWhenSaved;

        internal readonly Window Window;

        internal Dashboard(string root)
        {
            this.root = root;
            using (var stream = typeof(Dashboard).Assembly.GetManifestResourceStream("Dashboard.xaml"))
                Window = (Window)XamlReader.Load(stream);
            Window.FontFamily = Ui.AppFont;

            meshMode = Find<RadioButton>("MeshMode");
            wifiMode = Find<RadioButton>("WifiMode");
            minutesBox = Find<TextBox>("MinutesBox");
            startButton = Find<Button>("StartButton");
            stopButton = Find<Button>("StopButton");
            compareButton = Find<Button>("CompareButton");
            analysisButton = Find<Button>("AnalysisButton");
            markButton = Find<Button>("MarkButton");
            detailText = Find<TextBlock>("DetailText");
            roundsText = Find<TextBlock>("RoundsText");
            elapsedText = Find<TextBlock>("ElapsedText");
            saveText = Find<TextBlock>("SaveText");
            progress = Find<ProgressBar>("Progress");

            var host = Find<Grid>("CardsHost");
            var accent = Ui.Brush("#0067D8");
            cards = new Card[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                cards[i] = new Card(targets[i], accent, (Style)Window.Resources["Glass"], i);
                host.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(cards[i].View, i);
                if (i > 0) cards[i].View.Margin = new Thickness(11, 0, 0, 0);
                host.Children.Add(cards[i].View);
            }

            ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            ticker.Tick += (sender, args) => UpdateClock();

            var maximize = Find<Button>("MaximizeButton");
            Find<Button>("MinimizeButton").Click += (sender, args) => Window.WindowState = WindowState.Minimized;
            maximize.Click += (sender, args) => Window.WindowState =
                Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            Window.StateChanged += (sender, args) => maximize.Content =
                Window.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            Find<Button>("CloseButton").Click += (sender, args) => Window.Close();
            Window.Closing += OnClosing;

            meshMode.Checked += (sender, args) => UpdateMode();
            wifiMode.Checked += (sender, args) => UpdateMode();
            minutesBox.TextChanged += (sender, args) =>
            {
                int minutes;
                if (session == null && ReadMinutes(out minutes))
                    elapsedText.Text = "00:00 / " + FormatTime(TimeSpan.FromMinutes(minutes));
            };
            startButton.Click += Start;
            stopButton.Click += (sender, args) => RequestStop();
            Find<Button>("FolderButton").Click += (sender, args) => OpenFolder();
            Find<Button>("QuickButton").Click += (sender, args) => OpenQuick();
            compareButton.Click += (sender, args) => OpenComparison();
            analysisButton.Click += (sender, args) => OpenAnalysis();
            markButton.Click += (sender, args) => MarkSymptom();
            UpdateMode();
        }

        private T Find<T>(string name) where T : class { return (T)Window.FindName(name); }
        private bool IsMesh { get { return meshMode.IsChecked == true; } }

        private static string ModeHint(bool mesh)
        {
            return mesh ? "PC가 확장기 LAN을 통해 연결된 상태에서 시작하세요." :
                "메인 공유기 Wi‑Fi를 연결하고, PC가 Wi‑Fi로 통신하는지 확인한 뒤 시작하세요.";
        }

        private void UpdateMode()
        {
            Find<TextBlock>("ModeHint").Text = ModeHint(IsMesh);
            if (session == null) ResetCards(IsMesh);
            else if (stop == null)
            {
                session = null;
                ResetCards(IsMesh);
                progress.Value = 0;
                roundsText.Text = "측정 대기 중";
                int minutes;
                elapsedText.Text = ReadMinutes(out minutes) ? "00:00 / " + FormatTime(TimeSpan.FromMinutes(minutes)) : "00:00";
                detailText.Text = "손실이 감지되면 마지막 손실 시각과 대상을 표시합니다.";
                saveText.Text = "측정 종료 후 끊김 분석에서 회차와 링크 기록을 확인할 수 있습니다.";
                SetState("대기 중", "#7B8FA8");
            }
        }

        private bool ReadMinutes(out int value)
        {
            return int.TryParse(minutesBox.Text, out value) && value >= 1 && value <= 720;
        }

        private void ResetCards(bool mesh)
        {
            for (int i = 0; i < cards.Length; i++) cards[i].Reset(mesh || i > 0);
        }

        private async void Start(object sender, RoutedEventArgs args)
        {
            if (stop != null) return;
            int minutes;
            if (!ReadMinutes(out minutes))
            {
                MessageBox.Show(Window, "1~720분 사이의 정수를 입력하세요.", "측정 시간");
                minutesBox.Focus();
                minutesBox.SelectAll();
                return;
            }

            bool mesh = IsMesh;
            ResetCards(mesh);
            Find<TextBlock>("ModeHint").Text = ModeHint(mesh);
            var current = new Session(root, mesh ? "Mesh" : "WiFi", TimeSpan.FromMinutes(minutes),
                mesh ? targets : targets.Skip(1).ToArray());
            session = current;
            var cancellation = new CancellationTokenSource();
            stop = cancellation;
            SetRunning(true);
            SetState("측정 중", "#07997F");
            detailText.Text = "손실이 감지되면 마지막 손실 시각과 대상을 표시합니다.";
            roundsText.Text = "첫 응답 대기 중";
            saveText.Text = "측정 중 · samples.csv에 매 회차 기록";
            UpdateClock();
            ticker.Start();

            try
            {
                await Task.Run(() => current.RunAsync(cancellation.Token,
                    (slot, time, samples) => Window.Dispatcher.Invoke(() => ShowRound(current, slot, time, samples, mesh))));
                SetState(current.EndReason == "Stopped" ? "중지 및 저장 완료" : "측정 완료", "#07997F");
                saveText.Text = current.Evidence.SaveFailed ? "Ping CSV 저장 완료 · 보조 기록 저장 실패" :
                    "CSV 저장 완료 · 끊김 분석에서 확인하세요.";
            }
            catch (Exception ex)
            {
                SetState("측정 오류", "#BF5B28");
                saveText.Text = "저장 상태를 결과 폴더에서 확인하세요.";
                MessageBox.Show(Window, "측정을 마치지 못했습니다.\n\n" + ex.Message, "측정 오류",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                ticker.Stop();
                UpdateClock();
                stop = null;
                cancellation.Dispose();
                SetRunning(false);
                if (closeWhenSaved) Window.Close();
            }
        }

        private void ShowRound(Session current, long slot, DateTimeOffset time, Sample[] samples, bool mesh)
        {
            if (stop != null && !stop.IsCancellationRequested) markButton.IsEnabled = true;
            var route = current.Evidence.LatestRoute(Internet.Ip);
            var mismatch = current.Stats.Select(stat => DiagnosticReport.RouteNote(current.Mode,
                current.Evidence.LatestRoute(stat.Target.Ip))).FirstOrDefault(note => note.Length > 0);
            Find<TextBlock>("ModeHint").Text = mismatch ?? (route == null ? ModeHint(mesh) :
                "외부 IP Windows 경로 · " + DiagnosticReport.RouteLabel(route));

            var problems = new List<string>();
            for (int i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                var stats = current.Stats[i];
                cards[mesh ? i : i + 1].Update(slot, sample, stats);
                if (sample.Lost || sample.Error)
                    problems.Add(stats.Target.Name + (sample.Error ? " 검사 오류" : " 손실"));
            }
            roundsText.Text = "측정 " + current.Rounds + "회" +
                (current.Skipped > 0 ? "  ·  건너뜀 " + current.Skipped + "회" : "");
            if (problems.Count > 0)
                detailText.Text = "최근 이상 " + time.ToString("HH:mm:ss.fff") + "  ·  " + string.Join(" / ", problems);
        }

        private void UpdateClock()
        {
            if (session == null) return;
            var elapsed = session.Clock.Elapsed;
            if (elapsed > session.Duration) elapsed = session.Duration;
            elapsedText.Text = FormatTime(elapsed) + " / " + FormatTime(session.Duration);
            progress.Value = 100.0 * elapsed.TotalMilliseconds / session.Duration.TotalMilliseconds;
        }

        private static string FormatTime(TimeSpan duration)
        {
            return duration.TotalHours >= 1 ? duration.ToString(@"hh\:mm\:ss") : duration.ToString(@"mm\:ss");
        }

        private void SetRunning(bool running)
        {
            meshMode.IsEnabled = wifiMode.IsEnabled = minutesBox.IsEnabled = startButton.IsEnabled =
                compareButton.IsEnabled = analysisButton.IsEnabled = !running;
            stopButton.IsEnabled = running;
            markButton.IsEnabled = false;
        }

        private void SetState(string text, string color)
        {
            Find<TextBlock>("StateText").Text = text;
            Find<Ellipse>("StateDot").Fill = Ui.Brush(color);
        }

        private void RequestStop()
        {
            if (stop == null || stop.IsCancellationRequested) return;
            stopButton.IsEnabled = false;
            markButton.IsEnabled = false;
            SetState("저장 중", "#BF5B28");
            stop.Cancel();
        }

        private void OnClosing(object sender, CancelEventArgs args)
        {
            if (stop == null) return;
            args.Cancel = true;
            closeWhenSaved = true;
            RequestStop();
        }

        private void OpenFolder()
        {
            try
            {
                string path = session != null && Directory.Exists(session.Folder) ? session.Folder : root;
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(Window, ex.Message, "결과 폴더"); }
        }

        private void MarkSymptom()
        {
            if (stop == null || stop.IsCancellationRequested || session == null) return;
            var time = DateTimeOffset.Now;
            detailText.Text = session.Evidence.Mark(time) ? "증상 시각 저장 · " + time.ToString("HH:mm:ss.fff") :
                "증상 시각을 저장할 수 없습니다. 끊김 시각을 따로 기록해 주세요.";
        }

        private void OpenAnalysis()
        {
            try { new AnalysisWindow(Window, root, session == null ? null : session.Folder).Window.ShowDialog(); }
            catch (Exception ex) { MessageBox.Show(Window, ex.Message, "끊김 분석"); }
        }

        private void OpenQuick()
        {
            try { new QuickWindow(Window).Window.ShowDialog(); }
            catch (Exception ex) { MessageBox.Show(Window, ex.Message, "빠른 진단"); }
        }

        private void OpenComparison()
        {
            try { new Comparison(Window, root).Window.ShowDialog(); }
            catch (Exception ex) { MessageBox.Show(Window, ex.Message, "결과 비교"); }
        }
    }

    internal static class Ui
    {
        internal static readonly FontFamily AppFont = new FontFamily(
            new Uri("pack://application:,,,/" + typeof(Ui).Assembly.GetName().Name + ";component/"),
            "./fonts/#Pretendard, Malgun Gothic");
        internal static readonly SolidColorBrush Ink = Brush("#202124");
        internal static readonly SolidColorBrush Muted = Brush("#555A64");
        internal static readonly SolidColorBrush Loss = Brush("#A94122");

        internal static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        internal static TextBlock Text(string text, double size, Brush color, bool strong = false)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = color,
                FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
        }
    }

    internal sealed class Card
    {
        private readonly TextBlock value, hint, lossRate, average, maximum, streak;
        private readonly Sparkline graph;
        internal readonly Border View;

        internal Card(Target target, SolidColorBrush accent, Style glass, int index)
        {
            var title = Ui.Text(target.Name, 15, Ui.Ink, true);
            var ip = Ui.Text(target.Ip, 12, Ui.Muted);
            ip.Margin = new Thickness(0, 2, 0, 0);
            Typography.SetNumeralAlignment(ip, FontNumeralAlignment.Tabular);
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(title);
            labels.Children.Add(ip);

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(CreateIcon(accent, index));
            titleRow.Children.Add(labels);

            value = Ui.Text("—", 31, Ui.Ink, true);
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            hint = Ui.Text("응답 대기", 12, Ui.Muted);
            var values = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            values.Children.Add(value);
            hint.Margin = new Thickness(9, 0, 0, 6);
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            values.Children.Add(hint);

            graph = new Sparkline(accent) { Height = 55, Margin = new Thickness(0, 4, 0, 10),
                ToolTip = "대상별 지연 축척(ms). 주황색은 손실, 회색은 검사 오류입니다." };
            var metrics = new Grid();
            lossRate = AddMetric(metrics, 0, "손실률");
            average = AddMetric(metrics, 1, "평균 지연");
            maximum = AddMetric(metrics, 2, "최대 지연");
            streak = Ui.Text("", 12, Ui.Muted);
            streak.Margin = new Thickness(0, 11, 0, 0);

            var content = new StackPanel();
            content.Children.Add(titleRow);
            content.Children.Add(values);
            content.Children.Add(graph);
            content.Children.Add(new Border
            {
                BorderBrush = Ui.Brush("#E7E9ED"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 0),
                Child = metrics
            });
            content.Children.Add(streak);
            View = new Border { Style = glass, Child = content };
        }

        private static Border CreateIcon(SolidColorBrush accent, int index)
        {
            string[] icons =
            {
                "M2,2 H7 V7 H2 Z M11,11 H16 V16 H11 Z M7,4.5 H13.5 V11 M4.5,7 V13.5 H11",
                "M2,10 H16 V15 H2 Z M5,10 V6 M13,10 V6 M5,12.5 H5.1 M8,12.5 H8.1 M4,4 Q9,0 14,4",
                "M16,9 A7,7 0 1 1 2,9 A7,7 0 1 1 16,9 M12,9 A3,7 0 1 1 6,9 A3,7 0 1 1 12,9 M2,9 H16"
            };
            var symbol = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(icons[index]), Stroke = accent, StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Color color = accent.Color;
            return new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Child = symbol,
                Margin = new Thickness(0, 0, 10, 0), BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush(Color.FromArgb(12, color.R, color.G, color.B),
                    Color.FromArgb(23, color.R, color.G, color.B), 65)
            };
        }

        private static TextBlock AddMetric(Grid metrics, int column, string label)
        {
            metrics.ColumnDefinitions.Add(new ColumnDefinition());
            var number = Ui.Text("—", 15, Ui.Ink, true);
            number.Margin = new Thickness(0, 3, 0, 0);
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text(label, 11.5, Ui.Muted));
            panel.Children.Add(number);
            Grid.SetColumn(panel, column);
            metrics.Children.Add(panel);
            return number;
        }

        internal void Reset(bool enabled)
        {
            View.Opacity = enabled ? 1.0 : 0.63;
            value.Text = "—";
            value.Foreground = Ui.Ink;
            hint.Text = enabled ? "응답 대기" : "측정 제외";
            lossRate.Text = average.Text = maximum.Text = "—";
            lossRate.Foreground = Ui.Ink;
            streak.Text = "최장 연속 손실 —";
            graph.Reset();
        }

        internal void Update(long slot, Sample sample, TargetStats totals)
        {
            value.Text = sample.Rtt.HasValue ? sample.Rtt.Value + " ms" : sample.Error ? "오류" : "손실";
            value.Foreground = sample.Rtt.HasValue ? Ui.Ink : sample.Error ? Ui.Muted : Ui.Loss;
            hint.Text = sample.Rtt.HasValue ? "최근 응답" : sample.Error ? "검사 실패" : "ICMP";
            lossRate.Text = totals.LossPercent.HasValue ? totals.LossPercent.Value.ToString("0.00") + "%" : "—";
            lossRate.Foreground = totals.Lost > 0 ? Ui.Loss : Ui.Ink;
            average.Text = totals.Average.HasValue ? totals.Average.Value.ToString("0.0") + " ms" : "—";
            maximum.Text = totals.Received > 0 ? totals.Max + " ms" : "—";
            streak.Text = "최장 연속 손실 " + totals.Longest + "회" + (totals.Errors > 0 ? "  ·  오류 " + totals.Errors + "회" : "");
            graph.Add(slot, sample);
        }
    }

    internal sealed class Sparkline : FrameworkElement
    {
        private readonly Pen grid = new Pen(Ui.Brush("#CED6E1"), 0.85);
        private readonly Pen line;
        private readonly Brush accent, fill;
        private readonly Queue<KeyValuePair<long, Sample>> points = new Queue<KeyValuePair<long, Sample>>();

        internal Sparkline(SolidColorBrush accent)
        {
            this.accent = accent;
            line = new Pen(accent, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round };
            Color color = accent.Color;
            fill = new LinearGradientBrush(Color.FromArgb(58, color.R, color.G, color.B),
                Color.FromArgb(5, color.R, color.G, color.B), new Point(0, 0), new Point(0, 1));
        }

        internal void Reset() { points.Clear(); InvalidateVisual(); }

        internal void Add(long slot, Sample sample)
        {
            points.Enqueue(new KeyValuePair<long, Sample>(slot, sample));
            while (points.Peek().Key < slot - 59) points.Dequeue();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double right = ActualWidth - 3;
            double top = 8;
            double bottom = ActualHeight - 8;
            const double left = 29;
            if (right <= left || bottom <= top) return;
            for (int i = 0; i < 3; i++)
            {
                double y = top + (bottom - top) * i / 2;
                dc.DrawLine(grid, new Point(left, y), new Point(right, y));
            }
            if (points.Count == 0) return;

            long first = points.Last().Key - 59;
            double maximum = Math.Max(30, points.Max(p => p.Value.Rtt ?? 0));
            DrawAxis(dc, maximum.ToString(CultureInfo.InvariantCulture), top);
            DrawAxis(dc, "0", bottom);
            var run = new List<Point>();
            long prevSlot = first - 2;
            foreach (var point in points)
            {
                double x = left + (right - left) * (point.Key - first) / 59;
                Sample sample = point.Value;
                if (!sample.Rtt.HasValue || point.Key != prevSlot + 1)
                {
                    DrawRun(dc, run, bottom);
                    run.Clear();
                }
                if (sample.Rtt.HasValue)
                    run.Add(new Point(x, bottom - (bottom - top) * sample.Rtt.Value / maximum));
                else
                    dc.DrawEllipse(sample.Error ? Ui.Muted : Ui.Loss, null, new Point(x, top), 2.5, 2.5);
                prevSlot = point.Key;
            }
            DrawRun(dc, run, bottom);
            if (run.Count > 0) dc.DrawEllipse(accent, new Pen(Brushes.White, 1), run[run.Count - 1], 2.7, 2.7);
        }

        private void DrawAxis(DrawingContext dc, string text, double y)
        {
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Ui.AppFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                10, Ui.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(23 - label.Width, y - label.Height / 2));
        }

        private void DrawRun(DrawingContext dc, List<Point> run, double bottom)
        {
            if (run.Count == 0) return;
            if (run.Count == 1)
            {
                dc.DrawEllipse(accent, null, run[0], 2, 2);
                return;
            }
            var area = new StreamGeometry();
            var curve = new StreamGeometry();
            using (var context = area.Open())
            {
                context.BeginFigure(new Point(run[0].X, bottom), true, true);
                context.PolyLineTo(run, false, false);
                context.LineTo(new Point(run[run.Count - 1].X, bottom), false, false);
            }
            using (var context = curve.Open())
            {
                context.BeginFigure(run[0], false, false);
                context.PolyLineTo(run.Skip(1).ToArray(), true, false);
            }
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, line, curve);
        }
    }
}
