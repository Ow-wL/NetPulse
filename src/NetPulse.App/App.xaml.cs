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
            world = new DemoAppWorld(new DemoWorld(scenario, timeBase), options.UserData, new DemoRunRules(options.DemoHold, options.DemoSpeedAfter));
        }
        else world = new LiveAppWorld(options.UserData);
        host = new MeasurementHost(world, options.UserData, new HostOptions { AppVersion = version, HoldStart = options.HoldStart });
        var recent = new RecentRecords(() => world.RecordPaths, holdAnalysis: options.HoldAnalysis);
        var resources = new
        {
            fonts = File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "Pretendard-Bold.otf")),
            scenarios = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "demo", "scenarios")),
        };
        log.Write("startup", new
        {
            mode = world.Kind, speed = options.DemoSpeed, version, resources, profiles = host.LoadStatus.ToString(),
            replay = new { options.Screen, options.Start, options.DemoHold, speedAfter = options.DemoSpeedAfter?.ToString(), options.HoldAnalysis, options.HoldStart },
        });
        window = new AppShell(options, log, settings, host, recent, ShutdownAsync);
        window.Activate();
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
