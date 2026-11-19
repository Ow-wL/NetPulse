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
/// 닫기 (M9 설계 A1): 측정 중 창 닫기는 트레이로 숨김(<see cref="CloseWindow"/>, AppShell.Resident.cs).
/// 종료(<see cref="BeginClose"/>): 진행 중인 테마 전환 정리 → 메모 초안·측정 저장 → 앱 정리 → 닫기.
/// </summary>
public sealed partial class AppShell : Window
{
    private readonly Ui ui;
    private readonly StartupOptions options;
    private readonly AppLog log;
    private readonly MeasurementHost host;
    private readonly DiagnosisHost diagnosis;
    private readonly RecentRecords recent;
    private readonly RecordMemos memos;
    private readonly Func<Task> beforeClose;
    private readonly GroupPanel root;
    private readonly Grid appGrid = new() { ColumnSpacing = Space.S5 };
    private readonly NavRail rail;
    private readonly ScreenHost screens;
    private readonly ToastHost toasts;
    private readonly DialogHost dialog;
    private readonly TextBlock diag;
    private readonly Button layoutProbe;
    private readonly Button? holdRelease;
    private readonly RowButton statusPill;
    private readonly TextBlock statusTop, statusBottom;
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse statusDot;
    private readonly DispatcherQueueTimer? runTimer;
    private readonly DispatcherQueueTimer selfTestTimer;
    private bool renderQueued, diagQueued, closing, closeAllowed, navigating;
    private readonly ObservationWatch observation = new();
    private UIElement liveContent = null!;

    /// <summary>설정 저장 경계 (M9 설계 B1).</summary>
    public SettingsStore Settings { get; }

    /// <summary>기록 옮기기·출입문 (M9 설계 B3).</summary>
    public NetPulse.App.Resident.ResidentServices Resident { get; }

    public AppShell(StartupOptions options, AppLog log, SettingsStore settings, MeasurementHost host, RecentRecords recent, RecordMemos memos, DiagnosisHost diagnosis, Func<Task> beforeClose,
        NetPulse.App.Resident.ResidentServices services)
    {
        this.options = options;
        this.log = log;
        this.host = host;
        this.diagnosis = diagnosis;
        this.recent = recent;
        this.beforeClose = beforeClose;
        this.memos = memos;
        Title = "NetPulse";
        AppWindow.ResizeClient(new SizeInt32(Size.WindowWidth, Size.WindowHeight));
        Settings = settings;
        Resident = services;
        ui = new Ui(this, options, log, settings.Current.Look, App.PersistLook(settings, log));

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
        // 시험 전용 (M10 D3): 멈춤 지점을 모두 푼다 (시험 주입이 켜진 실행에서만 만든다)
        if (TestHolds.Active)
        {
            holdRelease = new Button { Opacity = 0, Width = 0, Height = 0, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAutomationId(holdRelease, "diag.release");
            holdRelease.Click += (_, _) => TestHolds.ReleaseAll();
        }

        var context = new ScreenContext
        {
            Ui = ui, Host = host, Recent = recent, Toasts = toasts, Dialog = dialog, Options = options, Log = log,
            Diagnosis = diagnosis, Navigate = Navigate, QueueDiag = QueueDiag, SetRevealOrigin = SetRevealOrigin, IsClosing = () => closing,
            Memos = memos, OpenRecord = OpenRecord, OpenReport = OpenReport, WindowHandle = () => WinRT.Interop.WindowNative.GetWindowHandle(this),
            Settings = settings, Resident = services, TrayAvailable = () => TrayAvailable,
        };
        screens.Register("home", () => new HomeScreen(context));
        screens.Register("measure", () => new MeasureScreen(context));
        screens.Register("firstrun", () => new FirstRunScreen(context));
        screens.Register("profile", () => new ProfileEditor(context));
        screens.Register("settings", () => new SettingsScreen(context));
        screens.Register("diagnose", () => new DiagnoseScreen(context));
        screens.Register("records", () => new RecordsScreen(context));
        screens.Register("report", () => new ReportScreen(context));

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
        host.Finished += f => DispatcherQueue.TryEnqueue(() => { if (!closeAllowed) OnFinished(f); });   // 앱 안 끝 알림 (숨김이면 풍선은 알림 규칙이)
        recent.Changed += QueueDiag;
        ui.Effects.Changed += (_, _) => QueueDiag();
        ui.Motion.ActiveChanged += QueueDiag;
        ui.Theme.TransitionChanged += QueueDiag;
        dialog.Closed += _ => QueueDiag();
        if (options.MoveFail is not null) NetPulse.App.Resident.FaultMoveOps.HoldEnteredChanged += () => DispatcherQueue.TryEnqueue(QueueDiag);
        if (TestHolds.Active) TestHolds.Changed += () => DispatcherQueue.TryEnqueue(QueueDiag);
        root.GotFocus += (_, _) => QueueDiag();
        AddAccelerators();

        AppWindow.Closing += (_, e) => { if (closeAllowed) return; e.Cancel = true; CloseWindow(); };
        InitResident();
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
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (!closing) foreach (var id in new[] { "home", "measure", "diagnose", "records", "report" }) screens.Prepare(id); });   // 진단도 미리 (M6 bench diag-enter 40 ms대), 기록도 (M7 bench records-enter 50 ms대), 리포트도 (M8 bench report-enter 64 ms 한 번)
            // 기록 화면이 처음 쓰는 메서드는 백그라운드에서 미리 컴파일 (첫 상세 열기의 컴파일 시간, M7 bench records-open)
            _ = Design.JitWarmup.Start(typeof(RecordsScreen), typeof(Design.Controls.TimelinePanel), typeof(Design.Controls.TrendChart),
                typeof(Core.Presentation.RecordView), typeof(Core.Presentation.TimelineModel), typeof(Core.Analysis.EpisodeDetail),
                typeof(Core.Records.SymptomMarks), typeof(Core.Analysis.MeasurementComparison),
                typeof(ReportScreen), typeof(Design.Controls.ReportParts), typeof(Core.Report.ReportBuilder), typeof(Core.Report.ReportHtml),
                typeof(Core.Report.TimelineSvg), typeof(Core.Report.Redactor), typeof(Core.Report.ReportRequests));
            RenderStatus(host.Snapshot);
            QueueDiag();
            if (options.SelfTest is not null) selfTestTimer.Start();
            if (options.Bench) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunBench);
            if (options.BenchRecords) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunRecordsBench);
            if (options.BenchReport) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RunReportBench);
            if (options.OpenRecord is { } folder) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => OpenExternal(folder));
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

    /// <summary>기록 화면으로 가서 그 항목 상세를 연다 (null이면 목록, M7 4.2).</summary>
    private void OpenRecord(string? key)
    {
        Navigate("records", null);
        if (screens.Get("records") is RecordsScreen r) r.Show(key);
    }

    /// <summary>리포트 화면에서 그 기록을 고른다 (M8 설계 4.1).</summary>
    private void OpenReport(string key)
    {
        if (screens.Get("report") is ReportScreen pre) pre.Preselect(key);   // 들어가며 기본 측정으로 만들기 시작하지 않게
        Navigate("report", null);
        if (screens.Get("report") is ReportScreen r) r.Show(key);
    }

    /// <summary>다른 위치의 기록 열기 (시작 인수, M7 3.7). 경로는 앱 기록에 남기지 않는다.</summary>
    private void OpenExternal(string folder)
    {
        if (closing) return;
        Navigate("records", null);
        if (screens.Get("records") is RecordsScreen r) r.OpenExternal(folder);
    }

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
        if (holdRelease is not null) root.Children.Add(holdRelease);
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
        if (renderQueued || closeAllowed || hidden) return;   // 숨긴 동안 그리지 않는다: 다시 열 때 한 번 (M9 설계 A1)
        renderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            renderQueued = false;
            if (closeAllowed || hidden) return;   // 창을 닫은 뒤 큐에서 도는 그리기는 요소를 건드리지 않는다
            try { RenderProfile.Time("shell.render", () => Render(host.Snapshot)); }
            catch (Exception ex) { log.Write("renderFailed", new { type = ex.GetType().Name, message = ex.Message, at = ex.StackTrace?.Split(Environment.NewLine).Where(l => l.Contains("NetPulse")).Take(6) }); }
        });
    }

    private void Render(HostSnapshot s)
    {
        screens.Render(s);
        if (screens.Current is not HomeScreen && screens.Prepared("home") is HomeScreen home) home.Warm(s);
        // 상태 반응: 외부 응답 없음이나 링크 끊김이 관측되고 있으면 배경 빛이 붉게 (끝나면 원래대로).
        // 띠·링크 다운은 관측이지 판정이 아니다: 알림도 관측한 그것만 말한다 (끊김 여부·원인은 잠정 분석이 말한다, G4 M5 1라운드 B4·2라운드 3)
        // 종류는 이 장면에서 먼저 정하고 알약을 그린다 (M5 이월 3: 이전 종류로 한 번 그려지던 것).
        // 관측 전환·저장 실패 알림은 렌더와 떨어진 알림 규칙이 낸다 (M9 설계 A1·A4 [1-4]: 숨긴 동안에도, 장면을 버리지 않고)
        bool wasOutage = observation.Current != ObservationKind.None;
        observation.Update(s);
        bool outage = observation.Current != ObservationKind.None;
        RenderProfile.Time("shell.status", () => RenderStatus(s));
        if (outage != wasOutage) ui.Lights.SetState(outage);
        QueueDiag();
    }

    private void RenderStatus(HostSnapshot s)
    {
        (string top, string bottom, Brush dot) = s.State switch
        {
            HostState.Measuring when observation.Current == ObservationKind.LinkDown => ("링크 끊김 관측", "연결이 끊긴 상태가 이어지는 중", ui.Brushes.Bad),
            HostState.Measuring when observation.Current == ObservationKind.ExternalNone => ("응답 없음 관측", "외부 응답 없음이 이어지는 중", ui.Brushes.Bad),
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
        string? running = host.RunningId;
        _ = Task.Run(async () =>
        {
            // 목록을 다시 읽고, 방금 끝난 기록은 호스트가 가진 원본·분석을 그대로 넘긴다 (다시 읽지 않음, M7 구현 중 결정 3)
            await recent.Library.RefreshAsync(running).ConfigureAwait(false);
            if (f.Record is { } record && f.Analysis is { } analysis) recent.Library.Share(f.Result.Id, record, analysis);
            await recent.RefreshAsync(running).ConfigureAwait(false);
        });
        string reason = f.Result.EndReason switch { EndReason.Completed => "계획한 시간을 마쳤습니다.", EndReason.Stopped => "측정을 멈추고 저장했습니다.", _ => "측정이 끝났습니다." };
        if (hidden) { QueueRender(); return; }   // 숨김: 끝 알림은 풍선 (알림 규칙)
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
        TestHolds.ReleaseAll();    // 시험 전용 멈춤이 있으면 풀어 작업이 끝나게 한다 (M10 D3)
        instance?.MarkClosing();   // 두 번째 실행의 요청에는 closing으로 답한다 (A5)
        host.BlockStarts();
        runTimer?.Stop();
        // 기록을 옮기는 중이면 멈춤을 보낸다: 지금 기록 하나를 끝내거나 되돌리고 멈춘다 (M9 설계 A1 표). 아래에서 독점이 풀리기를 기다린다
        if (screens.Prepared("settings") is SettingsScreen settingsScreen) settingsScreen.StopMove();
        // 종료를 시작하면 화면의 프레임 구독·쌓인 프레임 작업부터 끊는다 (M7 이월 4, M9 설계 C2)
        screens.Suspend();
        ui.Pacer.Pause();
        log.Write("closeStep", new { step = "begin", thread = Environment.CurrentManagedThreadId, hidden });
        // 진단 억제는 첫 await 전에 (테마 정리를 기다리는 동안 결과를 저장·게시하지 않게, G4 1라운드 b4). 끝나기는 아래에서 기다린다
        var diagnosisStop = diagnosis.ShutdownAsync();
        // 리포트 내보내기 취소도 첫 await 전에 보낸다: 쓰는 중이면 조각 사이에서 멈추고 임시 파일을 지운다 (G4 M8 2라운드 b1)
        var reportStop = screens.Prepared("report") is ReportScreen report ? report.StopExportAsync() : Task.CompletedTask;
        await ui.Theme.ShutdownAsync(Waits.ThemeCleanup);
        log.Write("closeCleanup", new { snapshotLayers = ui.Theme.SnapshotLayersInTree, inTransition = ui.Theme.InTransition, rendering = ui.Theme.RenderingSubscribed });
        AppWindow.Hide();
        try
        {
            await diagnosisStop;   // 진단을 먼저 멈춘다: 억제 → 취소 → 최대 3초 (M6 설계 3.4)
            // 옮기기가 멈추고 되돌림·정리·독점 놓기까지 끝날 때를 기다린다 (기록 하나 복사 중이면 그 파일 사이에서 멈춘다).
            // 출입문 상태를 엿보는 대신 작업 자체에 합류한다 (G4 M9 1라운드 b1). 한도를 넘으면 기록하고 계속한다
            // 한도를 넘어도 끝까지 기다린다 (G4 M9 2라운드 b1): 옮기기가 독점을 놓기 전에는 초안 메모가 저장되지 않고, 끊으면 정리도 남는다.
            // 멈춤을 받았으니 지금 파일 하나를 끝내거나 되돌리면 곧 끝난다 (리포트 내보내기와 같은 선택: 남는 파일보다 늦은 닫기)
            var moving = Resident.Moves.Running;
            if (await Task.WhenAny(moving, Task.Delay(Waits.CloseWrites)) != moving)
            {
                log.Write("moveCloseWait", new { });
                await moving;
            }
            // 기록 화면의 메모 저장은 쓰기 큐에 들어가기 전(저장 시작 알림·앞 요청 대기)일 수 있어 호스트 종료와 함께 따로 기다린다
            // (호스트는 쓰기 큐만 기다린다, G4 M7 3라운드 1). 둘은 세대로 서로의 옛 글을 건너뛰므로 동시에 해도 된다
            var memoWrites = memos.DrainAsync(Waits.CloseWrites);
            var shutdown = Task.WhenAll(host.ShutdownAsync(), memoWrites, reportStop);
            if (await Task.WhenAny(shutdown, Task.Delay(Waits.CloseWrites + Waits.CloseWrites + Waits.CloseWrites)) != shutdown)
                log.Write("closeTimeout", new { state = host.Snapshot.State.ToString() });
            // 리포트 내보내기는 시간 한도 없이 끝까지 기다린다: 취소를 받았으니 곧 끝나고, 끝나기 전에 닫으면 임시 파일이 남을 수 있다
            // (G4 M8 1·2라운드 b1. 입출력 자체가 멈추면 운영체제가 오류로 끝낼 때까지 기다린다 — 남는 파일보다 늦은 닫기를 택함)
            if (!reportStop.IsCompleted)
            {
                log.Write("reportCloseWait", new { });
                await reportStop;
            }
            if (memoWrites.IsCompleted && !memoWrites.Result) log.Write("memoCloseTimeout", new { });
            var left = host.UnsavedDrafts();
            if (left.Count > 0) log.Write("memoUnsaved", new { marks = left });   // 메모 글은 남기지 않는다
            await beforeClose();
        }
        catch (Exception ex)
        {
            // async void라 예외가 부른 쪽(창 닫기·트레이·두 번째 실행)에 가지 않는다: 여기서 관찰해 기록하고, 아래에서 창은 닫되
            // 종료 코드로 실패를 드러낸다 (정상 종료 0과 구별, G4 M10 2라운드 2). 예외 종류만 남긴다
            log.Write("closeFailed", new { type = ex.GetType().Name });
            Environment.ExitCode = 70;   // EX_SOFTWARE: 닫기 중 예외로 저장·정리를 다 하지 못했다 (잘못된 인수 64와 같은 방식)
        }
        finally
        {
            // 닫은 뒤에는 화면 요소를 건드리는 큐 작업·타이머가 돌지 않게 먼저 끊는다
            // (게이트에서 닫을 때 드물게 WinUI 내부 예외 0xC000027B로 끝났다, G4 M5 2라운드 반영 중)
            closeAllowed = true;
            log.Write("closeStep", new { step = "detach", thread = Environment.CurrentManagedThreadId });
            toasts.StopAll();
            tray?.Dispose();
            ui.Detach();
            log.Write("closeStep", new { step = "close", thread = Environment.CurrentManagedThreadId });
            Close();
        }
    }
}
