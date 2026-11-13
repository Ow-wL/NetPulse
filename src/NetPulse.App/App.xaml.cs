using Microsoft.UI.Xaml;
using NetPulse.Core.Demo;
using NetPulse.Core.Measurement;
using NetPulse.Core.Timing;
using NetPulse.Platform;

namespace NetPulse.App;

/// <summary>
/// 앱이 측정 엔진의 수명을 소유한다 (M1 설계 2절). 창은 구독만 소유한다.
/// 종료 순서: 취소 요청 → 실행 중인 측정 작업이 끝날 때까지 기다림(3초) → 기록 → 종료.
/// </summary>
public partial class App : Application
{
    private ShellWindow? window;
    private MeasurementEngine? engine;
    private AppLog? log;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => log?.Write("unhandled", new { type = e.Exception.GetType().Name, message = e.Message });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = StartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        log = new AppLog(options.UserData);
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

        window = new ShellWindow(engine, mode, options.RunSeconds);
        window.Closed += (_, _) => Shutdown();
        window.Activate();
    }

    private void Shutdown()
    {
        if (engine is null) return;
        var stopping = engine;
        engine = null;
        // UI 스레드를 막지 않도록 다른 스레드에서 기다린다. 제한시간 안에 끝났는지 기록한다.
        bool completed = Task.Run(() => stopping.StopAsync(TimeSpan.FromSeconds(3))).GetAwaiter().GetResult();
        var state = stopping.State;
        log?.Write("shutdown", new { stopCompleted = completed, rounds = state.Rounds, skipped = state.Skipped });
    }

    private static string ResolveScenario(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        return Path.Combine(AppContext.BaseDirectory, "demo", "scenarios", nameOrPath + ".json");
    }
}
