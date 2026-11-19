using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.App.Resident;
using NetPulse.App.Screens;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;
using NetPulse.Design.Tokens;
using NetPulse.Platform;

namespace NetPulse.App;

/// <summary>
/// 상주 (M9 설계 A): 측정 중 창 닫기 = 트레이로 숨김, 트레이 메뉴, 알림(앱 안·풍선), 단일 실행 요청, 세션 종료.
/// 알림 입력은 호스트가 게시한 장면을 하나도 버리지 않고 순서대로 처리한다 [2-5] (렌더와 떨어져 있어 숨긴 동안에도 돈다 [1-4]).
/// </summary>
public sealed partial class AppShell
{
    private enum TrayCommand { None = 0, Open = 1, Mark = 2, Exit = 3 }

    private readonly ResidentNotifier notifier = new();
    private readonly ConcurrentQueue<HostSnapshot> published = new();
    private readonly List<object> notified = [];
    private TrayIcon? tray;
    private SingleInstance? instance;
    private bool drainQueued, hidden, hintOpen, sessionEnding;
    private string trayState = "none";
    private object? lastMenu;
    private DateTime lastTipAt;

    /// <summary>트레이를 쓸 수 있는가 (아이콘이 들어감).</summary>
    private bool TrayAvailable => tray?.Added == true;

    private bool CloseToTray => options.CloseToTray ?? Settings.Current.Resident.CloseToTray;

    private NoticePrefs Prefs
    {
        get
        {
            var r = Settings.Current.Resident;
            return new NoticePrefs(r.NotifyDropout, r.NotifyRecovered, r.NotifyFinished);
        }
    }

    private void InitResident()
    {
        host.Published += s =>
        {
            published.Enqueue(s);
            QueueDrain();
        };
        try
        {
            tray = new TrayIcon();
            tray.Selected += () => Reopen(toMeasure: false, "tray");
            tray.MenuRequested += ShowTrayMenu;
            tray.BalloonClicked += () => Reopen(toMeasure: true, "balloon");
            tray.TaskbarRestarted += ok =>
            {
                trayState = ok ? "added" : "unavailable";
                log.Write("resident", new { what = "taskbarRestarted", ok });
                if (!ok && hidden) Reopen(toMeasure: false, "trayLost");   // 숨은 실행을 구제 (A3)
            };
            tray.QueryEndSession += OnQueryEndSession;
            tray.EndSession += OnEndSession;
            trayState = !options.SimulateTrayOff && tray.Add(TooltipFor(host.Snapshot)) ? "added" : "unavailable";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            trayState = "unavailable";
            log.Write("resident", new { what = "trayFailed", type = ex.GetType().Name });
        }
        log.Write("resident", new { what = "tray", state = trayState });
    }

    /// <summary>App이 단일 실행 개체를 넘긴다: 두 번째 실행의 열기·종료 요청을 받는다.</summary>
    public void AttachInstance(SingleInstance owner) => instance = owner;

    /// <summary>두 번째 실행의 요청 (앱의 단일 실행 서버가 동의 뒤 이리로 넘긴다, M10 A2).</summary>
    public Task<bool> OnInstanceCommand(string command)
    {
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || sessionEnding) { accepted.TrySetResult(false); return; }   // 종료 중: closing으로 답한다
            if (command == "exit") BeginClose();
            else Reopen(toMeasure: false, "secondInstance");
            accepted.TrySetResult(true);
        })) accepted.TrySetResult(false);
        return accepted.Task;
    }

    // ---------------- 닫기 = 숨김 또는 종료 ----------------

    /// <summary>창 닫기(X, Alt+F4) — M9 설계 A1 표.</summary>
    private void CloseWindow()
    {
        if (closing || hintOpen) return;
        var state = host.Snapshot.State;
        bool hide = state is HostState.Measuring or HostState.Starting && CloseToTray && TrayAvailable;
        if (!hide) { BeginClose(); return; }
        if (!Settings.Current.Resident.TrayHintShown) { ShowHint(); return; }
        HideToTray();
    }

    private void ShowHint()
    {
        if (dialog.IsOpen) dialog.Close(false);   // 다른 확인이 열려 있으면 취소로 닫고 안내를 먼저
        hintOpen = true;
        void Closed(bool _)
        {
            dialog.Closed -= Closed;
            hintOpen = false;
            var choice = dialog.LastChoice;
            log.Write("resident", new { what = "hint", choice = choice.ToString() });
            if (choice == DialogChoice.Cancel) return;
            Settings.Update(f => f with { Resident = f.Resident with { TrayHintShown = true } });
            if (choice == DialogChoice.Confirm) HideToTray();
            else BeginClose();
        }
        dialog.Closed += Closed;
        dialog.Open(null, "창을 닫아도 측정을 계속할 수 있습니다",
            "트레이(작업 표시줄 오른쪽 아이콘)에서 측정을 계속합니다. 아이콘을 누르면 다시 열고, 끝내려면 트레이 메뉴의 종료를 고르세요. 설정에서 바꿀 수 있습니다.",
            "트레이에서 계속", "취소", alternate: "측정 멈추고 종료");
        QueueDiag();
    }

    private void HideToTray()
    {
        if (hidden || closing) return;
        hidden = true;
        screens.Suspend();
        ui.Pacer.Pause();
        ui.Theme.Suspend();
        ui.Motion.SetReduced(true);
        toasts.Hold();
        AppWindow.Hide();
        // 숨긴 뒤 남은 프레임 구독 수 (화면·테마·프레임 측정기, 0이어야 한다: 설계 A1, G4 M9 1라운드 b2)
        log.Write("resident", new { what = "hidden", host = host.Snapshot.State.ToString(), frameHooks = FrameHooks() });
        // 시험 전용 (M10 D3): 숨기면 멈춤 지점을 모두 푼다. 숨긴 창의 UIA 트리는 믿을 수 없어 diag.release를 누를 수 없다 (UI 시험 1차).
        // 멈춘 작업의 완료가 정확히 숨긴 동안 도착한다
        if (TestHolds.Active) TestHolds.ReleaseAll();
        // 실측 시험 전용 (M10 D4): 처음 숨길 때 시험 풍선을 정확히 한 번. 호출 결과만 남긴다 (실제로 보였는지는 사용자가 확인)
        if (options.LiveBalloonTest && !liveBalloonSent)
        {
            liveBalloonSent = true;
            bool called = tray?.ShowBalloon("NetPulse 시험 풍선", "실측 시험입니다. 이 풍선이 보이면 확인해 주세요.", false) == true;
            log.Write("liveBalloon", new { called });
        }
    }

    private bool liveBalloonSent;

    /// <summary>다시 열기. 화면은 그대로(강제 이동 없음), 풍선 클릭만 측정 화면으로.</summary>
    private void Reopen(bool toMeasure, string by)
    {
        if (closing) return;
        bool was = hidden;
        if (hidden)
        {
            hidden = false;
            ui.Theme.Resume();
            ui.Motion.SetReduced(ui.Effects.Mode.ReduceMotion);
            ui.Pacer.Resume();
            screens.Resume();
            try { RenderProfile.Time("shell.render", () => Render(host.Snapshot)); }
            catch (Exception ex) { log.Write("renderFailed", new { type = ex.GetType().Name }); }
        }
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        if (toMeasure && screens.CurrentId != "measure") Navigate("measure", null);
        if (was) toasts.Release();   // 숨긴 동안 쌓인 앱 안 알림
        int during = notifier.TakeHiddenSummary();
        if (was && during > 0) toasts.Show(ToastKind.Info, "숨긴 동안 끊김이 관측되었습니다", $"관측 {during}번. 측정 화면의 타임라인에서 확인하세요.");
        log.Write("resident", new { what = "reopened", by, wasHidden = was, hiddenStarts = during });
        QueueDiag();
    }

    private void ShowTrayMenu(int x, int y)
    {
        if (tray is null || closing) return;
        bool measuring = host.Snapshot.State == HostState.Measuring;
        TrayMenuItem[] items =
        [
            new((int)TrayCommand.Open, "열기", IsDefault: true),
            new((int)TrayCommand.Mark, "증상 표시", Enabled: measuring),
            new(0, "", Separator: true),
            new((int)TrayCommand.Exit, "종료"),
        ];
        lastMenu = new { items = items.Where(i => !i.Separator).Select(i => new { i.Text, i.Enabled }), at = DateTimeOffset.Now };
        log.Write("resident", new { what = "menu", measuring });
        int cmd = tray.ShowMenu(x, y, items);
        log.Write("resident", new { what = "menuChoice", cmd });
        switch ((TrayCommand)cmd)
        {
            case TrayCommand.Open: Reopen(toMeasure: false, "menu"); break;
            case TrayCommand.Mark:
                if (host.Snapshot.State == HostState.Measuring && host.MarkSymptom() is { } t)
                {
                    log.Write("resident", new { what = "markFromTray", t.Mark });
                    if (!hidden && screens.Current is MeasureScreen) QueueRender();
                }
                break;
            case TrayCommand.Exit: BeginClose(); break;
        }
        QueueDiag();
    }

    // ---------------- 알림 ----------------

    private void QueueDrain()
    {
        lock (published)
        {
            if (drainQueued) return;
            drainQueued = true;
        }
        if (!DispatcherQueue.TryEnqueue(Drain)) lock (published) drainQueued = false;
    }

    /// <summary>게시된 장면을 순서대로 알림 규칙에 먹인다 (UI 스레드). 화면 요소는 앱 안 알림만 건드린다.</summary>
    private void Drain()
    {
        lock (published) drainQueued = false;
        if (closeAllowed) return;
        var presence = Presence();
        var prefs = Prefs;
        HostSnapshot? last = null;
        while (published.TryDequeue(out var s))
        {
            last = s;
            foreach (var n in notifier.Feed(ResidentInput.From(s), presence, prefs)) Deliver(n);
        }
        if (last is not null) UpdateTooltip(last);
    }

    private void Deliver(ResidentNotice n)
    {
        if (n.InApp && !hidden)
        {
            var kind = n.Kind == NoticeKind.StorageFailing ? ToastKind.Error : n.Warning ? ToastKind.Warning : ToastKind.Info;
            toasts.Show(kind, n.Title, n.Body, autoClose: n.Kind != NoticeKind.StorageFailing);
        }
        if (n.Balloon)
        {
            bool called = tray?.ShowBalloon(n.Title, n.Body, n.Warning) == true;
            var entry = new { kind = n.Kind.ToString(), title = n.Title, at = DateTimeOffset.Now, called };
            lock (notified)
            {
                notified.Add(entry);
                if (notified.Count > 20) notified.RemoveAt(0);
            }
            log.Write("resident", new { what = "balloon", kind = n.Kind.ToString(), title = n.Title, called });
        }
        QueueDiag();
    }

    /// <summary>창 상태: 숨김(숨김·최소화) / 앞 / 뒤.</summary>
    private WindowPresence Presence()
    {
        if (hidden || !AppWindow.IsVisible) return WindowPresence.Hidden;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return WindowPresence.Hidden;
        return GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this) ? WindowPresence.Front : WindowPresence.Back;
    }

    private void UpdateTooltip(HostSnapshot s)
    {
        if (tray is null) return;
        var text = TooltipFor(s);
        if (text == tray.Tooltip) return;
        // 경과 시간만 바뀌면 1초에 한 번까지
        bool onlyElapsed = s.State == HostState.Measuring && notifier.Current == ObservationKind.None && tray.Tooltip.StartsWith("NetPulse · 측정 중", StringComparison.Ordinal);
        if (onlyElapsed && DateTime.UtcNow - lastTipAt < Waits.TrayTooltip) return;
        lastTipAt = DateTime.UtcNow;
        tray.SetTooltip(text);
    }

    private string TooltipFor(HostSnapshot s) => s.State switch
    {
        HostState.Measuring or HostState.Stopping when notifier.Current == ObservationKind.LinkDown => "NetPulse · 링크 끊김 관측 중",
        HostState.Measuring or HostState.Stopping when notifier.Current == ObservationKind.ExternalNone => "NetPulse · 외부 응답 없음 관측 중",
        HostState.Measuring => "NetPulse · 측정 중 " + TimelineModel.Seconds(s.ElapsedMs) + " 경과",
        HostState.Stopping => "NetPulse · 측정을 멈추는 중",
        HostState.Starting => "NetPulse · 측정을 시작하는 중",
        _ => "NetPulse · 측정하지 않음",
    };

    // ---------------- 세션 종료 (A6) ----------------

    /// <summary>
    /// 질의: 되돌릴 수 있는 것만 한다 — 새 시작을 막는다 (구현 중 결정: 종료가 취소되면 앱이 그대로 계속돼야 하므로
    /// 진단 멈춤·내보내기 취소·단일 실행 종료 표시처럼 되돌릴 수 없는 것은 확정 때 먼저 한다, DECISIONS "M9 구현 중 결정").
    /// </summary>
    private void OnQueryEndSession()
    {
        if (sessionEnding || closing) return;
        sessionEnding = true;
        host.BlockStarts();
        log.Write("sessionEnd", new { step = "query" });
    }

    private void OnEndSession(bool ending)
    {
        if (!ending)
        {
            if (!sessionEnding) return;
            sessionEnding = false;
            if (!closing) host.UnblockStarts();
            log.Write("sessionEnd", new { step = "cancelled" });
            return;
        }
        OnQueryEndSession();
        // 확정: 취소부터 보낸다 (기다리지 않음) — 진단 억제, 리포트 내보내기 취소 토큰, 두 번째 실행에는 closing
        instance?.MarkClosing();
        if (screens.Prepared("settings") is SettingsScreen settingsScreen) settingsScreen.StopMove();   // 옮기기 작업자가 되돌리고 스스로 독점을 놓는다
        _ = diagnosis.ShutdownAsync();
        var reportStop = screens.Prepared("report") is ReportScreen report ? report.StopExportAsync() : Task.CompletedTask;
        // UI 스레드는 데이터 작업이 끝나기를 막고 기다리기만 한다 (A6). 하나의 기한 안에서 순서대로 합류한다 (G4 M9 1라운드 b1):
        // 옮기기 정리(독점을 놓아야 초안 메모가 저장된다) → 내보내기 정리(임시 파일) → 측정 봉인 → 메모. 모두 UI 문맥을 쓰지 않는다
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var moving = Resident.Moves.Running;
        var flush = Task.Run(async () =>
        {
            await moving.ConfigureAwait(false);
            await reportStop.ConfigureAwait(false);
            await host.ShutdownAsync().ConfigureAwait(false);
            await memos.DrainAsync(Waits.CloseWrites).ConfigureAwait(false);
        });
        bool done = flush.Wait(Waits.SessionEnd);
        log.Write("sessionEnd", new { step = "flushed", done, moveDone = moving.IsCompleted, exportDone = reportStop.IsCompleted,
            ms = watch.ElapsedMilliseconds, state = host.Snapshot.State.ToString() });
        tray?.Remove();
    }

    /// <summary>지금 걸린 프레임(CompositionTarget.Rendering) 구독 수: 화면 + 테마 경계 검사 + 프레임 측정기.</summary>
    private int FrameHooks() => screens.FrameHooks + (ui.Theme.RenderingSubscribed ? 1 : 0) + (ui.Pacer.Paused ? 0 : 1);

    private object ResidentDiag() => new
    {
        trayState,
        hidden,
        suspended = screens.SuspendedCount,
        pacerPaused = ui.Pacer.Paused,
        frameHooks = FrameHooks(),
        hintShown = Settings.Current.Resident.TrayHintShown,
        closeToTray = CloseToTray,
        tooltip = tray?.Tooltip,
        notifications = NotifiedSnapshot(),
        lastMenu,
        windowState = Presence().ToString().ToLowerInvariant(),
        trayWindow = tray is null ? 0 : (long)tray.Handle,
        hiddenObserved = notifier.HiddenSegmentStarts,
        settingsPersist = Settings.CanPersist,
        workGate = Resident.WorkGate.Closed,
        probed = (diagnosis.Probes as NetPulse.Core.Demo.DemoDiagnosticProbes)?.Probed,   // 데모: 진단 검사기가 받은 대상 (B4)
    };

    private object[] NotifiedSnapshot() { lock (notified) return [.. notified]; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
