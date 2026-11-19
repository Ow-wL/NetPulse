using Microsoft.UI.Xaml;
using NetPulse.App.Design.Showcase;
using NetPulse.Core.Demo;
using NetPulse.Core.Live;
using NetPulse.Design.Settings;
using NetPulse.App.Resident;
using NetPulse.Platform;

namespace NetPulse.App;

/// <summary>
/// 앱 (M5 설계 4.1): 세계(실측/데모)를 고르고 측정 수명 호스트와 메인 창을 만든다. <b>측정을 시작하지 않고</b> 뜬다
/// (첫 실행이면 첫 실행 안내, 아니면 홈). 종료 순서는 창이 정한다: 테마 전환 정리 → 메모 초안·측정 저장 → 닫기.
/// </summary>
public partial class App : Application
{
    private Window? window;
    private MeasurementHost? host;
    private AppLog? log;

    public App()
    {
        InitializeComponent();
        // 처리되지 않은 예외는 종류·HRESULT·우리 코드 쪽 스택 줄까지 남긴다 (드문 종료 충돌의 원인을 다음에 바로 보려고)
        UnhandledException += (_, e) => log?.Write("unhandled", new
        {
            type = e.Exception.GetType().Name, hresult = e.Exception.HResult, message = e.Message,
            at = e.Exception.StackTrace?.Split(Environment.NewLine).Where(l => l.Contains("NetPulse", StringComparison.Ordinal)).Take(8),
        });
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log?.Write("unhandledDomain", new
        {
            type = e.ExceptionObject.GetType().Name,
            at = (e.ExceptionObject as Exception)?.StackTrace?.Split(Environment.NewLine).Where(l => l.Contains("NetPulse", StringComparison.Ordinal)).Take(8),
        });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 화면 앱: 전체(2세대) 수집이 프레임을 막지 않게 (M5 이월 2, M6 bench: 긴 측정의 잠정 분석이 2세대 수집을 일으켜
        // 화면 이동 프레임이 예산 가까이 튀었다). 메모리가 모자라면 런타임이 그래도 막는 수집을 한다
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        try { Launch(); }
        catch (Exception ex)
        {
            // 기록을 만들기 전에 실패해도 원인을 남긴다 (임시 폴더, 예외 종류와 메시지만)
            var text = NetPulse.Core.Records.RecordFolderRules.Sanitize(ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
            if (log is not null) log.Write("startupFailed", new { type = ex.GetType().Name, message = ex.Message, hresult = ex.HResult, at = ex.StackTrace?.Split(Environment.NewLine).Take(6) });
            else File.WriteAllText(Path.Combine(Path.GetTempPath(), "netpulse-startup-error.log"), text);
            throw;
        }
    }

    /// <summary>두 번째 실행이 보낸 요청을 받는 곳. 동의 화면 → 셸 순서로 바뀐다 (단일 실행 서버는 한 번만 연다).</summary>
    private Func<string, Task<bool>> instanceHandler = _ => Task.FromResult(false);

    /// <summary>
    /// 시작 (M10 설계 A2 [2-1]): 동의 전에는 인수 읽기, 앱 기록, 단일 실행 소유권, 사용 조건 화면만 한다.
    /// 설정·세계·기록 복구·도서관·측정 호스트·진단·트레이·셸은 동의 뒤(<see cref="LaunchAfterConsent"/>)에 만든다.
    /// 명령줄 기능 요청(<c>--start</c>, <c>--screen</c>, 기록 열기)은 그 뒤에서 한 번 처리된다.
    /// </summary>
    private void Launch()
    {
        var options = StartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        Directory.CreateDirectory(options.UserData);
        log = new AppLog(options.UserData);
        if (options.TestInjectionAllowed)
        {
            if (options.FakeVersion is { } fake) AppInfo.Override(fake);
            NetPulse.App.Legal.LegalTexts.FailForTest = options.NoticesFail;
        }
        SingleInstance? owner = null;
        if (!options.Showcase)
        {
            owner = ClaimInstance(options, log);
            if (owner is null) return;   // 요청을 넘기고 끝남 (Environment.Exit)
            owner.Serve(command => instanceHandler(command));
        }
        if (TermsConsent.IsAccepted(options.UserData))
        {
            LaunchAfterConsent(options, log, owner, termsSaved: true);
            return;
        }
        var terms = new TermsWindow(options, log);
        window = terms;
        instanceHandler = command => OnUi(terms, () =>
        {
            if (command == "exit") { terms.CloseNow(); Quit(owner); }
            else terms.BringToFront();
        });
        terms.Declined += () => { terms.CloseNow(); Quit(owner); };
        terms.Accepted += saved =>
        {
            var current = log;
            LaunchAfterConsent(options, current!, owner, saved);
            terms.CloseNow();
        };
        terms.Activate();
    }

    /// <summary>창의 UI 스레드에서 한다. 큐에 넣지 못하면 false (두 번째 실행에 closing으로 답한다).</summary>
    private static Task<bool> OnUi(Window w, Action action)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!w.DispatcherQueue.TryEnqueue(() => { action(); done.TrySetResult(true); })) done.TrySetResult(false);
        return done.Task;
    }

    /// <summary>동의하지 않음·동의 화면 중 종료 요청: 기록·설정을 만들지 않고 끝낸다 (종료 코드 0).</summary>
    private void Quit(SingleInstance? owner)
    {
        log?.Write("shutdown", new { stopCompleted = true, rounds = 0, skipped = 0, terms = "declined" });
        owner?.MarkClosing();
        owner?.Dispose();
        Exit();
    }

    private void LaunchAfterConsent(StartupOptions options, AppLog log, SingleInstance? owner, bool termsSaved)
    {
        var load = SettingsFormat.Load(options.UserData);
        log.Write("settings", new { status = load.Status.ToString().ToLowerInvariant(), detail = load.Detail, repaired = load.Repaired });   // 칸 이름만, 값 없음
        var settings = new SettingsStore(options.UserData, load);
        // 이번 실행 인수는 저장하지 않는다 (메모리에만): 저장소를 거치지 않고 시작 값만 바꾼다
        var look = settings.Current.Look;
        if (options.Theme is { } t) look = look with { Theme = t };
        if (options.Aurora is { } a) look = look with { Aurora = a };
        if (look != settings.Current.Look) settings = new SettingsStore(options.UserData, load with { Settings = load.Settings with { Look = look } });
        if (options.Showcase)
        {
            LaunchShowcase(options, log, settings);
            return;
        }

        string version = AppInfo.Current.Version;
        IAppWorld world;
        if (options.Demo is { } demo)
        {
            var scenario = Scenario.Load(ResolveScenario(demo));
            var timeBase = new DemoTimeBase(scenario.StartAt ?? DateTimeOffset.Now, options.DemoSpeed);
            // 데모의 대체 위치도 사용자 폴더 안에 둔다 (공용 임시 폴더면 다른 실행의 데모 기록이 최근 측정에 섞인다)
            // 재현 인수 --demo-mark: 시간축이 정확히 그 회차일 때 증상 표시 + 메모 (실제 버튼과 같은 호스트 경로).
            // 화면 쪽 알림에서 누르면 600배속에서는 이미 멈춤 회차까지 지나 있었다
            (long, Action)? mark = options.DemoMark is long m ? (m, MarkForReplay) : null;
            world = new DemoAppWorld(new DemoWorld(scenario, timeBase), options.UserData, new DemoRunRules(options.DemoHold, options.DemoSpeedAfter, mark),
                Path.Combine(options.UserData, "demo-fallback"));
        }
        else world = new LiveAppWorld(options.UserData);
        // 기록 위치 (M9 설계 B3): 설정의 기본 위치·옛 위치. 끊긴 옮기기의 일지를 먼저 검증한다 (흐름 9, [2-8])
        var folders = new RecordFolderSettings(settings);
        var recordStartup = StartRecords(world, settings, folders, load, log);
        var workGate = new RecordWorkGate();
        host = new MeasurementHost(world, options.UserData, new HostOptions { AppVersion = version, HoldStart = options.HoldStart, WorkGate = workGate });
        var recent = new RecentRecords(new RecordLibrary(() => world.RecordPaths, options.HoldAnalysis, options.HoldList) { WorkGate = workGate });
        var memos = new RecordMemos(host.Notes, () => world.Now) { WorkGate = workGate };
        memos.Saving += host.MemoSavingElsewhere;   // 측정 화면에 남은 옛 초안을 버리고 넣은 옛 쓰기도 건너뛴다 (M7 구현 중 결정 4, G4 M7 1라운드 a1)
        memos.Saved += host.MemoSavedElsewhere;
        memos.SavingFailed += ex => log?.Write("memoSavingFailed", new { type = ex.GetType().Name });   // 예외 종류만 (글·경로 없음, G4 M7 4라운드 3)
        var resources = new
        {
            fonts = File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "Pretendard-Bold.otf")),
            scenarios = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "demo", "scenarios")),
        };
        log.Write("startup", new
        {
            mode = world.Kind, speed = options.DemoSpeed, version, resources, profiles = host.LoadStatus.ToString(),
            replay = new { options.Screen, options.Start, options.DemoHold, speedAfter = options.DemoSpeedAfter?.ToString(), options.HoldAnalysis, options.HoldStart,
                options.HoldList, openRecord = options.OpenRecord is null ? null : "external" },   // 경로는 남기지 않는다
        });
        var mh = host;
        var diagnosis = new DiagnosisHost(world.Diagnostics, new DiagnosisStore(Path.Combine(options.UserData, "diagnoses")), () => mh.RunningId, () => world.Now, version);
        ApplyDiagnosisDefaults(diagnosis, world.Diagnostics.Settings, settings, log);
        var moves = new RecordMoveService(world, workGate, host, folders, path => SingleInstance.FinalPath(path));
        // 옮기기 고장 주입(프로세스 강제 종료 포함)은 데모 + 직접 준 격리 사용자 폴더에서만 (G4 M9 1라운드 b7: 실측 기본 폴더의 실제 기록에서 켜지지 않게)
        if (options.MoveFail is { } moveFail)
        {
            if (options.Demo is not null && options.UserDataGiven) FaultMoveOps.Configure(moves, moveFail);
            else log.Write("moveFailRefused", new { });
        }
        var shell = new AppShell(options, log, settings, host, recent, memos, diagnosis, ShutdownAsync,
            new ResidentServices(moves, workGate, recordStartup, TermsNotSaved: !termsSaved));
        window = shell;
        shell.AttachInstance(owner!);
        instanceHandler = shell.OnInstanceCommand;
        window.Activate();
    }

    private void MarkForReplay()
    {
        if (host?.MarkSymptom() is { } t && host.Snapshot.SessionId is { } sid) _ = host.SaveMemoAsync(sid, t.Mark, "화상회의 중 끊김");
    }

    /// <summary>부품 전시 화면 (M2). 측정 없이 뜬다. 이번 실행의 테마·오로라 인수는 저장하지 않는다.</summary>
    private void LaunchShowcase(StartupOptions options, AppLog appLog, SettingsStore settings)
    {
        appLog.Write("startup", new { mode = "showcase", version = AppInfo.Current.Version });
        window = new ShowcaseWindow(options, appLog, settings, () =>
        {
            appLog.Write("shutdown", new { stopCompleted = true, rounds = 0, skipped = 0 });
            return Task.CompletedTask;
        });
        window.Activate();
    }

    /// <summary>창이 측정을 멈추고 저장한 뒤 부른다: 끝 기록.</summary>
    private Task ShutdownAsync()
    {
        var s = host?.Snapshot;
        log?.Write("shutdown", new
        {
            stopCompleted = s?.Finished?.Result.SealFailed != true, rounds = s?.Rounds.Count ?? 0,
            endReason = s?.Finished?.Result.EndReason.ToString(), unsaved = s?.Finished?.Result.Unsaved,
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 단일 실행 (M9 설계 A5). 첫 실행이면 그 개체(프로세스가 끝날 때까지 쥔다), 아니면 떠 있는 실행에 열기·종료를 넘기고
    /// 프로세스를 끝낸다: 0 = 넘김(종료는 대상이 끝남까지), 2 = <c>--exit</c>인데 떠 있는 것 없음, 3 = 10초 안에 답 없음.
    /// </summary>
    private static SingleInstance? ClaimInstance(StartupOptions options, AppLog log)
    {
        string key = SingleInstance.KeyFor(options.UserData);
        string command = options.Exit ? "exit" : "open";
        if (!options.Exit && SingleInstance.TryAcquire(key) is { } first) return first;
        AllowSetForegroundWindow(-1);   // 받는 쪽이 창을 앞으로 가져올 수 있게 (사용자가 연 이 실행이 전경 권한을 가진다)
        var (owner, delivered, sawOther) = SingleInstance.ClaimOrSendAsync(key, command, NetPulse.Design.Tokens.Waits.InstanceClaim).GetAwaiter().GetResult();
        int code;
        if (options.Exit)
        {
            owner?.Dispose();
            code = !sawOther ? 2 : owner is not null ? 0 : 3;
        }
        else if (owner is not null) return owner;   // 앞 실행이 종료 중이었다: 이어서 첫 실행으로
        else code = delivered ? 0 : 3;
        log.Write("instance", new { command, delivered, sawOther, code });
        Environment.Exit(code);
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// 시작 때 기록 위치 (M9 설계 B3 흐름 9): 일지가 있으면 한 덩어리로 검증해 맞으면 우리 .moving만 정리하고, 맞지 않으면 records 묶음을
    /// 저장 금지로 두고 아무것도 자동으로 하지 않는다. 그다음 세계에 위치를 준다 (경로는 기록에 남기지 않는다).
    /// </summary>
    private static RecordStartup StartRecords(IAppWorld world, SettingsStore settings, RecordFolderSettings folders, SettingsLoad load, AppLog log)
    {
        var f = folders.Read();
        string? problem = null;
        bool interrupted = false;
        if (load.Repaired.Contains("records.move")) { problem = "shape"; interrupted = true; }
        else if (f.Move is not null) (interrupted, problem) = RecordMoveService.Recover(f, f.Folder ?? world.DefaultRecordFolder);
        if (problem is not null) settings.BlockRecords();
        world.SetRecordFolders(f.Folder, f.Extra);
        log.Write("records", new { folder = f.Folder is null ? "default" : "custom", extra = f.Extra.Count, interrupted, problem });
        return new RecordStartup(interrupted, problem);
    }

    /// <summary>진단 기본값 (B4): 설정 값이 있고 맞으면 세계의 기본 위에. 맞지 않는 쌍은 기본값 + 기록(칸 이름만).</summary>
    /// <param name="baseSettings">세계의 기본 진단 설정 (실측: Core 기본값, 데모: 시나리오 값). 설정 값은 이 위에 얹는다</param>
    internal static void ApplyDiagnosisDefaults(DiagnosisHost diagnosis, NetPulse.Core.Diagnostics.DiagnosisSettings baseSettings, SettingsStore settings, AppLog log)
    {
        var d = settings.Current.Diagnosis;
        var (parsed, rejected) = NetPulse.Core.Diagnostics.DiagnosisDefaults.From(d.TcpTargets, d.Domains);
        var next = baseSettings;
        if (d.TcpTargets is not null && !rejected.Contains("tcpTargets")) next = next with { TcpTargets = parsed.TcpTargets };
        if (d.Domains is not null && !rejected.Contains("domains")) next = next with { Domains = parsed.Domains };
        diagnosis.SetSettings(next);
        if (rejected.Count > 0) log.Write("diagnosisDefaults", new { rejected });
    }

    /// <summary>보기 설정 저장 (Effects가 부른다). 실패는 종류만 기록.</summary>
    internal static Action<AppSettings> PersistLook(SettingsStore store, AppLog log) => look =>
    {
        if (store.Update(f => f with { Look = look }, out var why) == SettingsSaveResult.Failed) log.Write("settingsSaveFailed", new { what = "look", type = why });
    };

    private static string ResolveScenario(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        return Path.Combine(AppContext.BaseDirectory, "demo", "scenarios", nameOrPath + ".json");
    }
}
