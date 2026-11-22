using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.App.Design.Controls;
using NetPulse.Design.Effects;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;
using Windows.Graphics;

namespace NetPulse.App.Design.Showcase;

/// <summary>
/// 부품 전시 화면 (M2 설계 5.7). 모든 토큰·재질·부품·상태를 한 창에서 보여 주고, 캡처 매트릭스와 UI 테스트의 대상이 된다.
/// 배치: 왼쪽 탐색(보통 유리), 위쪽 도구 막대(보통 유리), 가운데 스크롤 영역(두꺼운 유리 카드). 스크롤 영역은 보통 유리 밑으로 지나가지 않는다.
/// 진단 값은 자동화 요소 "diag.state"의 이름(JSON)으로 내보낸다.
/// </summary>
public sealed partial class ShowcaseWindow : Window
{
    private readonly Ui ui;
    private readonly StartupOptions options;
    private readonly AppLog log;
    private readonly Func<Task> beforeClose;
    private readonly GroupPanel root;
    private readonly Grid appGrid = new() { ColumnSpacing = Space.S5 };
    private readonly ScrollViewer scroll;
    private readonly StackPanel sectionHost = new() { Spacing = Space.S5 };
    private readonly NavRail rail;
    private readonly TextBlock sectionTitle;
    private readonly SegmentedChoice themeChoice, stateChoice;
    private readonly ToastHost toasts;
    private readonly DialogHost dialog;
    private readonly TextBlock diag;
    private readonly DispatcherQueueTimer selfTestTimer;
    private readonly Dictionary<string, (string Label, string Icon, Func<IEnumerable<UIElement>> Build)> sections = [];
    private bool diagQueued, closing, closeAllowed;

    public ShowcaseWindow(StartupOptions options, AppLog log, SettingsStore store, Func<Task> beforeClose)
    {
        this.options = options;
        this.log = log;
        this.beforeClose = beforeClose;
        Title = "NetPulse 부품 전시";
        AppWindow.ResizeClient(new SizeInt32(Size.WindowWidth, Size.WindowHeight));
        ui = new Ui(this, options, log, store.Current.Look, App.PersistLook(store, log));

        root = new GroupPanel("showcase.root", "NetPulse 부품 전시") { Background = ui.Brushes.Bg };
        rail = new NavRail(ui, "showcase.nav", "NetPulse");
        sectionTitle = Fonts.Text("", Typography.Title, ui.Brushes.Ink);
        themeChoice = new SegmentedChoice(ui, "toolbar.theme", "테마", [("dark", "다크"), ("light", "라이트"), ("system", "시스템")], ThemeValue(store.Current.Look.Theme));
        stateChoice = new SegmentedChoice(ui, "toolbar.state", "상태", [("normal", "정상"), ("problem", "끊김")], options.Problem ? "problem" : "normal");
        scroll = new ScrollViewer { Content = sectionHost, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        AutomationProperties.SetAutomationId(scroll, "showcase.scroll");
        toasts = new ToastHost(ui);
        dialog = new DialogHost(ui, appGrid);
        diag = new TextBlock { Opacity = 0, IsHitTestVisible = false, Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(diag, "diag.state");

        BuildLayout();
        RegisterSections();
        var scaled = ScaleHost();
        Content = ui.Theme.Host(scaled);
        liveContent = scaled;
        ui.Root = root;
        ui.Theme.Apply(ui.Effects.Mode);
        ui.Lights.SetState(options.Problem, animate: false);
        log.Write("showcase", new { section = options.Section ?? "overview", uiScale = options.UiScale, mode = ui.Effects.Describe(ui.Effects.Mode), frozen = options.FreezeMotion });

        rail.Selected += ShowSection;
        rail.Select(options.Section is { } s && sections.ContainsKey(s) ? s : "overview");
        themeChoice.Changed += v => { SetRevealOrigin(themeChoice.Item(v)); ui.Effects.UpdateSettings(ui.Effects.Settings with { Theme = v switch { "light" => ThemePreference.Light, "system" => ThemePreference.System, _ => ThemePreference.Dark } }, persist: true); };
        stateChoice.Changed += v => { ui.Lights.SetState(v == "problem"); QueueDiag(); };
        ui.Effects.Changed += (_, _) => { QueueDiag(); if (rail.Current == "contrast") ShowSection("contrast"); };
        ui.Motion.ActiveChanged += QueueDiag;
        ui.Theme.TransitionChanged += QueueDiag;
        dialog.Closed += _ => QueueDiag();
        root.GotFocus += (_, _) => QueueDiag();

        AppWindow.Closing += (_, e) => { if (closeAllowed) return; e.Cancel = true; BeginClose(); };
        selfTestTimer = DispatcherQueue.CreateTimer();
        selfTestTimer.Interval = MotionTokens.Slow * 3;
        selfTestTimer.IsRepeating = false;
        selfTestTimer.Tick += (_, _) => RunSelfTest();
        root.Loaded += (_, _) =>
        {
            log.Write("loaded", null);
            QueueDiag();
            if (options.SelfTest is not null) selfTestTimer.Start();
            if (options.Bench) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RunBench);
        };
        if (options.RunSeconds > 0)
        {
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromSeconds(options.RunSeconds);
            t.Tick += (_, _) => BeginClose();
            runTimer = t;
            t.Start();
        }
    }

    private readonly DispatcherQueueTimer? runTimer;
    private UIElement liveContent = null!;

    public Ui Ui => ui;

    private static string ThemeValue(ThemePreference p) => p switch { ThemePreference.Light => "light", ThemePreference.System => "system", _ => "dark" };

    // ---------- 배치 ----------

    private void BuildLayout()
    {
        appGrid.Padding = new Thickness(Space.S5);
        appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        appGrid.Children.Add(rail);

        var main = new Grid { RowSpacing = Space.S5 };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new Grid { ColumnSpacing = Space.S4 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sectionTitle.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(sectionTitle, "toolbar.title");
        bar.Children.Add(sectionTitle);
        Grid.SetColumn(themeChoice, 1);
        Grid.SetColumn(stateChoice, 2);
        themeChoice.VerticalAlignment = stateChoice.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(themeChoice);
        bar.Children.Add(stateChoice);
        var toolbar = new GroupPanel("showcase.toolbar", "도구 막대");
        toolbar.Children.Add(ui.Glass.Surface(GlassLevel.Control, bar, Radius.Outer, new Thickness(Space.S6, Space.S3, Space.S3, Space.S3)));
        main.Children.Add(toolbar);
        Grid.SetRow(scroll, 1);
        main.Children.Add(scroll);
        Grid.SetColumn(main, 1);
        appGrid.Children.Add(main);

        root.Children.Add(ui.Lights.Root);
        root.Children.Add(appGrid);
        root.Children.Add(toasts);
        root.Children.Add(dialog);
        root.Children.Add(diag);
    }

    /// <summary>150% 상당 배치: 내용을 창 ÷ 배율의 논리 크기로 배치하고 배율만큼 키워 그린다 (설계 5.8). 오버레이도 같은 안에 있다.</summary>
    private UIElement ScaleHost()
    {
        if (options.UiScale <= 1) return root;
        var box = new Viewbox { Stretch = Stretch.Fill, Child = root };
        SizeChanged += (_, e) =>
        {
            root.Width = e.Size.Width / options.UiScale;
            root.Height = e.Size.Height / options.UiScale;
        };
        return box;
    }

    // ---------- 절 ----------

    private void RegisterSections()
    {
        Add("overview", "개요", Icons.Home, Overview);
        Add("colors", "색·글자", Icons.Sparkle, Colors);
        Add("materials", "재질", Icons.Layers, Materials_);
        Add("contrast", "대비 시험", Icons.Contrast, ContrastSection);
        Add("buttons", "버튼", Icons.Square, Buttons);
        Add("cards", "카드", Icons.Card, Cards);
        Add("navigation", "탐색", Icons.Menu, NavigationSection);
        Add("toasts", "알림", Icons.Bell, Toasts);
        Add("dialog", "대화상자", Icons.Window, DialogSection);
        Add("chart", "그래프", Icons.Chart, ChartSection);
        Add("motion", "모션", Icons.Pulse, MotionSection);
        Add("settings", "설정", Icons.Settings, SettingsSection);
    }

    private void Add(string id, string label, string icon, Func<IEnumerable<UIElement>> build)
    {
        sections[id] = (label, icon, build);
        rail.Add(id, label, icon);
    }

    private void ShowSection(string id)
    {
        var (label, _, build) = sections[id];
        sectionTitle.Text = label;
        sectionHost.Children.Clear();
        var items = build().ToArray();
        foreach (var item in items) sectionHost.Children.Add(item);
        scroll.ChangeView(0, 0, null, true);
        sectionGeneration++;   // 장면 세대 (M12 G1-B "그리기 끝남")
        ui.Motion.Stagger(items);
        if (id == "toasts") ShowAllToasts();
        if (id == "dialog" && options.OpenDialog && !dialog.IsOpen)
        {
            pendingWork++;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                pendingWork--;
                if (FindById(root, "dialog.open") is Control opener) OpenDialog(opener, FocusState.Programmatic);
                QueueDiag();
            })) pendingWork--;
        }
        log.Write("section", new { id });
        QueueDiag();
    }

    private IEnumerable<UIElement> Overview()
    {
        var chips = Row(new StatusChip(ui, "chip.ok", Controls.Status.Ok, "정상"), new StatusChip(ui, "chip.warn", Controls.Status.Warn, "주의"),
            new StatusChip(ui, "chip.bad", Controls.Status.Bad, "끊김"), new StatusChip(ui, "chip.unknown", Controls.Status.Unknown, "알 수 없음"));
        yield return new GlassCard(ui, "card.status", "연결 상태", CardState.Normal, chips, "상태 칩");
        yield return new GlassCard(ui, "card.actions", "행동", CardState.Normal, Row(
            new GlassButton(ui, ButtonKind.Primary, "overview.start", "측정 시작", Icons.Play),
            new GlassButton(ui, ButtonKind.Secondary, "overview.quick", "빠른 진단"),
            new GlassButton(ui, ButtonKind.Ghost, "overview.more", "자세히"),
            new GlassButton(ui, ButtonKind.Icon, "overview.refresh", "새로 고침", Icons.Refresh)), "버튼 네 종류");
        yield return new GlassCard(ui, "card.timeline", "최근 2분", CardState.Normal, Chart("overview.chart"), "응답 시간 (ms)");
        yield return new GlassCard(ui, "card.loading", "불러오는 중", CardState.Loading);
    }

    private IEnumerable<UIElement> Colors()
    {
        var p = ui.Theme.Palette;
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = Space.S8 * 4, ItemHeight = Space.S8 + Space.S3 };
        foreach (var (name, brush) in new (string, Brush)[]
        {
            ("bg", ui.Brushes.Bg), ("ink", ui.Brushes.Ink), ("muted", ui.Brushes.Muted), ("faint", ui.Brushes.Faint),
            ("accent", ui.Brushes.Accent), ("accentInk", ui.Brushes.AccentInk), ("series2", ui.Brushes.Series2),
            ("ok", ui.Brushes.Ok), ("warn", ui.Brushes.Warn), ("bad", ui.Brushes.Bad), ("focusOuter", ui.Brushes.FocusOuter), ("edge", ui.Brushes.Edge),
        })
        {
            var swatch = new Border { Width = Size.ControlS, Height = Size.ControlS, CornerRadius = new CornerRadius(Radius.Inner - Space.S1), Background = brush, BorderBrush = ui.Brushes.Edge, BorderThickness = new Thickness(Size.Hairline) };
            var label = Fonts.Text(name, Typography.Label, ui.Brushes.Ink);
            label.VerticalAlignment = VerticalAlignment.Center;
            var row = Row(swatch, label);
            AutomationProperties.SetName(row, name);
            grid.Children.Add(row);
        }
        yield return new GlassCard(ui, "card.colors", "색 토큰", CardState.Normal, grid, p.Name);
        var type = new StackPanel { Spacing = Space.S3 };
        foreach (var style in Typography.All)
            type.Children.Add(Fonts.Text($"{style.Name} {style.Size.ToString(CultureInfo.InvariantCulture)} — 연결 품질 A, 응답 23 ms", style, ui.Brushes.Ink));
        yield return new GlassCard(ui, "card.type", "글자 단계", CardState.Normal, type, "Pretendard");
    }

    private IEnumerable<UIElement> Materials_()
    {
        var row = new Grid { ColumnSpacing = Space.S5 };
        var levels = new[] { (GlassLevel.Content, "두꺼운 유리", "카드, 표, 그래프"), (GlassLevel.Control, "보통 유리", "탐색, 도구 막대"), (GlassLevel.Float, "얇은 유리", "펼침, 도움말 (글자는 본문색만)"), (GlassLevel.Dialog, "차폐층 + 보통 유리", "대화상자, 알림") };
        for (int i = 0; i < levels.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (level, title, use) = levels[i];
            var stack = new StackPanel { Spacing = Space.S2 };
            stack.Children.Add(Fonts.Text(title, Typography.Label, ui.Brushes.Ink));
            stack.Children.Add(Fonts.Text(use, Typography.Caption, level == GlassLevel.Float ? ui.Brushes.Ink : ui.Brushes.Muted));
            var host = new GroupPanel("material." + level.ToString().ToLowerInvariant(), title) { Height = Space.S8 * 3 };
            if (level == GlassLevel.Dialog) host.Children.Add(new Border { Background = ui.Brushes.Scrim, CornerRadius = new CornerRadius(Radius.Outer) });
            host.Children.Add(ui.Glass.Surface(level, stack, Radius.Outer, new Thickness(Space.S4)));
            Grid.SetColumn(host, i);
            row.Children.Add(host);
        }
        var holder = new GroupPanel("materials.levels", "재질 네 단계");
        holder.Children.Add(row);
        yield return holder;
    }

    /// <summary>
    /// 대비 시험 장면 (설계 5.7, 7.3): 허용된 배경 범위의 경계색 바둑판과 빛 색 띠, 그래프 선을 유리 뒤에 깔고 그 위에 글자 토큰을 올린다.
    /// 무늬 색은 테마마다 계산하므로 테마가 바뀌면 다시 만든다.
    /// </summary>
    private IEnumerable<UIElement> ContrastSection()
    {
        var theme = ui.Effects.Mode.Theme;
        var swatches = ContrastScene.Swatches(theme).Select(c => new SolidColorBrush(TokenBrushes.ToColor(c))).ToArray();
        if (options.AltPattern) swatches = [swatches[1], swatches[0], .. swatches.Skip(2).Reverse()];   // 효과 줄이기 표면 검사: 무늬를 바꿔도 표면이 같아야 한다
        var scene = new GroupPanel("contrast.scene", "대비 시험 장면") { Height = Space.S8 * 8 };
        var pattern = new Grid();
        for (int c = 0; c < ContrastScene.CheckerCells; c++) pattern.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < ContrastScene.CheckerCells / 2; r++) pattern.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < ContrastScene.CheckerCells / 2; r++)
            for (int c = 0; c < ContrastScene.CheckerCells; c++)
            {
                // 위 절반: 어두운·밝은 경계를 번갈아 (강한 경계), 아래 절반: 빛 색 하나씩 띠
                var fill = r < ContrastScene.CheckerCells / 4 ? swatches[(r + c) % 2] : swatches[2 + c % (swatches.Length - 2)];
                var cell = new Rectangle { Fill = fill };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                pattern.Children.Add(cell);
            }
        AutomationProperties.SetAccessibilityView(pattern, AccessibilityView.Raw);
        scene.Children.Add(pattern);
        var lines = new LineChart(ui, "contrast.lines", ChartStyle.MaxMs) { VerticalAlignment = VerticalAlignment.Bottom };
        lines.SetData(Data.Primary, Data.Secondary, Data.Lost, "유리 뒤를 지나는 그래프 선");
        AutomationProperties.SetAccessibilityView(lines, AccessibilityView.Raw);
        scene.Children.Add(lines);

        var panels = new Grid { ColumnSpacing = Space.S5, Margin = new Thickness(Space.S6) };
        var levels = new[] { GlassLevel.Content, GlassLevel.Control, GlassLevel.Float, GlassLevel.Dialog };
        for (int i = 0; i < levels.Length; i++)
        {
            panels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var level = levels[i];
            string key = level.ToString().ToLowerInvariant();
            var stack = new StackPanel { Spacing = Space.S2 };
            stack.Children.Add(Measured(key + ".ink", "본문 글자 Aa 가나 23 ms", ui.Brushes.Ink));
            if (level != GlassLevel.Float) stack.Children.Add(Measured(key + ".muted", "보조 글자 Aa 가나 23 ms", ui.Brushes.Muted));
            var host = new GroupPanel("contrast." + key, key) { VerticalAlignment = VerticalAlignment.Center };
            if (level == GlassLevel.Float) host.Children.Add(BehindFloat());
            if (level == GlassLevel.Dialog) host.Children.Add(new Border { Background = ui.Brushes.Scrim, CornerRadius = new CornerRadius(Radius.Outer) });
            host.Children.Add(ui.Glass.Surface(level, stack, Radius.Outer, new Thickness(Space.S4)));
            Grid.SetColumn(host, i);
            panels.Children.Add(host);
        }
        scene.Children.Add(panels);
        yield return scene;
    }

    /// <summary>
    /// 떠 있는 유리 뒤에 오는 것 (설계 4.3의 5): 측정 글자 자리를 가로지르는 촘촘한 그래프 선과 글자.
    /// 두꺼운·보통 유리 뒤에는 배치 규칙상 이런 것이 오지 않으므로 떠 있는 유리에만 깐다 (G4 M2 1라운드 지적 5).
    /// </summary>
    private UIElement BehindFloat()
    {
        var layer = new Grid { IsHitTestVisible = false };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var brush in new[] { ui.Brushes.Ink, ui.Brushes.Muted, ui.Brushes.Ink, ui.Brushes.Muted })
            text.Children.Add(Fonts.Text("뒤에 있는 글자 Aa 가나 12 ms 뒤에 있는 글자", Typography.Body, brush));
        layer.Children.Add(text);
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = Space.S1 };
        foreach (var brush in new[] { ui.Brushes.Accent, ui.Brushes.Series2, ui.Brushes.Ok, ui.Brushes.Warn, ui.Brushes.Bad, ui.Brushes.Accent, ui.Brushes.Ink, ui.Brushes.Series2, ui.Brushes.Bad, ui.Brushes.Accent })
            lines.Children.Add(new Rectangle { Height = ChartStyle.Line, Fill = brush });
        layer.Children.Add(lines);
        AutomationProperties.SetAccessibilityView(layer, AccessibilityView.Raw);
        return layer;
    }

    /// <summary>실측 대비 대상 글자. --hide-measured-text면 이 요소만 투명하게 (공유 브러시는 그대로).</summary>
    private TextBlock Measured(string id, string text, Brush brush)
    {
        var t = Fonts.Text(text, Typography.Body, options.HideMeasuredText ? ui.Brushes.Transparent : brush);
        AutomationProperties.SetAutomationId(t, "contrast." + id);
        return t;
    }

    private IEnumerable<UIElement> Buttons()
    {
        var grid = new Grid { RowSpacing = Space.S4, ColumnSpacing = Space.S5 };
        var states = new[] { "기본", "포인터 올림", "비활성" };
        var kinds = new[] { (ButtonKind.Primary, "주요"), (ButtonKind.Secondary, "보조"), (ButtonKind.Ghost, "고스트"), (ButtonKind.Icon, "아이콘") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var _ in states) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        for (int s = 0; s < states.Length; s++)
        {
            var h = Fonts.Text(states[s], Typography.Caption, ui.Brushes.Muted);
            Grid.SetColumn(h, s + 1);
            grid.Children.Add(h);
        }
        for (int k = 0; k < kinds.Length; k++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var (kind, name) = kinds[k];
            var label = Fonts.Text(name, Typography.Label, ui.Brushes.Ink);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, k + 1);
            grid.Children.Add(label);
            for (int s = 0; s < states.Length; s++)
            {
                string id = $"btn.{kind.ToString().ToLowerInvariant()}.{(s == 0 ? "normal" : s == 1 ? "hover" : "disabled")}";
                var b = new GlassButton(ui, kind, id, kind == ButtonKind.Icon ? "새로 고침" : "측정 시작", kind is ButtonKind.Primary or ButtonKind.Icon ? (kind == ButtonKind.Icon ? Icons.Refresh : Icons.Play) : null);
                if (s == 1) b.ForceHover(true);
                if (s == 2) b.IsEnabled = false;
                Grid.SetRow(b, k + 1);
                Grid.SetColumn(b, s + 1);
                grid.Children.Add(b);
            }
        }
        yield return new GlassCard(ui, "card.buttons", "버튼 상태", CardState.Normal, grid, "초점은 탭으로 옮겨 보세요");
    }

    private IEnumerable<UIElement> Cards()
    {
        yield return new GlassCard(ui, "card.normal", "최근 측정", CardState.Normal, Row(new StatusChip(ui, "cards.chip", Controls.Status.Ok, "등급 A"), Fonts.Text("12분 · 끊김 0회", Typography.Body, ui.Brushes.Muted)));
        yield return new GlassCard(ui, "card.state.loading", "불러오는 중", CardState.Loading);
        yield return new GlassCard(ui, "card.state.empty", "빈 상태", CardState.Empty);
        yield return new GlassCard(ui, "card.state.error", "오류", CardState.Error);
    }

    private IEnumerable<UIElement> NavigationSection()
    {
        var text = Fonts.Wrapped("왼쪽 탐색은 탭 한 번으로 들어오고 위아래 화살표로 옮겨 다니며 Space나 Enter로 고릅니다. 아래는 여러 선택지 중 하나를 고르는 부품입니다.", Typography.Body, ui.Brushes.Muted);
        var stack = new StackPanel { Spacing = Space.S4 };
        stack.Children.Add(text);
        stack.Children.Add(new SegmentedChoice(ui, "nav.demo.range", "기간", [("1h", "1시간"), ("24h", "24시간"), ("7d", "7일")], "24h"));
        yield return new GlassCard(ui, "card.navigation", "탐색과 선택", CardState.Normal, stack);
    }

    private IEnumerable<UIElement> Toasts()
    {
        var row = Row(
            ToastButton("toasts.info", "정보", ToastKind.Info),
            ToastButton("toasts.success", "성공", ToastKind.Success),
            ToastButton("toasts.warning", "주의", ToastKind.Warning),
            ToastButton("toasts.error", "오류", ToastKind.Error));
        yield return new GlassCard(ui, "card.toasts", "알림", CardState.Normal, row, "오른쪽 위에 쌓입니다");
    }

    private GlassButton ToastButton(string id, string label, ToastKind kind)
    {
        var b = new GlassButton(ui, ButtonKind.Secondary, id, label + " 알림");
        b.Click += (_, _) => { ShowToast(kind); QueueDiag(); };
        return b;
    }

    private void ShowAllToasts()
    {
        while (toasts.Children.Count > 0) toasts.Children.RemoveAt(0);
        foreach (var k in Enum.GetValues<ToastKind>()) ShowToast(k, autoClose: false);
    }

    private void ShowToast(ToastKind kind, bool autoClose = true)
    {
        var (title, body) = kind switch
        {
            ToastKind.Success => ("측정을 저장했습니다", "12분 동안 끊김이 없었습니다."),
            ToastKind.Warning => ("응답이 느립니다", "최근 1분 동안 평균 응답이 180 ms입니다."),
            ToastKind.Error => ("인터넷 연결이 끊겼습니다", "공유기는 응답하지만 외부 대상 3곳이 응답하지 않습니다."),
            _ => ("측정을 시작했습니다", "1초마다 공유기와 외부 대상 3곳을 확인합니다."),
        };
        toasts.Show(kind, title, body, autoClose);
    }

    private IEnumerable<UIElement> DialogSection()
    {
        var open = new GlassButton(ui, ButtonKind.Secondary, "dialog.open", "DNS 캐시 비우기…");
        open.Click += (_, _) => OpenDialog(open, FocusState.Keyboard);
        yield return new GlassCard(ui, "card.dialog", "대화상자", CardState.Normal, open, "시스템을 바꾸는 동작만 확인을 묻습니다");
    }

    private void OpenDialog(Control opener, FocusState focus)
    {
        dialog.Open(opener, "DNS 캐시를 비울까요?", "Windows가 기억해 둔 주소 정보를 지웁니다. 사이트에 처음 접속할 때 잠깐 느릴 수 있습니다.", "비우기", "취소", focus);
        QueueDiag();
    }

    private IEnumerable<UIElement> ChartSection()
    {
        yield return new GlassCard(ui, "card.chart", "응답 시간", CardState.Normal, Chart("chart.main"), "실선: 인터넷 · 점선: 공유기 · 붉은 띠: 응답 없음");
    }

    private IEnumerable<UIElement> MotionSection()
    {
        var replay = new GlassButton(ui, ButtonKind.Secondary, "motion.replay", "차례로 등장", Icons.Refresh);
        replay.Click += (_, _) => ui.Motion.Stagger(sectionHost.Children.ToArray());
        var roller = new DigitRoller(ui, "motion.digits", 3, Typography.Display, "ms");
        roller.Set(23, animate: false);
        var roll = new GlassButton(ui, ButtonKind.Secondary, "motion.roll", "숫자 바꾸기");
        int step = 0;
        roll.Click += (_, _) => roller.Set(new[] { 23, 148, 31, 9, 260 }[++step % 5]);
        var blinkTarget = new StatusChip(ui, "motion.blink.chip", Controls.Status.Bad, "공유기 → 인터넷 구간");
        var blink = new GlassButton(ui, ButtonKind.Secondary, "motion.blink", "끊김 깜빡임");
        blink.Click += (_, _) => ui.Motion.Blink(blinkTarget, MotionTokens.BlinkTimes);
        var detail = new GlassCard(ui, "card.motion.detail", "상세 패널", CardState.Normal, Fonts.Text("카드를 누르면 같은 요소가 자리를 이어받아 커진다.", Typography.Body, ui.Brushes.Muted)) { Visibility = Visibility.Collapsed };
        var open = new GlassButton(ui, ButtonKind.Secondary, "motion.shared", "공통 요소 이동");
        open.Click += (_, _) =>
        {
            detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (detail.Visibility == Visibility.Visible) detail.UpdateLayout();
            if (detail.Visibility == Visibility.Visible) ui.Motion.SharedMove(open, detail, root);
        };
        var stack = new StackPanel { Spacing = Space.S4 };
        stack.Children.Add(new PulseLine(ui, "motion.pulse"));
        stack.Children.Add(Row(roller, roll));
        stack.Children.Add(Row(blinkTarget, blink));
        stack.Children.Add(Row(replay, open));
        yield return new GlassCard(ui, "card.motion", "모션", CardState.Normal, stack, ui.Motion.Reduced ? "움직임 줄이기 켜짐" : "토큰: 167 / 250 / 333 ms");
        yield return detail;
        yield return new GlassCard(ui, "card.motion.loading", "반복 (불러오는 중)", CardState.Loading);
    }

    /// <summary>원형 전환이 퍼져 나갈 점: 누른 선택지의 가운데 (살아 있는 화면 좌표).</summary>
    private void SetRevealOrigin(FrameworkElement? item)
    {
        if (item is null || item.ActualWidth <= 0) { ui.Theme.RevealOrigin = null; return; }
        ui.Theme.RevealOrigin = item.TransformToVisual(liveContent).TransformPoint(new Windows.Foundation.Point(item.ActualWidth / 2, item.ActualHeight / 2));
    }

    private IEnumerable<UIElement> SettingsSection()
    {
        var s = ui.Effects.Settings;
        var stack = new StackPanel { Spacing = Space.S2 };
        var theme = new SegmentedChoice(ui, "settings.theme", "테마", [("dark", "다크"), ("light", "라이트"), ("system", "Windows 설정")], ThemeValue(s.Theme));
        theme.Changed += v => { themeChoice.Set(v); };
        stack.Children.Add(theme);
        stack.Children.Add(Toggle("settings.aurora", "연한 오로라", s.Aurora, v => ui.Effects.Settings with { Aurora = v }));
        stack.Children.Add(Toggle("settings.reduceEffects", "효과 줄이기 (유리를 불투명하게)", s.ReduceEffects, v => ui.Effects.Settings with { ReduceEffects = v }));
        stack.Children.Add(Toggle("settings.reduceMotion", "움직임 줄이기", s.ReduceMotion, v => ui.Effects.Settings with { ReduceMotion = v }));
        yield return new GlassCard(ui, "card.settings", "화면 설정", CardState.Normal, stack, "바로 저장됩니다");
    }

    private SettingToggle Toggle(string id, string label, bool value, Func<bool, AppSettings> next)
    {
        var t = new SettingToggle(ui, id, label, value);
        t.Checked += (_, _) => ui.Effects.UpdateSettings(next(true), persist: true);
        t.Unchecked += (_, _) => ui.Effects.UpdateSettings(next(false), persist: true);
        return t;
    }

    // ---------- 데모 데이터 ----------

    private LineChart Chart(string id)
    {
        var c = new LineChart(ui, id, ChartStyle.MaxMs);
        c.SetData(Data.Primary, Data.Secondary, Data.Lost, "최근 2분 응답 시간: 인터넷 평균 31 ms, 공유기 평균 4 ms, 응답 없음 2번 (40초 전).");
        return c;
    }

    /// <summary>같은 값이 늘 같게 나오는 데모 계열 (캡처 비교용).</summary>
    private static class Data
    {
        public static double[] Primary { get; } = Series(120, 31, 9, 7);
        public static double[] Secondary { get; } = Series(120, 4, 1.5, 3);
        public static bool[] Lost { get; } = Enumerable.Range(0, 120).Select(i => i is 78 or 79).ToArray();

        private static double[] Series(int n, double mean, double swing, int seed) =>
            Enumerable.Range(0, n).Select(i => i is 78 or 79 ? double.NaN : mean + swing * Math.Sin((i + seed) * 0.37) + swing * 0.6 * Math.Sin((i * seed) * 0.11)).ToArray();
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    // ---------- 닫기 ----------

    private async void BeginClose()
    {
        if (closing) return;
        closing = true;
        runTimer?.Stop();
        // 진행 중인 테마 전환의 정리를 UI가 살아 있는 동안 기다린다 (M2 이월 1). 닫기 직전 정리 상태를 기록한다.
        await ui.Theme.ShutdownAsync(Waits.ThemeCleanup);
        log.Write("closeCleanup", new { snapshotLayers = ui.Theme.SnapshotLayersInTree, inTransition = ui.Theme.InTransition, rendering = ui.Theme.RenderingSubscribed });
        AppWindow.Hide();
        try { await beforeClose(); }
        finally
        {
            ui.Detach();   // OS 이벤트·렌더링 구독·진행 중인 전환·모션을 정리한 뒤 닫는다
            closeAllowed = true;
            Close();
        }
    }
}
