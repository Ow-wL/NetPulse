// M0 스파이크 C. A안과 같은 시험 장면을 WinUI 3로 만든다. 측정 루프와 진단 확인은 공용 코드를 쓴다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace NetPulseSpike
{
    public sealed class MainWindow : Window
    {
        private sealed class Palette
        {
            public Color Bg, Ink, Muted, Accent, Tint, Edge, OrbA, OrbB, OrbC;
            public Color[] Lines;
        }

        private static readonly Palette Dark = new Palette
        {
            Bg = C("#0B1020"), Ink = C("#EEF2FF"), Muted = C("#A7B0C8"), Accent = C("#5EEAD4"), Tint = C("#12182C"),
            Edge = C("#29FFFFFF"), OrbA = C("#22D3EE"), OrbB = C("#8B5CF6"), OrbC = C("#0EA5E9"),
            Lines = new[] { C("#5EEAD4"), C("#A78BFA"), C("#FB7185") }
        };
        private static readonly Palette Light = new Palette
        {
            Bg = C("#EEF2F8"), Ink = C("#111827"), Muted = C("#4B5563"), Accent = C("#0F766E"), Tint = C("#FFFFFF"),
            Edge = C("#B3FFFFFF"), OrbA = C("#67E8F9"), OrbB = C("#C4B5FD"), OrbC = C("#93C5FD"),
            Lines = new[] { C("#0E7490"), C("#7C3AED"), C("#BE123C") }
        };

        private readonly SpikeOptions options;
        private readonly ResultLog log = new ResultLog("C");
        private readonly MeasureLoop loop = new MeasureLoop();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Grid root = new Grid();
        private readonly Rectangle background = new Rectangle();
        private readonly List<Ellipse> orbs = new List<Ellipse>();
        private readonly List<Border> glassContent = new List<Border>(), glassControl = new List<Border>(), glassFloat = new List<Border>();
        private readonly List<TextBlock> inkText = new List<TextBlock>(), mutedText = new List<TextBlock>();
        private readonly Canvas chart = new Canvas();
        private readonly TranslateTransform chartShift = new TranslateTransform();
        private readonly Polyline[] lines = new Polyline[3];
        private readonly List<double>[] series = { new List<double>(), new List<double>(), new List<double>() };
        private readonly Grid pageHost = new Grid();
        private Grid monitorPage, diagnosePage;
        private TextBlock roundText, latencyText, lateText, statusText, probesText;
        private Palette palette;
        private bool ready;
        private string probes;
        private readonly Stopwatch frameClock = Stopwatch.StartNew();
        private double lastArrivalMs, lastFrameMs;
        private List<double> frameTimes;
        private const int WindowSize = 60;

        private static Color C(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = 255; int i = 0;
            if (hex.Length == 8) { a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber); i = 2; }
            return Color.FromArgb(a, byte.Parse(hex.Substring(i, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(i + 2, 2), NumberStyles.HexNumber), byte.Parse(hex.Substring(i + 4, 2), NumberStyles.HexNumber));
        }

        public MainWindow(SpikeOptions options)
        {
            this.options = options;
            Title = "NetPulse Spike C";
            AppWindow.Resize(new SizeInt32(1280, 840));
            log.Write("start", "runtime", "winui", "options", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
            var settings = WindowsSettings.Read();
            log.Write("windowsSettings", "values", string.Join(",", settings.Select(p => p.Key + "=" + p.Value)));
            if (settings["clientAreaAnimation"] == "off") options.ReduceMotion = true;
            if (settings["enableTransparency"] == "0") options.ReduceTransparency = true;
            Stage("backdrop", () => SystemBackdrop = options.ReduceTransparency ? null : new MicaBackdrop { Kind = MicaKind.BaseAlt });
            Stage("scene", BuildScene);
            palette = options.Theme == "light" ? Light : Dark;
            Stage("theme", ApplyTheme);
            Stage("content", () => Content = root);

            CompositionTarget.Rendering += OnRendering;
            loop.RoundCompleted += r => log.Write("round", "index", r.Index, "scheduledMs", Math.Round(r.ScheduledMs, 1),
                "actualMs", Math.Round(r.ActualMs, 1), "rttMs", r.RttMs.HasValue ? (object)r.RttMs.Value : null);
            loop.RoundCompleted += r => DispatcherQueue.TryEnqueue(() => OnRound(r));
            _ = loop.RunAsync(stop.Token);
            _ = Task.Run(() =>
            {
                var values = Probes.RunAll();
                log.Write("probes", "values", string.Join(",", values.Select(p => p.Key + "=" + p.Value)));
                probes = ProbeSummary.ForDisplay(values);
                DispatcherQueue.TryEnqueue(() => probesText.Text = probes);
            });
            Closed += (s, e) =>
            {
                stop.Cancel();
                log.Write("end", "rounds", loop.Rounds, "missed", loop.Missed, "maxLateMs", Math.Round(loop.MaxLateMs, 1));
                log.Dispose();
            };
            if (options.HideTest) _ = HideTestAsync();
            if (options.RunSeconds > 0) _ = CloseAfterAsync(options.RunSeconds);
            if (options.Bench) _ = BenchAsync();
        }

        private void Stage(string name, Action action)
        {
            try { action(); log.Write("stage", "name", name, "ok", true); }
            catch (Exception ex) { log.Write("stage", "name", name, "ok", false, "error", ex.GetType().Name + ": " + ex.Message); throw; }
        }

        // ---------------- 장면 구성 ----------------
        private void BuildScene()
        {
            root.Children.Add(background);
            // Canvas에 넣으면 시작 시 XAML 내부 예외로 종료되어 Grid를 창보다 크게 잡아 잘림을 피한다.
            var orbLayer = new Grid { Margin = new Thickness(-700), IsHitTestVisible = false };
            for (int i = 0; i < 3; i++)
            {
                var orb = new Ellipse { Width = 900, Height = 900, Opacity = 0.55, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var t = new CompositeTransform { TranslateX = 700 + new[] { -250, 700, 300 }[i], TranslateY = 700 + new[] { -350, 100, 350 }[i] };
                orb.RenderTransform = t;
                orbs.Add(orb);
                orbLayer.Children.Add(orb);
                if (!options.ReduceMotion) Drift(t, i);
            }
            root.Children.Add(orbLayer);

            var layout = new Grid { Padding = new Thickness(16), ColumnSpacing = 16 };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(layout);

            var rail = new StackPanel { Spacing = 6, Padding = new Thickness(12, 16, 12, 16) };
            var brand = Ink("NetPulse", 17, true); brand.Margin = new Thickness(10, 6, 10, 16);
            rail.Children.Add(brand);
            rail.Children.Add(NavButton("측정", "nav-monitor", () => ShowPage(true)));
            rail.Children.Add(NavButton("진단", "nav-diagnose", () => ShowPage(false)));
            var railGrid = new Grid();
            railGrid.RowDefinitions.Add(new RowDefinition());
            railGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            railGrid.Children.Add(rail);
            var themeButton = NavButton("테마 전환", "theme-toggle", ToggleTheme);
            themeButton.Margin = new Thickness(12, 0, 12, 16);
            Grid.SetRow(themeButton, 1);
            railGrid.Children.Add(themeButton);
            var railBorder = Glass(glassControl, railGrid, 20);
            railBorder.Padding = new Thickness(0);
            layout.Children.Add(railBorder);

            Grid.SetColumn(pageHost, 1);
            if (!options.ReduceMotion)
                pageHost.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 14 } };
            layout.Children.Add(pageHost);
            monitorPage = BuildMonitorPage();
            diagnosePage = BuildDiagnosePage();
            pageHost.Children.Add(monitorPage);
        }

        private Grid BuildMonitorPage()
        {
            var page = new Grid { RowSpacing = 16 };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition());
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.Children.Add(Ink("실시간 측정", 30, true));

            var chartWrap = new Grid { CornerRadius = new CornerRadius(22) };
            Grid.SetRow(chartWrap, 1);
            chart.RenderTransform = chartShift;
            for (int k = 0; k < 3; k++)
            {
                lines[k] = new Polyline { StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
                chart.Children.Add(lines[k]);
            }
            chartWrap.Children.Add(chart);
            if (options.GlassTest) chartWrap.Children.Add(BuildGlassLab());

            var status = new StackPanel();
            status.Children.Add(Muted("지금 상태"));
            statusText = Ink("측정 준비 중", 22, true, "status-text");
            status.Children.Add(statusText);
            var nested = new StackPanel();
            nested.Children.Add(Muted("현재 회차"));
            roundText = Ink("—", 30, true, "round");
            nested.Children.Add(roundText);
            var nestedBorder = Glass(glassFloat, nested, 14);
            nestedBorder.Margin = new Thickness(0, 14, 0, 0);
            nestedBorder.Padding = new Thickness(14, 12, 14, 12);
            status.Children.Add(nestedBorder);
            var statusBorder = Glass(glassContent, status, 20);
            statusBorder.Width = 360; statusBorder.HorizontalAlignment = HorizontalAlignment.Left;
            statusBorder.VerticalAlignment = VerticalAlignment.Top; statusBorder.Margin = new Thickness(28);
            chartWrap.Children.Add(statusBorder);

            var metric = new StackPanel();
            metric.Children.Add(Muted("외부 지연"));
            latencyText = Ink("— ms", 30, true, "latency");
            metric.Children.Add(latencyText);
            lateText = Muted("예정 대비 —");
            metric.Children.Add(lateText);
            var metricBorder = Glass(glassContent, metric, 20);
            metricBorder.Width = 240; metricBorder.HorizontalAlignment = HorizontalAlignment.Right;
            metricBorder.VerticalAlignment = VerticalAlignment.Bottom; metricBorder.Margin = new Thickness(28);
            chartWrap.Children.Add(metricBorder);
            page.Children.Add(chartWrap);

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            var mark = new Button { Content = "증상 표시" };
            AutomationProperties.SetAutomationId(mark, "mark");
            mark.Click += (s, e) => log.Write("mark", "label", "user");
            toolbar.Children.Add(mark);
            var memo = new TextBox { PlaceholderText = "메모", Width = 180, MaxLength = 40 };
            AutomationProperties.SetAutomationId(memo, "memo");
            toolbar.Children.Add(memo);
            probesText = Muted("진단 API 확인 중");
            AutomationProperties.SetAutomationId(probesText, "probes");
            probesText.VerticalAlignment = VerticalAlignment.Center;
            toolbar.Children.Add(probesText);
            var toolbarBorder = Glass(glassControl, toolbar, 20);
            toolbarBorder.Padding = new Thickness(14, 12, 14, 12);
            Grid.SetRow(toolbarBorder, 2);
            page.Children.Add(toolbarBorder);
            return page;
        }

        private Border[] labPanels;

        // P1 확인용: 흑백 줄무늬 위에 틴트만 / Acrylic / 불투명 패널을 나란히 놓는다.
        private Grid BuildGlassLab()
        {
            var lab = new Grid { IsHitTestVisible = false };
            var stripes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 260 };
            for (int i = 0; i < 120; i++)
                stripes.Children.Add(new Rectangle { Width = 6, Fill = new SolidColorBrush(i % 2 == 0 ? Colors.White : Colors.Black) });
            lab.Children.Add(stripes);
            var row = new Grid { ColumnSpacing = 40, Padding = new Thickness(40, 0, 40, 0), VerticalAlignment = VerticalAlignment.Center, Height = 330 };
            string[] names = { "틴트만", "Acrylic", "불투명" };
            labPanels = new Border[3];
            for (int i = 0; i < 3; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                labPanels[i] = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(0, 10, 0, 0),
                    Child = new TextBlock { Text = names[i], FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = new SolidColorBrush(Colors.White) } };
                Grid.SetColumn(labPanels[i], i);
                row.Children.Add(labPanels[i]);
            }
            lab.Children.Add(row);
            return lab;
        }

        private Grid BuildDiagnosePage()
        {
            var page = new Grid { RowSpacing = 16 };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition());
            page.Children.Add(Ink("빠른 진단", 30, true));
            var cards = new Grid { RowSpacing = 16, ColumnSpacing = 16, VerticalAlignment = VerticalAlignment.Top };
            cards.ColumnDefinitions.Add(new ColumnDefinition()); cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (!options.ReduceMotion)
                cards.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition { IsStaggeringEnabled = true } };
            string[] names = { "어댑터", "게이트웨이", "DNS", "HTTPS" };
            for (int i = 0; i < 4; i++)
            {
                var card = new StackPanel();
                card.Children.Add(Muted(names[i]));
                card.Children.Add(Ink(i == 2 ? "확인" : "정상", 30, true));
                var b = Glass(glassContent, card, 20);
                Grid.SetColumn(b, i % 2); Grid.SetRow(b, i / 2);
                cards.Children.Add(b);
            }
            Grid.SetRow(cards, 1);
            page.Children.Add(cards);
            return page;
        }

        private Border Glass(List<Border> group, UIElement child, double radius)
        {
            var b = new Border { Child = child, CornerRadius = new CornerRadius(radius), Padding = new Thickness(18, 18, 20, 18),
                BorderThickness = new Thickness(1) };
            group.Add(b);
            return b;
        }

        private TextBlock Ink(string text, double size, bool strong, string id = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = strong ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal };
            if (id != null) AutomationProperties.SetAutomationId(t, id);
            inkText.Add(t);
            return t;
        }

        private TextBlock Muted(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12.5 };
            mutedText.Add(t);
            return t;
        }

        private Button NavButton(string text, string id, Action action)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(12, 10, 12, 10) };
            AutomationProperties.SetAutomationId(b, id);
            AutomationProperties.SetName(b, text);
            b.Click += (s, e) => action();
            return b;
        }

        private void Drift(CompositeTransform t, int i)
        {
            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            double[] dx = { 250, -300, 200 }, dy = { 180, 120, -250 }, secs = { 18, 22, 26 };
            foreach (var (prop, to) in new[] { ("TranslateX", t.TranslateX + dx[i]), ("TranslateY", t.TranslateY + dy[i]) })
            {
                var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromSeconds(secs[i]), EnableDependentAnimation = false,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Storyboard.SetTarget(a, t); Storyboard.SetTargetProperty(a, prop);
                sb.Children.Add(a);
            }
            sb.Begin();
        }

        // ---------------- 테마와 재질 ----------------
        private void ApplyTheme()
        {
            root.RequestedTheme = palette == Light ? ElementTheme.Light : ElementTheme.Dark;
            background.Fill = new SolidColorBrush(palette.Bg);
            var orbColors = new[] { palette.OrbA, palette.OrbB, palette.OrbC };
            for (int i = 0; i < orbs.Count; i++)
            {
                var g = new RadialGradientBrush();
                g.GradientStops.Add(new GradientStop { Color = orbColors[i], Offset = 0 });
                g.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, orbColors[i].R, orbColors[i].G, orbColors[i].B), Offset = 1 });
                orbs[i].Fill = g;
                orbs[i].Visibility = options.ReduceTransparency ? Visibility.Collapsed : Visibility.Visible;
            }
            Paint(glassContent, 0.62); Paint(glassControl, 0.42); Paint(glassFloat, 0.28);
            if (labPanels != null)
            {
                var t = palette.Tint;
                labPanels[0].Background = new SolidColorBrush(Color.FromArgb(31, t.R, t.G, t.B));
                labPanels[1].Background = new AcrylicBrush { TintColor = t, TintOpacity = 0.12, TintLuminosityOpacity = 0.12, FallbackColor = t };
                labPanels[2].Background = new SolidColorBrush(t);
                foreach (var p in labPanels) p.BorderBrush = new SolidColorBrush(palette.Edge);
            }
            foreach (var t in inkText) t.Foreground = new SolidColorBrush(palette.Ink);
            foreach (var t in mutedText) t.Foreground = new SolidColorBrush(palette.Muted);
            for (int k = 0; k < 3; k++) lines[k].Stroke = new SolidColorBrush(palette.Lines[k]);
        }

        private void Paint(List<Border> group, double tintOpacity)
        {
            foreach (var b in group)
            {
                b.BorderBrush = new SolidColorBrush(palette.Edge);
                // 창 안쪽 Acrylic: 패널 뒤의 앱 콘텐츠를 흐린다. 투명 효과를 끄면 불투명 표면.
                b.Background = options.ReduceTransparency
                    ? (Brush)new SolidColorBrush(palette.Tint)
                    : new AcrylicBrush { TintColor = palette.Tint, TintOpacity = tintOpacity, TintLuminosityOpacity = tintOpacity, FallbackColor = palette.Tint };
            }
        }

        private void ToggleTheme()
        {
            palette = palette == Dark ? Light : Dark;
            if (options.ReduceMotion) { ApplyTheme(); return; }
            // 원형 확산 대신 짧은 교차 페이드. (원형 확산은 Composition 클립 애니메이션이 필요하며 스파이크에서 구현하지 않음)
            var fade = new DoubleAnimation { From = 0.6, To = 1, Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 } };
            var sb = new Storyboard();
            Storyboard.SetTarget(fade, root); Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            ApplyTheme();
            sb.Begin();
        }

        private void ShowPage(bool monitor)
        {
            var next = monitor ? monitorPage : diagnosePage;
            if (pageHost.Children.Contains(next)) return;
            pageHost.Children.Clear();
            pageHost.Children.Add(next);
        }

        // ---------------- 실시간 그래프 ----------------
        private void OnRound(Round r)
        {
            for (int k = 0; k < 3; k++) { series[k].Add(r.Series[k]); if (series[k].Count > WindowSize) series[k].RemoveAt(0); }
            lastArrivalMs = frameClock.Elapsed.TotalMilliseconds;
            roundText.Text = r.Index.ToString(CultureInfo.InvariantCulture);
            latencyText.Text = r.Series[2].ToString("0", CultureInfo.InvariantCulture) + " ms";
            lateText.Text = "예정 대비 " + (r.ActualMs - r.ScheduledMs).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
            statusText.Text = r.Series[2] > 80 ? "지연 급증" : "안정적";
            RebuildLines();
        }

        private void RebuildLines()
        {
            // 첫 회차가 화면 배치보다 먼저 올 수 있다. 부모가 아직 없으면 다음 회차에 그린다.
            var host = chart.Parent as FrameworkElement;
            if (host == null) return;
            double w = host.ActualWidth, h = host.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double step = w / (WindowSize - 2);
            for (int k = 0; k < 3; k++)
            {
                var pts = new PointCollection();
                var s = series[k];
                for (int i = 0; i < s.Count; i++)
                    pts.Add(new Point(w - (s.Count - 1 - i) * step, h - 30 - s[i] / 110 * (h - 60)));
                lines[k].Points = pts;
            }
        }

        private void OnRendering(object sender, object e)
        {
            double now = frameClock.Elapsed.TotalMilliseconds;
            if (frameTimes != null && lastFrameMs > 0) frameTimes.Add(now - lastFrameMs);
            lastFrameMs = now;
            double w = (chart.Parent as FrameworkElement)?.ActualWidth ?? 0;
            double step = w / (WindowSize - 2);
            // 움직임 줄이기에서는 그래프를 흘리지 않고 새 값이 오면 한 번에 바꾼다.
            chartShift.X = options.ReduceMotion ? 0 : -Math.Min(1, (now - lastArrivalMs) / 1000) * step;
            if (!ready)
            {
                ready = true;
                log.Write("ready", "msSinceProcessStart", Math.Round(StartupClock.MillisecondsSinceProcessStart()));
            }
        }

        private async Task Record(string label, int ms)
        {
            frameTimes = new List<double>();
            await Task.Delay(ms);
            var t = frameTimes; frameTimes = null;
            t.Sort();
            double avg = t.Count > 0 ? 1000 / t.Average() : 0;
            double p95 = t.Count > 0 ? t[(int)(t.Count * 0.95)] : 0;
            log.Write("frames", "label", label, "avgFps", Math.Round(avg, 1), "p95Ms", Math.Round(p95, 1), "count", t.Count);
        }

        private async Task BenchAsync()
        {
            await Task.Delay(4000);
            await Record("scene", 10000);
            var themeRun = Record("theme-toggle", 6000);
            for (int i = 0; i < 6; i++) { ToggleTheme(); await Task.Delay(650); }
            await themeRun;
            var pageRun = Record("page-switch", 6000);
            for (int i = 0; i < 6; i++) { ShowPage(i % 2 == 1); await Task.Delay(800); }
            await pageRun;
            ShowPage(true);
            log.Write("mark", "label", "bench-done");
        }

        private async Task HideTestAsync()
        {
            await Task.Delay(10000);
            long before = loop.Rounds, missedBefore = loop.Missed;
            log.Write("hide", "rounds", before);
            AppWindow.Hide();
            await Task.Delay(30000);
            AppWindow.Show();
            log.Write("show", "rounds", loop.Rounds, "roundsWhileHidden", loop.Rounds - before, "missedWhileHidden", loop.Missed - missedBefore,
                "latestIndex", loop.Latest == null ? -1 : loop.Latest.Index);
        }

        private async Task CloseAfterAsync(int seconds)
        {
            await Task.Delay(seconds * 1000);
            Close();
        }
    }
}
