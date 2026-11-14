using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.App.Screens;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;
using Windows.Graphics;

namespace NetPulse.App;

/// <summary>
/// 메인 창 (M5 설계 4.2). 층: 배경 빛 → 앱 격자(왼쪽 탐색, 오른쪽 화면) → 알림 → 대화상자 → 진단 값. 전시 화면과 같은 구성이다.
/// 측정 수명은 <see cref="MeasurementHost"/>(Core)가 갖고, 창은 그 장면을 UI 스레드로 옮겨 보이는 화면만 다시 그린다.
/// 제목 표시줄은 넓히지 않고 테마 색으로 칠한다 (설계 4.2, M11 재검토).
/// 닫기: 진행 중인 테마 전환 정리 → 메모 초안·측정 저장(트레이는 M9) → 앱 정리 → 닫기.
/// </summary>
public sealed partial class AppShell : Window
{
    private readonly Ui ui;
    private readonly StartupOptions options;
    private readonly AppLog log;
    private readonly MeasurementHost host;
    private readonly DiagnosisHost diagnosis;
    private readonly RecentRecords recent;
    private readonly Func<Task> beforeClose;
    private readonly GroupPanel root;
    private readonly Grid appGrid = new() { ColumnSpacing = Space.S5 };
    private readonly NavRail rail;
    private readonly ScreenHost screens;
    private readonly ToastHost toasts;
    private readonly DialogHost dialog;
    private readonly TextBlock diag;
    private readonly Button layoutProbe;
    private readonly RowButton statusPill;
    private readonly TextBlock statusTop, statusBottom;
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse statusDot;
    private readonly DispatcherQueueTimer? runTimer;
    private readonly DispatcherQueueTimer selfTestTimer;
    private bool renderQueued, diagQueued, closing, closeAllowed, navigating, lastLinkDown;
    private HostState lastState;
    private bool lastOutage, lastStorageFailing;
    private UIElement liveContent = null!;

    public AppShell(StartupOptions options, AppLog log, AppSettings settings, MeasurementHost host, RecentRecords recent, DiagnosisHost diagnosis, Func<Task> beforeClose)
    {
        this.options = options;
        this.log = log;
        this.host = host;
        this.diagnosis = diagnosis;
        this.recent = recent;
        this.beforeClose = beforeClose;
        Title = "NetPulse";
        AppWindow.ResizeClient(new SizeInt32(Size.WindowWidth, Size.WindowHeight));
        ui = new Ui(this, options, log, settings);

        root = new GroupPanel("app.root", "NetPulse") { Background = ui.Brushes.Bg };
        rail = new NavRail(ui, "app.nav", "NetPulse");
        rail.Add("home", "홈", Icons.Home);
        rail.Add("measure", "측정", Icons.Pulse);
        rail.Add("diagnose", "진단", Icons.Diagnose);
        rail.Add("records", "기록", Icons.Menu);
        rail.Add("report", "리포트", Icons.Report);
        rail.AddBottom("settings", "설정", Icons.Settings);
        statusDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = Size.MarkerDot, Height = Size.MarkerDot, Fill = ui.Brushes.Muted, VerticalAlignment = VerticalAlignment.Center };
        statusTop = Fonts.Text("", Typography.Caption, ui.Brushes.Muted);
        statusBottom = Fonts.Text("", Typography.Label, ui.Brushes.Ink);
        var pillRow = new Grid { ColumnSpacing = Space.S3 };
        pillRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pillRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pillRow.Children.Add(statusDot);
        var pillTexts = new StackPanel();
        pillTexts.Children.Add(statusTop);
        pillTexts.Children.Add(statusBottom);
        Grid.SetColumn(pillTexts, 1);
        pillRow.Children.Add(pillTexts);
        statusPill = new RowButton(ui, "app.status", "측정 상태", pillRow, pill: true);
        statusPill.Click += (_, _) => Navigate("measure", null);
        rail.Footer = statusPill;

        screens = new ScreenHost(ui);
        toasts = new ToastHost(ui, bottom: true);
        dialog = new DialogHost(ui, appGrid);
        diag = new TextBlock { Opacity = 0, IsHitTestVisible = false, Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(diag, "diag.state");
        // 시험 전용: 누르면 지금 화면의 배치 검사를 기록에 남긴다 (캡처한 그 장면 그대로, M5 G5)
        layoutProbe = new Button { Opacity = 0, Width = 0, Height = 0, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(layoutProbe, "diag.layout");
        layoutProbe.Click += (_, _) => log.Write("selftest", new { kind = "layout", screen = screens.CurrentId, report = LayoutProbe.Report(root, diag, [toasts]) });

        var context = new ScreenContext
        {
            Ui = ui, Host = host, Recent = recent, Toasts = toasts, Dialog = dialog, Options = options, Log = log,
            Diagnosis = diagnosis, Navigate = Navigate, QueueDiag = QueueDiag, SetRevealOrigin = SetRevealOrigin, IsClosing = () => closing,
        };
        screens.Register("home", () => new HomeScreen(context));
        screens.Register("measure", () => new MeasureScreen(context));
        screens.Register("firstrun", () => new FirstRunScreen(context));
        screens.Register("profile", () => new ProfileEditor(context));
        screens.Register("settings", () => new SettingsScreen(context));
        screens.Register("diagnose", () => new DiagnoseScreen(context));
        screens.Register("records", () => new PendingScreen(context, "records", "기록", "측정 목록, 끊김 목록과 통계, 분석, 비교를 이 화면에서 보게 됩니다."));
        screens.Register("report", () => new PendingScreen(context, "report", "리포트", "통신사에 전달할 리포트를 이 화면에서 만들게 됩니다."));

        BuildLayout();
        var scaled = ScaleHost();
        Content = ui.Theme.Host(scaled);
        liveContent = scaled;
        ui.Root = root;
        ui.Theme.Apply(ui.Effects.Mode);
        ui.Lights.SetState(false, animate: false);

        rail.Selected += id => { if (!navigating) Navigate(id, null); };
        host.Changed += QueueRender;
        diagnosis.Changed += QueueRender;
        host.Finished += f => DispatcherQueue.TryEnqueue(() => { if (!closeAllowed) OnFinished(f); });
        recent.Changed += QueueDiag;
        ui.Effects.Changed += (_, _) => QueueDiag();
        ui.Motion.ActiveChanged += QueueDiag;
        ui.Theme.TransitionChanged += QueueDiag;
        dialog.Closed += _ => QueueDiag();
        root.GotFocus += (_, _) => QueueDiag();
        AddAccelerators();

        AppWindow.Closing += (_, e) => { if (closeAllowed) return; e.Cancel = true; BeginClose(); };
        selfTestTimer = DispatcherQueue.CreateTimer();
        selfTestTimer.Interval = MotionTokens.Slow * 3;
        selfTestTimer.IsRepeating = false;
        selfTestTimer.Tick += (_, _) => RunSelfTest();
        root.Loaded += (_, _) =>
        {
            log.Write("loaded", null);
            _ = recent.RefreshAsync(host.RunningId);
            _ = Task.Run(diagnosis.LoadRecent);
            StartScreen();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (!closing) foreach (var id in new[] { "home", "measure" }) screens.Prepare(id); });
            RenderStatus(host.Snapshot);
            QueueDiag();
            if (options.SelfTest is not null) selfTestTimer.Start();
            if (options.Bench) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunBench);
        };
        if (options.RunSeconds > 0)
        {
            runTimer = DispatcherQueue.CreateTimer();
            runTimer.Interval = TimeSpan.FromSeconds(options.RunSeconds);
            runTimer.Tick += (_, _) => BeginClose();
            runTimer.Start();
        }
    }

    public Ui Ui => ui;

    private void BuildLayout()
    {
        appGrid.Padding = new Thickness(Space.S5);
        appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        appGrid.Children.Add(rail);
        Grid.SetColumn(screens, 1);
        appGrid.Children.Add(screens);
        root.Children.Add(ui.Lights.Root);
        root.Children.Add(appGrid);
        root.Children.Add(toasts);
        root.Children.Add(dialog);
        root.Children.Add(diag);
        root.Children.Add(layoutProbe);
    }

    /// <summary>150% 상당 배치 (M2 설계 5.8와 같음).</summary>
    private UIElement ScaleHost()
    {
        if (options.UiScale <= 1) return root;
        var box = new Viewbox { Stretch = Stretch.Fill, Child = root };
        void Fit(double w, double h)
        {
            root.Width = w / options.UiScale;
            root.Height = h / options.UiScale;
        }
        SizeChanged += (_, e) => Fit(e.Size.Width, e.Size.Height);
        // 처음 크기 알림이 오지 않는 경우가 있어(관측: 루트가 내용의 자연 크기로 배치됨) 그릇이 배치될 때도 창 크기에 맞춘다
        box.Loaded += (_, _) => Fit(Bounds.Width, Bounds.Height);
        return box;
    }

    private async void StartScreen()
    {
        string first = options.Screen ?? (host.Snapshot.State == HostState.NoProfile && !options.Start ? "firstrun" : "home");
        if (options.Start)
        {
            if (host.CurrentProfile is null && host.World is DemoAppWorld demo && demo.World.Detect().Proposal is { } p)
                host.SaveProfiles(new NetPulse.Core.Targets.ProfileSet([p], p.Id));   // 데모 재현: 프로필이 없으면 데모 탐지 제안으로
            await host.StartAsync();
            first = options.Screen ?? "measure";
        }
        Navigate(first, null);
    }

    // ---------------- 탐색 ----------------

    private static readonly HashSet<string> RailItems = ["home", "measure", "diagnose", "records", "report", "settings"];

    private void Navigate(string id, FrameworkElement? from)
    {
        if (closing) return;
        navigating = true;
        try
        {
            if (RailItems.Contains(id)) rail.Select(id);
            else if (id == "firstrun") rail.Select("home");
            else if (id == "profile") rail.Select(ProfileEditor.ReturnTo == "settings" ? "settings" : ProfileEditor.ReturnTo == "firstrun" ? "home" : "measure");
        }
        finally { navigating = false; }
        screens.Show(id, host.Snapshot, from);
        log.Write("screen", new { id });
        QueueDiag();
    }

    private void SetRevealOrigin(FrameworkElement? item)
    {
        if (item is null || liveContent is null) return;
        var center = item.TransformToVisual(liveContent).TransformPoint(new Windows.Foundation.Point(item.ActualWidth / 2, item.ActualHeight / 2));
        ui.Theme.RevealOrigin = center;
    }

    private void AddAccelerators()
    {
        var mark = new KeyboardAccelerator { Key = Windows.System.VirtualKey.M, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        mark.Invoked += (_, e) =>
        {
            if (host.Snapshot.State != HostState.Measuring || dialog.IsOpen) return;
            if (screens.CurrentId != "measure") Navigate("measure", null);
            if (screens.Current is MeasureScreen m) m.MarkSymptom();
            e.Handled = true;
        };
        root.KeyboardAccelerators.Add(mark);
        root.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape || dialog.IsOpen) return;
            if (screens.Current?.Escape() == true) e.Handled = true;
        };
    }

    // ---------------- 장면 반영 ----------------

    private void QueueRender()
    {
        if (renderQueued || closeAllowed) return;
        renderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            renderQueued = false;
            if (closeAllowed) return;   // 창을 닫은 뒤 큐에서 도는 그리기는 요소를 건드리지 않는다
            try { RenderProfile.Time("shell.render", () => Render(host.Snapshot)); }
            catch (Exception ex) { log.Write("renderFailed", new { type = ex.GetType().Name, message = ex.Message, at = ex.StackTrace?.Split(Environment.NewLine).Where(l => l.Contains("NetPulse")).Take(6) }); }
        });
    }

    private void Render(HostSnapshot s)
    {
        screens.Render(s);
        RenderProfile.Time("shell.status", () => RenderStatus(s));
        // 상태 반응: 외부 응답 없음이나 링크 끊김이 관측되고 있으면 배경 빛이 붉게 (끝나면 원래대로).
        // 띠·링크 다운은 관측이지 판정이 아니다: 알림도 관측한 그것만 말한다 (끊김 여부·원인은 잠정 분석이 말한다, G4 M5 1라운드 B4·2라운드 3)
        bool externalNone = s.Measuring && s.Bands.LastOrDefault(b => b.Kind == BandKind.ExternalNone) is { OpenEnd: true };
        bool linkDown = s.Measuring && s.LinkDowns.Any(d => d.Ongoing);
        bool outage = externalNone || linkDown;
        if (outage != lastOutage)
        {
            ui.Lights.SetState(outage);
            if (outage)
                toasts.Show(ToastKind.Warning, linkDown ? "링크 끊김 관측" : "외부 응답 없음 관측",
                    (linkDown ? "이 PC의 네트워크 연결이 끊긴 상태가 관측되고 있습니다." : "모든 외부 대상이 응답하지 않는 회차가 이어지고 있습니다.")
                    + " 끊김을 느꼈다면 증상을 표시하세요 (Ctrl+M).");
            else if (s.Measuring)
                toasts.Show(ToastKind.Info, "관측이 끝났습니다", lastLinkDown ? "링크 끊김 관측이 끝났습니다." : "외부 응답 없음 관측이 끝났습니다.");
            lastOutage = outage;
        }
        lastLinkDown = linkDown;
        bool storage = s.Status?.StorageFailing == true;
        if (storage && !lastStorageFailing) toasts.Show(ToastKind.Error, "기록을 저장하지 못하고 있습니다", "측정은 계속하며 다시 시도합니다.", autoClose: false);
        lastStorageFailing = storage;
        if (lastState is HostState.Measuring or HostState.Stopping && s.State == HostState.Idle && lastOutage)
        {
            ui.Lights.SetState(false);
            lastOutage = false;
        }
        lastState = s.State;
        QueueDiag();
    }

    private void RenderStatus(HostSnapshot s)
    {
        (string top, string bottom, Brush dot) = s.State switch
        {
            HostState.Measuring when lastOutage && lastLinkDown => ("링크 끊김 관측", "연결이 끊긴 상태가 이어지는 중", ui.Brushes.Bad),
            HostState.Measuring when lastOutage => ("응답 없음 관측", "외부 응답 없음이 이어지는 중", ui.Brushes.Bad),
            HostState.Measuring => ("측정 중", TimelineModel.Seconds(s.ElapsedMs) + " 경과", ui.Brushes.Ok),
            HostState.Stopping => ("측정 중", "멈추는 중", ui.Brushes.Warn),
            HostState.Starting => ("측정", "시작하는 중", ui.Brushes.Warn),
            HostState.NoProfile => ("측정 구성 없음", "첫 실행 안내를 여세요", ui.Brushes.Muted),
            _ => ("측정하지 않음", s.Finished is not null ? "방금 측정을 마침" : "측정을 시작하세요", ui.Brushes.Muted),
        };
        statusTop.Text = top;
        statusBottom.Text = bottom;
        statusDot.Fill = dot;
        AutomationProperties.SetName(statusPill, $"{top}, {bottom}");
    }

    private void OnFinished(FinishedMeasurement f)
    {
        _ = recent.RefreshAsync(host.RunningId);
        string reason = f.Result.EndReason switch { EndReason.Completed => "계획한 시간을 마쳤습니다.", EndReason.Stopped => "측정을 멈추고 저장했습니다.", _ => "측정이 끝났습니다." };
        if (f.Result.Unsaved > 0 || f.Result.SealFailed)
            toasts.Show(ToastKind.Warning, "측정이 끝났습니다 · 일부 저장 안 됨", $"저장하지 못한 회차 {f.Result.Unsaved}개", autoClose: false);
        else toasts.Show(ToastKind.Success, "측정이 끝났습니다", reason);
        QueueRender();
    }

    // ---------------- 닫기 ----------------

    private async void BeginClose()
    {
        if (closing) return;
        closing = true;
        runTimer?.Stop();
        await ui.Theme.ShutdownAsync(Waits.ThemeCleanup);
        log.Write("closeCleanup", new { snapshotLayers = ui.Theme.SnapshotLayersInTree, inTransition = ui.Theme.InTransition, rendering = ui.Theme.RenderingSubscribed });
        AppWindow.Hide();
        try
        {
            await diagnosis.ShutdownAsync();   // 진단을 먼저 멈춘다: 억제 → 취소 → 최대 3초 (M6 설계 3.4)
            var shutdown = host.ShutdownAsync();
            if (await Task.WhenAny(shutdown, Task.Delay(Waits.CloseWrites + Waits.CloseWrites + Waits.CloseWrites)) != shutdown)
                log.Write("closeTimeout", new { state = host.Snapshot.State.ToString() });
            var left = host.UnsavedDrafts();
            if (left.Count > 0) log.Write("memoUnsaved", new { marks = left });   // 메모 글은 남기지 않는다
            await beforeClose();
        }
        finally
        {
            // 닫은 뒤에는 화면 요소를 건드리는 큐 작업·타이머가 돌지 않게 먼저 끊는다
            // (게이트에서 닫을 때 드물게 WinUI 내부 예외 0xC000027B로 끝났다, G4 M5 2라운드 반영 중)
            closeAllowed = true;
            toasts.StopAll();
            ui.Detach();
            Close();
        }
    }
}
