using Microsoft.UI.Xaml;
using NetPulse.App.Design.Showcase;
using NetPulse.Core.Demo;
using NetPulse.Core.Live;
using NetPulse.Design.Settings;
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
            var text = ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace;
            if (log is not null) log.Write("startupFailed", new { type = ex.GetType().Name, message = ex.Message, hresult = ex.HResult, at = ex.StackTrace?.Split(Environment.NewLine).Take(6) });
            else File.WriteAllText(Path.Combine(Path.GetTempPath(), "netpulse-startup-error.log"), text);
            throw;
        }
    }

    private void Launch()
    {
        var options = StartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        Directory.CreateDirectory(options.UserData);
        log = new AppLog(options.UserData);
        var (settings, status, detail) = AppSettings.Load(options.UserData);
        log.Write("settings", new { status = status.ToString().ToLowerInvariant(), detail });
        if (options.Theme is { } t) settings = settings with { Theme = t };
        if (options.Aurora is { } a) settings = settings with { Aurora = a };
        if (options.Showcase)
        {
            LaunchShowcase(options, log, settings);
            return;
        }

        string version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
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
        host = new MeasurementHost(world, options.UserData, new HostOptions { AppVersion = version, HoldStart = options.HoldStart });
        var recent = new RecentRecords(new RecordLibrary(() => world.RecordPaths, options.HoldAnalysis, options.HoldList));
        var memos = new RecordMemos(host.Notes, () => world.Now);
        memos.Saving += host.MemoSavingElsewhere;   // 측정 화면에 남은 옛 초안을 버리고 넣은 옛 쓰기도 건너뛴다 (M7 구현 중 결정 4, G4 M7 1라운드 a1)
        memos.Saved += host.MemoSavedElsewhere;
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
        window = new AppShell(options, log, settings, host, recent, memos, diagnosis, ShutdownAsync);
        window.Activate();
    }

    private void MarkForReplay()
    {
        if (host?.MarkSymptom() is { } t && host.Snapshot.SessionId is { } sid) _ = host.SaveMemoAsync(sid, t.Mark, "화상회의 중 끊김");
    }

    /// <summary>부품 전시 화면 (M2). 측정 없이 뜬다. 이번 실행의 테마·오로라 인수는 저장하지 않는다.</summary>
    private void LaunchShowcase(StartupOptions options, AppLog appLog, AppSettings settings)
    {
        appLog.Write("startup", new { mode = "showcase", version = typeof(App).Assembly.GetName().Version?.ToString() });
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

    private static string ResolveScenario(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        return Path.Combine(AppContext.BaseDirectory, "demo", "scenarios", nameOrPath + ".json");
    }
}
