using Microsoft.UI.Xaml;
using NetPulse.App.Design.Showcase;
using NetPulse.Core.Demo;
using NetPulse.Core.Measurement;
using NetPulse.Core.Timing;
using NetPulse.Design.Settings;
using NetPulse.Platform;

namespace NetPulse.App;

/// <summary>
/// 앱이 측정 엔진의 수명을 소유한다 (M1 설계 2절). 창은 구독만 소유한다.
/// 종료 순서: 취소 요청 → 실행 중인 측정 작업이 끝날 때까지 기다림(3초) → 기록 → 창 닫기 → 종료.
/// 기다리는 동안 UI 스레드를 막지 않는다 (창은 숨긴 상태로 남는다).
/// </summary>
public partial class App : Application
{
    private Window? window;
    private MeasurementEngine? engine;
    private AppLog? log;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => log?.Write("unhandled", new { type = e.Exception.GetType().Name, message = e.Message });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
        log = new AppLog(options.UserData);
        if (options.Showcase)
        {
            LaunchShowcase(options, log);
            return;
        }
        string mode;
        if (options.Demo is { } demo)
        {
            var scenario = Scenario.Load(ResolveScenario(demo));
            var clock = new ScaledClock(options.DemoSpeed, scenario.StartAt ?? DateTimeOffset.Now);
            engine = new MeasurementEngine(clock, new DemoProbe(scenario), scenario.ProbeTargets, TimeSpan.FromSeconds(1), scenario.RoundCount);
            mode = "demo:" + scenario.Name;
        }
        else
        {
            // M1: 대상 모델은 M3에서 만든다. 지금은 대상 없이 회차만 돈다.
            engine = new MeasurementEngine(new SystemClock(), new IcmpProbe(TimeSpan.FromMilliseconds(800)), [], TimeSpan.FromSeconds(1));
            mode = "live";
        }
        var resources = new
        {
            fonts = File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "Pretendard-Bold.otf")),
            scenarios = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "demo", "scenarios")),
        };
        log.Write("startup", new { mode, speed = options.DemoSpeed, version = typeof(App).Assembly.GetName().Version?.ToString(), resources });
        engine.Start();

        window = new ShellWindow(engine, mode, options.RunSeconds, ShutdownAsync);
        window.Activate();
    }

    /// <summary>부품 전시 화면 (M2). 측정 엔진 없이 뜬다. 이번 실행의 테마·오로라 인수는 저장하지 않는다.</summary>
    private void LaunchShowcase(StartupOptions options, AppLog appLog)
    {
        var (settings, status, detail) = AppSettings.Load(options.UserData);
        appLog.Write("settings", new { status = status.ToString().ToLowerInvariant(), detail });
        if (options.Theme is { } t) settings = settings with { Theme = t };
        if (options.Aurora is { } a) settings = settings with { Aurora = a };
        appLog.Write("startup", new { mode = "showcase", version = typeof(App).Assembly.GetName().Version?.ToString() });
        window = new ShowcaseWindow(options, appLog, settings, () =>
        {
            appLog.Write("shutdown", new { stopCompleted = true, rounds = 0, skipped = 0 });
            return Task.CompletedTask;
        });
        window.Activate();
    }

    private async Task ShutdownAsync()
    {
        if (engine is null) return;
        var stopping = engine;
        engine = null;
        bool completed = await stopping.StopAsync(TimeSpan.FromSeconds(3));
        var state = stopping.State;
        log?.Write("shutdown", new { stopCompleted = completed, rounds = state.Rounds, skipped = state.Skipped });
    }

    private static string ResolveScenario(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        return Path.Combine(AppContext.BaseDirectory, "demo", "scenarios", nameOrPath + ".json");
    }
}
