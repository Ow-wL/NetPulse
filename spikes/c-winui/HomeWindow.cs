// M0 확인용: 사용자가 고른 흑요석 홈 시안(spikes/design/home-mockups.html)을 WinUI 3로 재현한다.
// 목적은 D1 최종 확정 전 "C로 시안 수준의 표현이 나오는가"를 눈으로 비교하는 것이다. 제품 코드가 아니다.
// 실행: NetPulseSpikeC.exe --home [--theme light] [--aurora on] [--state problem] [--chrome 0] [--reduce-motion] [--reduce-transparency]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace NetPulseSpike
{
    public sealed class HomeWindow : Window
    {
        // ---------- 흑요석 토큰 (home-mockups.html의 값을 그대로 옮김) ----------
        private sealed class Tokens
        {
            public bool Dark;
            public Color Bg, Ink, Muted, Faint, Accent, AccentInk, Tint, Edge, Hi, Ok, Warn, Bad, HeadTop, HeadBottom;
            public Color[] Lights; public double LightOpacity;
            public double ContentAlpha, ControlAlpha, FloatAlpha;
            public Color[][] Veil; public double VeilStrength;
            public Color[] ProblemLights;
        }

        private static Color C(string hex, double alpha = 1)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb((byte)Math.Round(alpha * 255), byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber), byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber));
        }

        private static readonly Tokens DarkTokens = new Tokens
        {
            Dark = true, Bg = C("050507"), Ink = C("f4f4f5"), Muted = C("9b9ba4"), Faint = C("ffffff", .06), Accent = C("7dd3fc"), AccentInk = C("04131c"),
            Tint = C("0e0e12"), Edge = C("ffffff", .10), Hi = C("ffffff", .35), Ok = C("34d399"), Warn = C("fbbf24"), Bad = C("fb7185"),
            HeadTop = C("ffffff"), HeadBottom = C("a1a1aa"),
            Lights = new[] { C("1e293b"), C("0c4a6e"), C("18181b"), C("27272a") }, LightOpacity = .6,
            ContentAlpha = .70, ControlAlpha = .52, FloatAlpha = .36,
            Veil = new[] { new[] { C("14b8a6"), C("22d3ee") }, new[] { C("7c3aed"), C("2563eb") }, new[] { C("0ea5e9"), C("a78bfa") } }, VeilStrength = .30,
            ProblemLights = new[] { C("4c0519"), C("7c2d12"), C("18181b"), C("27272a") },
        };
        private static readonly Tokens LightTokens = new Tokens
        {
            Dark = false, Bg = C("f4f4f2"), Ink = C("111113"), Muted = C("5d5d66"), Faint = C("000000", .05), Accent = C("0369a1"), AccentInk = C("ffffff"),
            Tint = C("ffffff"), Edge = C("000000", .08), Hi = C("ffffff"), Ok = C("047857"), Warn = C("a16207"), Bad = C("be123c"),
            HeadTop = C("111113"), HeadBottom = C("52525b"),
            Lights = new[] { C("e7e5e4"), C("dbeafe"), C("fafaf9"), C("e4e4e7") }, LightOpacity = .9,
            ContentAlpha = .72, ControlAlpha = .55, FloatAlpha = .40,
            Veil = new[] { new[] { C("5eead4"), C("a5f3fc") }, new[] { C("c4b5fd"), C("93c5fd") }, new[] { C("bae6fd"), C("ddd6fe") } }, VeilStrength = .38,
            ProblemLights = new[] { C("fecdd3"), C("fed7aa"), C("fafaf9"), C("e4e4e7") },
        };

        // ---------- 상태 ----------
        private Tokens t;
        private bool aurora, problem, reduceMotion, reduceTransparency, chrome = true, firstBuild = true;
        private readonly Grid windowRoot = new Grid();
        private Grid sceneRoot;
        private readonly ResultLog log = new ResultLog("C-home");
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        // 타임라인 합성 데이터 (시안과 같은 생성기)
        private const int N = 120;
        private readonly List<double?> net = new List<double?>();
        private readonly List<double> gw = new List<double>();
        private readonly List<int> marks = new List<int> { N - 46 };
        private long seed = 11;
        private int tick;
        private int problemSince = int.MaxValue;
        private double lastArrivalMs;
        private readonly TranslateTransform plotShift = new TranslateTransform();
        private Grid plotHost;
        private Grid veilLayer;
        private readonly List<FrameworkElement> riseCards = new List<FrameworkElement>();
        private List<double> frameTimes; private double lastFrameMs;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer secondTimer, closeTimer;
        private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> countTimers = new List<Microsoft.UI.Dispatching.DispatcherQueueTimer>();

        // 장면마다 따로 있는 그래프. 전환 중에는 두 장면이 함께 살아 있으므로 둘 다 같은 데이터로 다시 그린다.
        private sealed class PlotView { public Grid Scene, Host; public Canvas Fixed, Moving; public Tokens T; }
        private readonly List<PlotView> plots = new List<PlotView>();
        private Grid building;   // 지금 만드는 장면
        // 장면마다 시작한 반복 애니메이션. 장면을 뗄 때 멈춘다 (멈추지 않으면 떼어 낸 장면이 메모리에 남는다).
        private readonly Dictionary<Grid, List<Storyboard>> sceneStoryboards = new Dictionary<Grid, List<Storyboard>>();
        private Storyboard Track(Storyboard sb)
        {
            if (building != null) { if (!sceneStoryboards.TryGetValue(building, out var list)) sceneStoryboards[building] = list = new List<Storyboard>(); list.Add(sb); }
            return sb;
        }
        private readonly Dictionary<string, Button> switchButtons = new Dictionary<string, Button>();
        private readonly MeasureLoop measure = new MeasureLoop();   // 측정 부하를 함께 돌려 화면이 측정에 주는 영향을 본다
        private readonly System.Threading.CancellationTokenSource stop = new System.Threading.CancellationTokenSource();
        private bool highContrast;

        public HomeWindow(string[] args)
        {
            var a = args.ToList();
            string Arg(string name) { int i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
            t = Arg("--theme") == "light" ? LightTokens : DarkTokens;
            aurora = Arg("--aurora") == "on";
            problem = Arg("--state") == "problem";
            chrome = Arg("--chrome") != "0";
            reduceMotion = a.Contains("--reduce-motion");
            reduceTransparency = a.Contains("--reduce-transparency");
            int runSeconds = int.TryParse(Arg("--run-seconds"), out var rs) ? rs : 0;
            // Windows 설정을 읽는다: 투명 효과 끔 -> 효과 줄이기, 애니메이션 끔 -> 움직임 줄이기, 고대비 -> 효과 줄이기
            var ws = WindowsSettings.Read();
            if (ws["enableTransparency"] == "0") reduceTransparency = true;
            if (ws["clientAreaAnimation"] == "off") reduceMotion = true;
            try { highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; } catch (Exception) { }
            if (highContrast) reduceTransparency = true;
            log.Write("windowsSettings", "values", string.Join(",", ws.Select(p => p.Key + "=" + p.Value)), "highContrast", highContrast,
                "reduceMotion", reduceMotion, "reduceTransparency", reduceTransparency);

            Title = "NetPulse · 흑요석 재현";
            AppWindow.ResizeClient(new SizeInt32(1440, 900));
            SystemBackdrop = reduceTransparency ? null : new MicaBackdrop { Kind = MicaKind.Base };
            if (problem) problemSince = N - 6;
            for (; tick < N; tick++) { var s = Sample(tick); net.Add(s.Item1); gw.Add(s.Item2); }

            Content = windowRoot;
            Rebuild();

            // 타이머는 필드로 붙잡아 둔다. 지역 변수면 가비지 수집되어 멈출 수 있다.
            secondTimer = DispatcherQueue.CreateTimer();
            secondTimer.Interval = TimeSpan.FromSeconds(1);
            secondTimer.Tick += (s, e) => OnSecond();
            secondTimer.Start();
            CompositionTarget.Rendering += OnRendering;
            log.Write("start", "theme", t.Dark ? "dark" : "light", "aurora", aurora, "problem", problem);
            log.Write("veil", "requested", aurora, "visible", veilLayer.Visibility == Visibility.Visible, "opacity", Math.Round(veilLayer.Opacity, 2),
                "lightsHidden", reduceTransparency, "acrylic", !reduceTransparency);
            if (runSeconds > 0) { closeTimer = DispatcherQueue.CreateTimer(); closeTimer.Interval = TimeSpan.FromSeconds(runSeconds); closeTimer.Tick += (s, e) => Close(); closeTimer.Start(); }
            measure.RoundCompleted += r => log.Write("round", "index", r.Index, "scheduledMs", Math.Round(r.ScheduledMs, 1), "actualMs", Math.Round(r.ActualMs, 1));
            _ = measure.RunAsync(stop.Token);
            if (a.Contains("--bench")) _ = BenchAsync();
            Closed += (s, e) =>
            {
                stop.Cancel();
                log.Write("end", "rounds", measure.Rounds, "missed", measure.Missed, "maxLateMs", Math.Round(measure.MaxLateMs, 1), "scenes", windowRoot.Children.Count, "plots", plots.Count);
                log.Dispose();
            };
        }

        // ---------- 합성 데이터 ----------
        private double Rnd() { seed = seed * 16807 % 2147483647; return seed / 2147483647.0; }
        private bool LostAt(int i) => problem && (i >= problemSince || (i % 37 > 30 && i < problemSince - 3));
        private Tuple<double?, double> Sample(int i)
        {
            double? n = LostAt(i) ? (double?)null : 16 + 6 * Math.Sin(i / 9.0) + Rnd() * 7 + (i % 53 == 0 ? 45 : 0);
            return Tuple.Create(n, 1.5 + Rnd() * 2);
        }

        private void OnSecond()
        {
            var s = Sample(tick++);
            net.Add(s.Item1); gw.Add(s.Item2); net.RemoveAt(0); gw.RemoveAt(0);
            for (int k = 0; k < marks.Count; k++) marks[k]--;
            lastArrivalMs = clock.Elapsed.TotalMilliseconds;
            DrawPlot();
        }

        private void OnRendering(object sender, object e)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (frameTimes != null && lastFrameMs > 0) frameTimes.Add(now - lastFrameMs);
            lastFrameMs = now;
            if (plotHost == null || reduceMotion) { plotShift.X = 0; return; }
            double step = plotHost.ActualWidth / (N - 2);
            plotShift.X = -Math.Min(1, (now - lastArrivalMs) / 1000) * step;
        }

        // ---------- 장면 구성 ----------
        private static FontFamily Font(bool bold) => new FontFamily(bold ? "ms-appx:///Fonts/Pretendard-Bold.otf#Pretendard" : "ms-appx:///Fonts/Pretendard-Regular.otf#Pretendard");
        private static FontFamily SemiBold() => new FontFamily("ms-appx:///Fonts/Pretendard-SemiBold.otf#Pretendard");
        private SolidColorBrush B(Color c) => new SolidColorBrush(c);

        private TextBlock Text(string s, double size, Color color, int weight = 400, string id = null)
        {
            var tb = new TextBlock { Text = s, FontSize = size, Foreground = B(color),
                FontFamily = weight >= 700 ? Font(true) : weight >= 600 ? SemiBold() : Font(false),
                FontWeight = weight >= 700 ? FontWeights.Bold : weight >= 600 ? FontWeights.SemiBold : FontWeights.Normal,
                LineHeight = Math.Round(size * 1.5), LineStackingStrategy = LineStackingStrategy.BlockLineHeight };   // 시안의 line-height: 1.5
            if (id != null) AutomationProperties.SetAutomationId(tb, id);
            return tb;
        }

        // 한국어를 단어 중간에서 줄바꿈하지 않게 한다 (CSS의 word-break: keep-all). 공백이 아닌 글자 사이에 단어 결합자(U+2060)를 넣는다.
        private static string KeepAll(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                sb.Append(s[i]);
                if (i + 1 < s.Length && !char.IsWhiteSpace(s[i]) && !char.IsWhiteSpace(s[i + 1])) sb.Append('\u2060');
            }
            return sb.ToString();
        }

        private Brush HeadlineBrush() => new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = { new GradientStop { Color = t.HeadTop, Offset = 0 }, new GradientStop { Color = t.HeadBottom, Offset = 1 } }
        };

        /// <summary>유리 3단계. 위쪽 1px 하이라이트는 위가 밝은 테두리 그라디언트로 흉내 낸다.</summary>
        private Border Glass(string level, double radius, UIElement child, Thickness padding)
        {
            double alpha = level == "content" ? t.ContentAlpha : level == "control" ? t.ControlAlpha : t.FloatAlpha;
            Brush bg = reduceTransparency ? (Brush)B(t.Tint)
                : new AcrylicBrush { TintColor = t.Tint, TintOpacity = alpha, TintLuminosityOpacity = alpha, FallbackColor = t.Tint };
            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            edge.GradientStops.Add(new GradientStop { Color = t.Hi, Offset = 0 });
            edge.GradientStops.Add(new GradientStop { Color = t.Edge, Offset = 0.06 });
            edge.GradientStops.Add(new GradientStop { Color = t.Edge, Offset = 1 });
            // 흑요석 시안은 유리 반경을 18, 떠 있는 유리를 12로 덮어쓴다. 알약 모양(999)만 그대로 둔다.
            double r = radius >= 999 ? radius : level == "float" ? 12 : 18;
            var b = new Border { Background = bg, BorderBrush = edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(r),
                Padding = padding, Child = child };
            if (level != "float" && !reduceTransparency) { b.Shadow = new ThemeShadow(); b.Translation = new Vector3(0, 0, level == "content" ? 32 : 20); }
            return b;
        }

        private Path IconPath(string data, Color stroke, double thickness = 1.7)
        {
            return new Path { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data), Stroke = B(stroke), StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        }

        private void Rebuild() => Rebuild(false);

        private void RemoveScene(Grid scene)
        {
            windowRoot.Children.Remove(scene);
            plots.RemoveAll(v => v.Scene == scene);
            if (sceneStoryboards.TryGetValue(scene, out var list)) { foreach (var sb in list) sb.Stop(); sceneStoryboards.Remove(scene); }
        }

        // 전환 중 이전 장면을 입력과 접근성 트리에서 뺀다. 화면에는 남지만 클릭·초점·화면 읽기 대상이 아니다.
        private static void Detach(UIElement root)
        {
            root.IsHitTestVisible = false;
            void Walk(DependencyObject d)
            {
                if (d is UIElement u) { AutomationProperties.SetAccessibilityView(u, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); if (u is Control c) c.IsTabStop = false; }
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
            }
            Walk(root);
        }

        /// <summary>장면을 새로 만든다. keepOld면 이전 장면을 그대로 두고 새 장면을 그 위에 올린다 (전환 애니메이션용).</summary>
        private Grid Rebuild(bool keepOld)
        {
            var previous = sceneRoot;
            if (previous != null && !keepOld) RemoveScene(previous);
            // 제목 표시줄도 테마 바탕색에 맞춘다
            var bar = AppWindow.TitleBar;
            bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = t.Bg;
            bar.ForegroundColor = bar.ButtonForegroundColor = t.Ink;
            bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = t.Muted;
            bar.ButtonHoverBackgroundColor = t.Faint;
            riseCards.Clear();
            sceneRoot = building = new Grid { Background = B(t.Bg) };
            switchButtons.Clear();
            sceneRoot.Children.Add(BuildLights());
            sceneRoot.Children.Add(veilLayer = BuildVeil());
            var app = new Grid { Padding = new Thickness(18), ColumnSpacing = 18 };
            app.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
            app.ColumnDefinitions.Add(new ColumnDefinition());
            var rail = BuildRail(); app.Children.Add(rail); riseCards.Add(rail);
            var main = BuildMain(); Grid.SetColumn(main, 1); app.Children.Add(main);
            sceneRoot.Children.Add(app);
            if (chrome) sceneRoot.Children.Add(BuildSwitch());
            windowRoot.Children.Add(sceneRoot);
            if (firstBuild && !reduceMotion) PlayRise();
            firstBuild = false;
            DrawPlot();
            return previous;
        }

        // 배경 빛: 흐린 원 대신 가장자리가 투명한 원형 그라디언트
        private UIElement BuildLights()
        {
            var layer = new Grid { Margin = new Thickness(-300), IsHitTestVisible = false };
            var cols = problem ? t.ProblemLights : t.Lights;
            double[][] geo = { new[] { 1100.0, -40, -260 }, new[] { 1100.0, 1050, -30 }, new[] { 1100.0, 380, 600 }, new[] { 560.0, 1300, 700 } };
            for (int i = 0; i < 4; i++)
            {
                var e = new Ellipse { Width = geo[i][0], Height = geo[i][0], HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Opacity = t.LightOpacity * (i == 3 ? .5 : 1), Fill = Radial(cols[i]) };
                var tr = new CompositeTransform { TranslateX = geo[i][1], TranslateY = geo[i][2] };
                e.RenderTransform = tr;
                layer.Children.Add(e);
                if (!reduceMotion) Drift(tr, new[] { 250.0, -300, 200, -280 }[i], new[] { 180.0, 120, -250, 150 }[i], new[] { 19.0, 23, 27, 31 }[i]);
            }
            if (reduceTransparency) layer.Visibility = Visibility.Collapsed;
            return layer;
        }

        private static RadialGradientBrush Radial(Color c)
        {
            var g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop { Color = c, Offset = 0 });
            g.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(c.A * .55), c.R, c.G, c.B), Offset = .45 });
            g.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, c.R, c.G, c.B), Offset = 1 });
            return g;
        }

        // 연한 오로라: 길쭉한 빛 띠 세 개. 켜고 끄면 0.6초 동안 서서히 나타나고 사라진다.
        private Grid BuildVeil()
        {
            var layer = new Grid { Margin = new Thickness(-300), IsHitTestVisible = false, Opacity = aurora ? t.VeilStrength : 0 };
            double[][] geo = { new[] { -40.0, 120, -12 }, new[] { 900.0, 420, 10 }, new[] { 500.0, 880, -4 } };
            for (int i = 0; i < 3; i++)
            {
                var g = new RadialGradientBrush();
                g.GradientStops.Add(new GradientStop { Color = t.Veil[i][0], Offset = 0 });
                g.GradientStops.Add(new GradientStop { Color = Color.FromArgb(150, t.Veil[i][1].R, t.Veil[i][1].G, t.Veil[i][1].B), Offset = .5 });
                g.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, t.Veil[i][1].R, t.Veil[i][1].G, t.Veil[i][1].B), Offset = 1 });
                var e = new Ellipse { Width = 1300, Height = 420, Fill = g, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var tr = new CompositeTransform { TranslateX = geo[i][0], TranslateY = geo[i][1], Rotation = geo[i][2], CenterX = 650, CenterY = 210 };
                e.RenderTransform = tr;
                layer.Children.Add(e);
                if (!reduceMotion) Drift(tr, new[] { 220.0, -260, 180 }[i], new[] { 100.0, -70, -120 }[i], new[] { 26.0, 32, 38 }[i]);
            }
            if (reduceTransparency) layer.Visibility = Visibility.Collapsed;
            return layer;
        }

        private void Drift(CompositeTransform tr, double dx, double dy, double seconds)
        {
            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            foreach (var (prop, to) in new[] { ("TranslateX", tr.TranslateX + dx), ("TranslateY", tr.TranslateY + dy) })
            {
                var an = new DoubleAnimation { To = to, Duration = TimeSpan.FromSeconds(seconds), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                Storyboard.SetTarget(an, tr); Storyboard.SetTargetProperty(an, prop); sb.Children.Add(an);
            }
            Track(sb).Begin();
        }

        private FrameworkElement BuildRail()
        {
            var grid = new Grid { Padding = new Thickness(14, 18, 14, 18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new StackPanel { Spacing = 4 };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(8, 4, 8, 22) };
            var beat = IconPath("M1 7h4l2-5 4 10 2-5h6", t.AccentInk, 2);
            beat.StrokeDashArray = new DoubleCollection { 20, 20 };
            if (!reduceMotion) Animate(beat, "StrokeDashOffset", 20, -20, 2.4, true, dependent: true);
            var logo = new Grid { Width = 34, Height = 34 };
            logo.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = B(t.Accent) });
            logo.Children.Add(new Viewbox { Width = 20, Height = 14, Child = new Canvas { Width = 20, Height = 14, Children = { beat } } });
            brand.Children.Add(logo);
            var name = Text("NetPulse", 18, t.Ink, 700); name.VerticalAlignment = VerticalAlignment.Center;
            brand.Children.Add(name);
            top.Children.Add(brand);
            string[] labels = { "홈", "측정", "진단", "기록", "리포트" };
            string[] icons = { "M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z", "M3 12h4l3-7 4 14 3-7h4",
                "M12 3l2.5 5.5L20 9l-4 4 1 6-5-3-5 3 1-6-4-4 5.5-.5z", "M4 5h16M4 12h16M4 19h10", "M7 3h7l5 5v13H7zM14 3v5h5M10 13h6M10 17h6" };
            for (int i = 0; i < labels.Length; i++) top.Children.Add(NavItem(labels[i], icons[i], i == 0));
            grid.Children.Add(top);
            var bottom = new StackPanel { Spacing = 10 };
            bottom.Children.Add(NavItem("설정", "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6M19 12a7 7 0 0 0-.1-1.2l2-1.6-2-3.4-2.4 1a7 7 0 0 0-2-1.2L14 3h-4l-.5 2.6a7 7 0 0 0-2 1.2l-2.4-1-2 3.4 2 1.6A7 7 0 0 0 5 12c0 .4 0 .8.1 1.2l-2 1.6 2 3.4 2.4-1a7 7 0 0 0 2 1.2L10 21h4l.5-2.6a7 7 0 0 0 2-1.2l2.4 1 2-3.4-2-1.6c.1-.4.1-.8.1-1.2z", false));
            var pill = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = B(problem ? t.Bad : t.Ok), VerticalAlignment = VerticalAlignment.Center };
            pill.Children.Add(dot);
            var pillText = new StackPanel();
            pillText.Children.Add(Text(problem ? "끊김 감지 · 알림 보냄" : "백그라운드 모니터링 켜짐", 12.5, t.Muted));
            pillText.Children.Add(Text(problem ? "방금 전" : "2시간 14분째", 12.5, t.Ink, 700));
            pill.Children.Add(pillText);
            bottom.Children.Add(Glass("float", 14, pill, new Thickness(12)));
            Grid.SetRow(bottom, 2);
            grid.Children.Add(bottom);
            return Glass("control", 22, grid, new Thickness(0));
        }

        private UIElement NavItem(string label, string icon, bool active)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var path = IconPath(icon, active ? t.Accent : t.Muted);
            row.Children.Add(new Viewbox { Width = 18, Height = 18, Child = new Canvas { Width = 24, Height = 24, Children = { path } } });
            var tb = Text(label, 14, active ? t.Ink : t.Muted, 600); tb.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(tb);
            var b = new Border { Child = row, Padding = new Thickness(12, 11, 12, 11), CornerRadius = new CornerRadius(13) };
            if (active)
            {
                b.Background = B(t.Dark ? C("ffffff", .10) : C("ffffff", .75));
                b.BorderBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops = { new GradientStop { Color = t.Hi, Offset = 0 }, new GradientStop { Color = Colors.Transparent, Offset = .08 } } };
                b.BorderThickness = new Thickness(1);
            }
            return b;
        }

        private FrameworkElement BuildMain()
        {
            var main = new Grid { RowSpacing = 18, ColumnSpacing = 18 };
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(272) });
            main.RowDefinitions.Add(new RowDefinition());

            var top = BuildTopbar(); Grid.SetColumnSpan(top, 2); main.Children.Add(top); riseCards.Add(top);
            var hero = BuildHero(); Grid.SetRow(hero, 1); main.Children.Add(hero); riseCards.Add(hero);
            var map = BuildMap(); Grid.SetRow(map, 1); Grid.SetColumn(map, 1); main.Children.Add(map); riseCards.Add(map);
            var timeline = BuildTimeline(); Grid.SetRow(timeline, 2); main.Children.Add(timeline); riseCards.Add(timeline);
            var side = new Grid { RowSpacing = 18 };
            side.RowDefinitions.Add(new RowDefinition());
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var recent = BuildRecent(); side.Children.Add(recent); riseCards.Add(recent);
            var checks = BuildChecks(); Grid.SetRow(checks, 1); side.Children.Add(checks); riseCards.Add(checks);
            Grid.SetRow(side, 2); Grid.SetColumn(side, 1); main.Children.Add(side);
            return main;
        }

        private FrameworkElement BuildTopbar()
        {
            var g = new Grid { Padding = new Thickness(6, 4, 6, 0) };
            var left = new StackPanel();
            left.Children.Add(Text("11월 13일 목요일", 13, t.Muted));
            left.Children.Add(Text("네트워크 상태", 30, t.Ink, 700));
            g.Children.Add(left);
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            chips.Children.Add(Chip("Wi‑Fi", "5 GHz", true, "M2 9a15 15 0 0 1 20 0M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M12 19.5h.01"));
            chips.Children.Add(Chip("공유기", "2 ms", false));
            chips.Children.Add(Chip("인터넷", problem ? "응답 없음" : "18 ms", false));
            g.Children.Add(chips);
            return g;
        }

        private UIElement Chip(string a, string b, bool boldFirst, string icon = null)
        {
            var tb = new TextBlock { FontSize = 12.5, FontFamily = SemiBold(), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            tb.Inlines.Add(new Run { Text = a + "  ", Foreground = B(boldFirst ? t.Ink : t.Muted) });
            tb.Inlines.Add(new Run { Text = b, Foreground = B(boldFirst ? t.Muted : t.Ink), FontFamily = Font(true), FontWeight = FontWeights.Bold });
            if (icon == null) return Glass("float", 12, tb, new Thickness(14, 8, 14, 8));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var path = IconPath(icon, t.Muted, 2);
            row.Children.Add(new Viewbox { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Child = new Canvas { Width = 24, Height = 24, Children = { path } } });
            row.Children.Add(tb);
            return Glass("float", 12, row, new Thickness(14, 8, 14, 8));
        }

        private FrameworkElement BuildHero()
        {
            var g = new Grid { ColumnSpacing = 26, VerticalAlignment = VerticalAlignment.Center };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            // 등급 링: 원 둘레를 점선 간격으로 나눠 채우는 방식. 시계 방향 위쪽에서 시작.
            int score = problem ? 41 : 96;
            var ring = new Grid { Width = 168, Height = 168 };
            ring.Children.Add(new Ellipse { Stroke = B(t.Faint), StrokeThickness = 12, Margin = new Thickness(6) });
            double circumference = Math.PI * 144 / 12;   // StrokeDashArray는 선 굵기 단위
            var value = new Ellipse { Stroke = B(t.Accent), StrokeThickness = 12, Margin = new Thickness(6), StrokeDashCap = PenLineCap.Round,
                StrokeDashArray = new DoubleCollection { circumference, circumference }, StrokeDashOffset = circumference,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform { Angle = -90 } };
            ring.Children.Add(value);
            double target = circumference * (1 - score / 100.0);
            // 처음 한 번만 채운다. 테마·상태를 바꿀 때는 현재 값을 그대로 보인다.
            if (reduceMotion || !firstBuild) value.StrokeDashOffset = target; else Animate(value, "StrokeDashOffset", circumference, target, 1.4, false, dependent: true);
            var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var grade = Text(problem ? "D" : "A", 52, t.Ink, 700); grade.Foreground = HeadlineBrush(); grade.HorizontalAlignment = HorizontalAlignment.Center;
            label.Children.Add(grade);
            var scoreText = Text("연결 품질 0점", 12, t.Muted, 400, "score"); scoreText.HorizontalAlignment = HorizontalAlignment.Center;
            label.Children.Add(scoreText);
            ring.Children.Add(label);
            CountUp(scoreText, score, "연결 품질 ", "점");
            g.Children.Add(ring);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var verdict = Text(KeepAll(problem ? "외부 인터넷 연결이 끊긴 것으로 보입니다" : "지금 연결은 안정적입니다"), 27, t.Ink, 700, "verdict");
            verdict.LineHeight = Math.Round(27 * 1.25);   // 시안의 .verdict line-height: 1.25
            verdict.Foreground = HeadlineBrush(); verdict.TextWrapping = TextWrapping.Wrap;   // 한국어 단어 중간 줄바꿈 방지
            right.Children.Add(verdict);
            var sub = new TextBlock { FontSize = 14.5, Foreground = B(t.Muted), FontFamily = Font(false), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 20),
                LineHeight = Math.Round(14.5 * 1.5), LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            if (problem)
            {
                sub.Inlines.Add(new Run { Text = KeepAll("공유기는 응답하지만 서로 다른 외부 대상 ") });
                sub.Inlines.Add(Strong("3곳")); sub.Inlines.Add(new Run { Text = KeepAll("이 함께 응답하지 않습니다 · ") }); sub.Inlines.Add(Strong("지금 끊김이 이어지는 중"));
            }
            else
            {
                sub.Inlines.Add(new Run { Text = KeepAll("최근 2시간 동안 끊김 ") }); sub.Inlines.Add(Strong("0회"));
                sub.Inlines.Add(new Run { Text = KeepAll(" · 평균 지연 ") }); sub.Inlines.Add(Strong("18 ms"));
                sub.Inlines.Add(new Run { Text = KeepAll(" · 응답 성공 ") }); sub.Inlines.Add(Strong("99.9%"));
            }
            right.Children.Add(sub);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            // 측정 시작: 강조색 면 + 얇은 강조색 테두리 빛(흑요석의 glow)
            var ctaGlow = new Grid();
            ctaGlow.Children.Add(new Border { CornerRadius = new CornerRadius(20), Margin = new Thickness(-4), Background = Radial(Color.FromArgb(70, t.Accent.R, t.Accent.G, t.Accent.B)) });
            var cta = new Border { CornerRadius = new CornerRadius(16), Background = B(t.Accent), Padding = new Thickness(26, 14, 26, 14),
                BorderBrush = B(Color.FromArgb(128, t.Accent.R, t.Accent.G, t.Accent.B)), BorderThickness = new Thickness(1), Child = Text("측정 시작", 15, t.AccentInk, 700) };
            ctaGlow.Children.Add(cta);
            buttons.Children.Add(ctaGlow);
            buttons.Children.Add(Glass("float", 16, Text("빠른 진단", 15, t.Ink, 600), new Thickness(20, 13, 20, 13)));
            right.Children.Add(buttons);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            return Glass("content", 28, g, new Thickness(28, 26, 28, 26));
        }

        private Run Strong(string s) => new Run { Text = KeepAll(s), Foreground = B(t.Ink), FontFamily = SemiBold(), FontWeight = FontWeights.SemiBold };

        private FrameworkElement BuildMap()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.Children.Add(CardHead("연결 경로", "개념도 · 실제 경로와 다를 수 있음"));
            var c = new Canvas { Width = 360, Height = 170 };
            void Seg(double x1, double x2, bool bad)
            {
                var wire = new Line { X1 = x1, Y1 = 92, X2 = x2, Y2 = 92, Stroke = B(bad ? Color.FromArgb(90, t.Bad.R, t.Bad.G, t.Bad.B) : t.Faint), StrokeThickness = 3 };
                c.Children.Add(wire);
                if (bad && !reduceMotion) Animate(wire, "Opacity", 1, .35, .55, true, autoReverse: true);
                for (int k = 0; k < 2; k++)
                {
                    var flow = new Line { X1 = x1, Y1 = 92, X2 = x2, Y2 = 92, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                        StrokeDashCap = PenLineCap.Round, Stroke = B(bad ? t.Bad : t.Accent),
                        StrokeDashArray = bad ? new DoubleCollection { 2, 6 } : new DoubleCollection { 6, 40 } };
                    double cycle = bad ? 8 : 46;
                    flow.StrokeDashOffset = k * cycle / 2;
                    c.Children.Add(flow);
                    if (!reduceMotion) Animate(flow, "StrokeDashOffset", k * cycle / 2, k * cycle / 2 - cycle, bad ? .9 : 1.8, true, dependent: true);
                }
            }
            Seg(60, 180, false);
            Seg(180, 300, problem);
            Node(c, 60, "이 PC", "Wi‑Fi", "M-10 -6h20v12h-20zM-5 10h10", false);
            Node(c, 180, "공유기", "2 ms", "M-11 2h22v7h-22zM-6 2v-6M6 2v-6M-8 -9a12 12 0 0 1 16 0", false);
            Node(c, 300, "인터넷", problem ? "응답 없음" : "18 ms", "M-11 0a11 11 0 1 0 22 0a11 11 0 1 0 -22 0M-11 0h22M0 -11c5 6 5 16 0 22M0 -11c-5 6 -5 16 0 22", problem);
            var vb = new Viewbox { Child = c, Stretch = Stretch.Uniform };
            Grid.SetRow(vb, 1);
            g.Children.Add(vb);
            return Glass("content", 28, g, new Thickness(24, 22, 24, 18));
        }

        private void Node(Canvas c, double x, string name, string ms, string icon, bool bad)
        {
            var accent = bad ? t.Bad : t.Accent;
            var halo = new Ellipse { Width = 60, Height = 60, Fill = B(Color.FromArgb(bad ? (byte)56 : (byte)41, accent.R, accent.G, accent.B)),
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform() };
            Canvas.SetLeft(halo, x - 30); Canvas.SetTop(halo, 62); c.Children.Add(halo);
            if (!reduceMotion)
            {
                var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
                foreach (var p in new[] { "ScaleX", "ScaleY" })
                {
                    var an = new DoubleAnimation { From = .7, To = 1.6, Duration = TimeSpan.FromSeconds(2.6), EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 } };
                    Storyboard.SetTarget(an, halo.RenderTransform); Storyboard.SetTargetProperty(an, p); sb.Children.Add(an);
                }
                var op = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromSeconds(2.6), EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 } };
                Storyboard.SetTarget(op, halo); Storyboard.SetTargetProperty(op, "Opacity"); sb.Children.Add(op);
                Track(sb).Begin();
            }
            var core = new Ellipse { Width = 52, Height = 52, Fill = B(Color.FromArgb(230, t.Tint.R, t.Tint.G, t.Tint.B)), Stroke = B(t.Edge), StrokeThickness = 1.5 };
            Canvas.SetLeft(core, x - 26); Canvas.SetTop(core, 66); c.Children.Add(core);
            var path = IconPath(icon, accent, 1.6);
            Canvas.SetLeft(path, x); Canvas.SetTop(path, 92); c.Children.Add(path);
            var label = new StackPanel { Width = 110 };
            var n = Text(name, 12.5, t.Ink, 600); n.HorizontalAlignment = HorizontalAlignment.Center; label.Children.Add(n);
            var m = Text(ms, 11.5, t.Muted); m.HorizontalAlignment = HorizontalAlignment.Center; label.Children.Add(m);
            Canvas.SetLeft(label, x - 55); Canvas.SetTop(label, 128); c.Children.Add(label);
        }

        private UIElement CardHead(string title, string note)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.Children.Add(Text(title, 15.5, t.Ink, 700));
            var n = Text(note, 12, t.Muted); n.HorizontalAlignment = HorizontalAlignment.Right; n.VerticalAlignment = VerticalAlignment.Bottom;
            g.Children.Add(n);
            return g;
        }

        private FrameworkElement BuildTimeline()
        {
            var g = new Grid { RowSpacing = 8 };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.Children.Add(CardHead("최근 2분", "실시간 · 1초 간격"));
            var view = new PlotView { Scene = building, T = t, Host = new Grid(), Fixed = new Canvas(), Moving = new Canvas { RenderTransform = plotShift } };
            view.Host.Children.Add(view.Fixed);
            view.Host.Children.Add(view.Moving);
            view.Host.SizeChanged += (s, e) => { view.Host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) }; DrawPlot(view); };
            plots.Add(view);
            plotHost = view.Host;
            Grid.SetRow(view.Host, 1);
            g.Children.Add(view.Host);
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            foreach (var (label, color) in new[] { ("인터넷 지연", t.Accent), ("공유기 지연", t.Accent), ("응답 없음", t.Bad), ("증상 표시", t.Warn) })
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                item.Children.Add(new Border { Width = 10, Height = 3, CornerRadius = new CornerRadius(2), Background = B(color), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(Text(label, 12, t.Muted));
                legend.Children.Add(item);
            }
            Grid.SetRow(legend, 2);
            g.Children.Add(legend);
            return Glass("content", 28, g, new Thickness(22, 20, 22, 16));
        }

        private void DrawPlot() { foreach (var v in plots.ToList()) DrawPlot(v); }

        private void DrawPlot(PlotView view)
        {
            var plotFixed = view.Fixed; var plotMoving = view.Moving;
            var t = view.T;   // 이 그래프가 속한 장면의 테마 (전역 테마가 이미 바뀌었어도)
            double w = view.Host.ActualWidth, h = view.Host.ActualHeight;
            if (w <= 0 || h <= 0) return;
            plotFixed.Children.Clear(); plotMoving.Children.Clear();
            double step = w / (N - 2);
            double Y(double v) => h - 26 - v / 80 * (h - 42);
            double X(int i) => w - (N - 1 - i) * step;
            foreach (var v in new[] { 0, 40, 80 })
            {
                plotFixed.Children.Add(new Line { X1 = 34, X2 = w, Y1 = Y(v), Y2 = Y(v), Stroke = B(t.Faint), StrokeThickness = 1 });
                var l = Text(v + " ms", 11, t.Muted); Canvas.SetLeft(l, 0); Canvas.SetTop(l, Y(v) - 8); plotFixed.Children.Add(l);
            }
            var t1 = Text("2분 전", 11, t.Muted); Canvas.SetLeft(t1, w * .06); Canvas.SetTop(t1, h - 18); plotFixed.Children.Add(t1);
            var t2 = Text("1분 전", 11, t.Muted); Canvas.SetLeft(t2, w * .5 - 14); Canvas.SetTop(t2, h - 18); plotFixed.Children.Add(t2);
            var t3 = Text("지금", 11, t.Muted); t3.Width = 40; t3.TextAlignment = TextAlignment.Right; Canvas.SetLeft(t3, w - 40); Canvas.SetTop(t3, h - 18); plotFixed.Children.Add(t3);
            for (int i = 0; i < N; i++)
                if (net[i] == null)
                {
                    var band = new Rectangle { Width = step + .5, Height = h, Fill = B(Color.FromArgb(51, t.Bad.R, t.Bad.G, t.Bad.B)) };
                    Canvas.SetLeft(band, X(i) - step / 2); plotMoving.Children.Add(band);
                }
            foreach (var m in marks.Where(m => m >= 0))
            {
                plotMoving.Children.Add(new Line { X1 = X(m), X2 = X(m), Y1 = 0, Y2 = h, Stroke = B(t.Warn), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 2.6 } });
                var dot = new Ellipse { Width = 8, Height = 8, Fill = B(t.Warn) }; Canvas.SetLeft(dot, X(m) - 4); Canvas.SetTop(dot, 2); plotMoving.Children.Add(dot);
            }
            plotMoving.Children.Add(Series(gw.Select((v, i) => (double?)v).ToList(), t.Accent, false, X, Y, h));
            plotMoving.Children.Add(Series(net, t.Accent, true, X, Y, h));
            if (net[N - 1] != null)
            {
                var last = new Ellipse { Width = 8, Height = 8, Fill = B(t.Accent) }; Canvas.SetLeft(last, X(N - 1) - 4); Canvas.SetTop(last, Y(net[N - 1].Value) - 4);
                plotMoving.Children.Add(last);
            }
        }

        // 끊긴 구간마다 곡선(베지어)과 아래 채움을 따로 그린다.
        private UIElement Series(List<double?> values, Color color, bool fill, Func<int, double> X, Func<double, double> Y, double h)
        {
            var layer = new Canvas();
            var runs = new List<List<Point>>(); var cur = new List<Point>();
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == null) { if (cur.Count > 0) runs.Add(cur); cur = new List<Point>(); }
                else cur.Add(new Point(X(i), Y(values[i].Value)));
            }
            if (cur.Count > 0) runs.Add(cur);
            foreach (var r in runs.Where(r => r.Count > 1))
            {
                PathFigure Curve(bool closed)
                {
                    var f = new PathFigure { StartPoint = closed ? new Point(r[0].X, h) : r[0], IsClosed = closed };
                    if (closed) f.Segments.Add(new LineSegment { Point = r[0] });
                    for (int i = 1; i < r.Count; i++)
                    {
                        double mx = (r[i - 1].X + r[i].X) / 2;
                        f.Segments.Add(new BezierSegment { Point1 = new Point(mx, r[i - 1].Y), Point2 = new Point(mx, r[i].Y), Point3 = r[i] });
                    }
                    if (closed) f.Segments.Add(new LineSegment { Point = new Point(r[r.Count - 1].X, h) });
                    return f;
                }
                if (fill)
                {
                    var area = new Path { Data = new PathGeometry { Figures = { Curve(true) } },
                        Fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                            GradientStops = { new GradientStop { Color = Color.FromArgb(85, color.R, color.G, color.B), Offset = 0 }, new GradientStop { Color = Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 } } } };
                    layer.Children.Add(area);
                }
                layer.Children.Add(new Path { Data = new PathGeometry { Figures = { Curve(false) } }, Stroke = B(color), StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round });
            }
            return layer;
        }

        private FrameworkElement BuildRecent()
        {
            var sp = new StackPanel();
            var head = CardHead("최근 측정", "전체 보기"); ((FrameworkElement)head).Margin = new Thickness(0, 0, 0, 8); sp.Children.Add(head);
            sp.Children.Add(Row("A", t.Ok, "오늘 오후 1:20", "1시간 · 유선", "0회", "끊김"));
            sp.Children.Add(Row("C", t.Warn, "어제 오후 9:02", "2시간 · Wi‑Fi · 화상회의 중", "4회", "최장 12초"));
            sp.Children.Add(Row("D", t.Bad, "11월 10일 오후 8:45", "3시간 · Wi‑Fi", "11회", "최장 41초"));
            return Glass("content", 28, sp, new Thickness(22, 20, 22, 20));
        }

        private UIElement Row(string grade, Color color, string when, string what, string count, string unit)
        {
            var g = new Grid { ColumnSpacing = 14, Padding = new Thickness(10, 11, 10, 11) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var badge = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(13), Background = B(Color.FromArgb(36, color.R, color.G, color.B)),
                BorderBrush = B(Color.FromArgb(77, color.R, color.G, color.B)), BorderThickness = new Thickness(1) };
            var gt = Text(grade, 17, color, 700); gt.HorizontalAlignment = HorizontalAlignment.Center; gt.VerticalAlignment = VerticalAlignment.Center; badge.Child = gt;
            g.Children.Add(badge);
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(Text(when, 14, t.Ink, 600)); mid.Children.Add(Text(what, 12.5, t.Muted));
            Grid.SetColumn(mid, 1); g.Children.Add(mid);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var c1 = Text(count, 15, t.Ink, 700); c1.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(c1);
            var c2 = Text(unit, 12.5, t.Muted); c2.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(c2);
            Grid.SetColumn(right, 2); g.Children.Add(right);
            return g;
        }

        private FrameworkElement BuildChecks()
        {
            var g = new Grid { RowSpacing = 10, ColumnSpacing = 10 };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = CardHead("빠른 진단", problem ? "끊김 감지 후 자동 재확인 · 방금 전" : "3분 전 확인"); Grid.SetColumnSpan((FrameworkElement)head, 3); g.Children.Add(head);
            string[] names = { "어댑터", "공유기", "인터넷", "DNS", "HTTPS", "링크 기록" };
            for (int i = 0; i < 6; i++)
            {
                string state = i == 5 ? "warn" : problem && (i == 2 || i == 4) ? "bad" : "ok";
                var color = state == "ok" ? t.Ok : state == "warn" ? t.Warn : t.Bad;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var s = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(6), Background = B(Color.FromArgb(51, color.R, color.G, color.B)),
                    Child = new TextBlock { Text = state == "ok" ? "✓" : state == "warn" ? "!" : "✕", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B(color),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                row.Children.Add(s);
                var n = Text(names[i], 12.5, t.Ink); n.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(n);
                var chip = Glass("float", 16, row, new Thickness(12, 10, 12, 10));
                Grid.SetRow(chip, 1 + i / 3); Grid.SetColumn(chip, i % 3); g.Children.Add(chip);
            }
            return Glass("control", 22, g, new Thickness(18, 14, 18, 16));
        }

        // 시안 비교용 조작 막대 (디자인의 일부가 아님)
        private UIElement BuildSwitch()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            UIElement Btn(string label, string id, bool pressed, Action<Point> act)
            {
                var b = new Button { Content = label, CornerRadius = new CornerRadius(999), Padding = new Thickness(14, 8, 14, 8), FontSize = 12.5,
                    Background = B(pressed ? t.Ink : Colors.Transparent), Foreground = B(pressed ? t.Bg : t.Muted), BorderThickness = new Thickness(0) };
                AutomationProperties.SetAutomationId(b, id); AutomationProperties.SetName(b, label);
                switchButtons[id] = b;
                b.Click += (s, e) => { lastSwitchId = id; act(b.TransformToVisual(windowRoot).TransformPoint(new Point(b.ActualWidth / 2, b.ActualHeight / 2))); };
                return b;
            }
            sp.Children.Add(Btn("다크", "theme-dark", t.Dark, p => { if (!t.Dark) _ = SwitchAsync(p, () => t = DarkTokens); }));
            sp.Children.Add(Btn("라이트", "theme-light", !t.Dark, p => { if (t.Dark) _ = SwitchAsync(p, () => t = LightTokens); }));
            sp.Children.Add(Btn("연한 오로라", "aurora-toggle", aurora, p => ToggleAurora()));
            sp.Children.Add(Btn("끊김 연출", "problem-toggle", problem, p => _ = SwitchAsync(p, () => { problem = !problem; problemSince = problem ? N - 6 : int.MaxValue; RefillNet(); })));
            var bar = Glass("control", 999, sp, new Thickness(6));
            bar.HorizontalAlignment = HorizontalAlignment.Center; bar.VerticalAlignment = VerticalAlignment.Bottom; bar.Margin = new Thickness(0, 0, 0, 16);
            return bar;
        }

        private void UpdateSwitchStates()
        {
            foreach (var (id, on) in new[] { ("theme-dark", t.Dark), ("theme-light", !t.Dark), ("aurora-toggle", aurora), ("problem-toggle", problem) })
                if (switchButtons.TryGetValue(id, out var b)) { b.Background = B(on ? t.Ink : Colors.Transparent); b.Foreground = B(on ? t.Bg : t.Muted); }
        }

        private void RefillNet()
        {
            for (int k = 0; k < N; k++) { int i = tick - N + k; if (LostAt(i)) net[k] = null; else if (net[k] == null) net[k] = 16 + 6 * Math.Sin(i / 9.0) + Rnd() * 7; }
        }

        // 연한 오로라 켜고 끄기: 0.6초 서서히. 장면을 다시 만들지 않는다.
        private void ToggleAurora()
        {
            aurora = !aurora;
            double to = aurora ? t.VeilStrength : 0;
            UpdateSwitchStates();
            log.Write("aurora", "on", aurora);
            if (reduceMotion) { veilLayer.Opacity = to; return; }
            var sb = new Storyboard();
            var an = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(600), EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 } };
            Storyboard.SetTarget(an, veilLayer); Storyboard.SetTargetProperty(an, "Opacity"); sb.Children.Add(an);
            sb.Begin();
        }

        // ---------- 테마·상태 전환: 누른 지점에서 새 화면이 원형으로 퍼진다 ----------
        // 이전 장면을 그대로 둔 채 새 장면을 위에 올리고, 새 장면의 Composition 시각 요소에 원형 클립을 키운다.
        // 두 장면 모두 살아 있으므로 전환 중에도 그래프와 빛이 계속 움직인다. 끝나면 이전 장면을 뗀다.
        private bool switching;
        private string lastSwitchId;
        private async Task SwitchAsync(Point origin, Action change)
        {
            if (switching) { log.Write("switchIgnored", "reason", "in-progress"); return; }
            switching = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                change();
                if (reduceMotion) { Rebuild(); return; }
                bool hadFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(windowRoot.XamlRoot) is Button;
                var previous = Rebuild(true);
                if (previous != null) Detach(previous);
                if (hadFocus && lastSwitchId != null && switchButtons.TryGetValue(lastSwitchId, out var focusTarget)) focusTarget.Focus(FocusState.Programmatic);
                var visual = ElementCompositionPreview.GetElementVisual(sceneRoot);
                var comp = visual.Compositor;
                var ellipse = comp.CreateEllipseGeometry();
                ellipse.Center = new Vector2((float)origin.X, (float)origin.Y);
                ellipse.Radius = Vector2.Zero;
                visual.Clip = comp.CreateGeometricClip(ellipse);
                double w = windowRoot.ActualWidth, h = windowRoot.ActualHeight;
                float max = (float)Math.Sqrt(Math.Pow(Math.Max(origin.X, w - origin.X), 2) + Math.Pow(Math.Max(origin.Y, h - origin.Y), 2));
                var an = comp.CreateVector2KeyFrameAnimation();
                an.InsertKeyFrame(1f, new Vector2(max, max), comp.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1)));
                an.Duration = TimeSpan.FromMilliseconds(550);
                var batch = comp.CreateScopedBatch(CompositionBatchTypes.Animation);
                ellipse.StartAnimation("Radius", an);
                batch.End();
                var done = new TaskCompletionSource<bool>();
                batch.Completed += (s, e) => done.TrySetResult(true);
                log.Write("switchStart", "msToAnimation", Math.Round(sw.Elapsed.TotalMilliseconds));
                await done.Task;
                visual.Clip = null;
                if (previous != null) RemoveScene(previous);
            }
            finally
            {
                switching = false;
                log.Write("switch", "ms", Math.Round(sw.Elapsed.TotalMilliseconds), "theme", t.Dark ? "dark" : "light", "problem", problem);
            }
        }

        // ---------- 벤치: 정지 장면, 오로라 켜고 끄기, 반복 원형 전환의 프레임 ----------
        private async Task Record(string label, Func<Task> during)
        {
            frameTimes = new List<double>();
            await during();
            var ft = frameTimes; frameTimes = null;
            ft.Sort();
            double avg = ft.Count > 0 ? 1000 / ft.Average() : 0, p95 = ft.Count > 0 ? ft[(int)(ft.Count * .95)] : 0, max = ft.Count > 0 ? ft[ft.Count - 1] : 0;
            log.Write("frames", "label", label, "avgFps", Math.Round(avg, 1), "p95Ms", Math.Round(p95, 1), "maxMs", Math.Round(max, 1), "count", ft.Count);
        }

        private async Task BenchAsync()
        {
            await Task.Delay(4000);
            await Record("steady", () => Task.Delay(10000));
            await Record("aurora-toggle", async () => { for (int i = 0; i < 6; i++) { ToggleAurora(); await Task.Delay(1100); } });
            log.Write("mark", "label", "before-switches", "scenes", windowRoot.Children.Count, "plots", plots.Count, "storyboardScenes", sceneStoryboards.Count, "managedMB", Math.Round(GC.GetTotalMemory(true) / 1048576.0, 1));
            await Record("theme-switch", async () =>
            {
                var origin = new Point(windowRoot.ActualWidth / 2, windowRoot.ActualHeight - 40);
                for (int i = 0; i < 20; i++) { await SwitchAsync(origin, () => t = t.Dark ? LightTokens : DarkTokens); await Task.Delay(400); }
            });
            log.Write("mark", "label", "after-switches", "scenes", windowRoot.Children.Count, "plots", plots.Count, "storyboardScenes", sceneStoryboards.Count, "managedMB", Math.Round(GC.GetTotalMemory(true) / 1048576.0, 1));
            await Record("steady-after", () => Task.Delay(10000));
            log.Write("mark", "label", "bench-done");
        }

        // ---------- 등장, 숫자, 공용 애니메이션 ----------
        private void PlayRise()
        {
            for (int i = 0; i < riseCards.Count; i++)
            {
                var el = riseCards[i];
                var tr = new TranslateTransform { Y = 16 };
                el.RenderTransform = tr; el.Opacity = 0;
                var sb = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(80 + i * 60) };
                var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 };
                var a1 = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(333), EasingFunction = ease };
                Storyboard.SetTarget(a1, el); Storyboard.SetTargetProperty(a1, "Opacity"); sb.Children.Add(a1);
                var a2 = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(333), EasingFunction = ease };
                Storyboard.SetTarget(a2, tr); Storyboard.SetTargetProperty(a2, "Y"); sb.Children.Add(a2);
                sb.Begin();
            }
        }

        private void CountUp(TextBlock tb, int to, string prefix, string suffix)
        {
            if (reduceMotion || !firstBuild) { tb.Text = prefix + to + suffix; return; }
            var start = clock.Elapsed.TotalMilliseconds;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            countTimers.Add(timer);
            timer.Tick += (s, e) =>
            {
                double p = Math.Min(1, (clock.Elapsed.TotalMilliseconds - start) / 1100), eased = 1 - Math.Pow(1 - p, 4);
                tb.Text = prefix + Math.Round(to * eased) + suffix;
                if (p >= 1) { timer.Stop(); countTimers.Remove(timer); }
            };
            timer.Start();
        }

        private void Animate(DependencyObject target, string property, double from, double to, double seconds, bool forever, bool dependent = false, bool autoReverse = false)
        {
            var an = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromSeconds(seconds), EnableDependentAnimation = dependent };
            if (!forever) an.EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
            var sb = new Storyboard();
            if (forever) { sb.RepeatBehavior = RepeatBehavior.Forever; sb.AutoReverse = autoReverse; }
            Storyboard.SetTarget(an, target); Storyboard.SetTargetProperty(an, property); sb.Children.Add(an);
            Track(sb).Begin();
        }
    }
}
