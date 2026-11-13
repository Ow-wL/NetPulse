using Microsoft.UI.Xaml;
using NetPulse.App.Design.Showcase;
using NetPulse.Core.Demo;
using NetPulse.Core.Detection;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
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
    private MeasurementSession? session;
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
        string? liveStatus = null;
        if (options.Demo is { } demo)
        {
            var scenario = Scenario.Load(ResolveScenario(demo));
            var clock = new ScaledClock(options.DemoSpeed, scenario.StartAt ?? DateTimeOffset.Now);
            engine = new MeasurementEngine(clock, new DemoProbe(scenario), scenario.ProbeTargets, TimeSpan.FromSeconds(1), scenario.RoundCount);
            mode = "demo:" + scenario.Name;
        }
        else
        {
            mode = "live";
            liveStatus = StartLive(options, log);
        }
        var resources = new
        {
            fonts = File.Exists(Path.Combine(AppContext.BaseDirectory, "Fonts", "Pretendard-Bold.otf")),
            scenarios = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "demo", "scenarios")),
        };
        log.Write("startup", new { mode, speed = options.DemoSpeed, version = typeof(App).Assembly.GetName().Version?.ToString(), resources });
        if (session is null) engine!.Start();

        window = new ShellWindow(engine!, mode, options.RunSeconds, ShutdownAsync, liveStatus);
        window.Activate();
    }

    /// <summary>
    /// 실시간 모드 (M3 설계 9절): 탐지 제안 프로필로 세션을 돌린다. 첫 실행 확인 화면은 M5.
    /// 제안이 없거나 시작하지 못하면 대상 없이 회차만 돈다 (이유를 상태와 기록에 남긴다. 주소는 남기지 않는다).
    /// </summary>
    private string StartLive(StartupOptions options, AppLog appLog)
    {
        DetectionResult detection;
        try { detection = DetectionPlanner.Plan(NetworkInfo.Snapshot(DetectionPlanner.RouteProbeAddress)); }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or DllNotFoundException or EntryPointNotFoundException)
        {
            detection = new DetectionResult(DetectionStatus.Unknown, null, ex.GetType().Name, null, Tristate.Unknown, 0);
        }
        appLog.Write("detection", new { status = detection.Status.ToString(), kind = detection.Kind?.ToString(), @virtual = detection.Virtual.ToString(),
            targets = detection.Proposal?.ActiveTargets.Count ?? 0 });
        if (detection.Proposal is { } profile)
        {
            try
            {
                var fallback = DiskRecordRoot.Defaults().Fallback;
                session = MeasurementSession.Start(new SessionOptions
                {
                    Profile = profile, Clock = new SystemClock(), ProbeFor = t => ProbeFactory.For(t, profile.Timeout),
                    Primary = new DiskRecordRoot(Path.Combine(options.UserData, "records"), "primary"), Fallback = fallback,
                    Routes = new RouteObserver(), Power = new PowerEvents(),
                    AppVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                });
                engine = session.Engine;
                appLog.Write("session", new { id = session.Id, location = session.Status.LocationKind });
                return $"실시간 · 대상 {profile.ActiveTargets.Count}개";
            }
            catch (SessionStartException ex)
            {
                appLog.Write("sessionFailed", new { message = ex.Message, inner = ex.InnerException?.GetType().Name });
            }
        }
        engine = new MeasurementEngine(new SystemClock(), new IcmpProbe(TimeSpan.FromMilliseconds(800)), [], TimeSpan.FromSeconds(1));
        return "측정 대상 없음 · " + (detection.Notice ?? detection.Status.ToString());
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
        if (session is { } running)
        {
            session = null;
            engine = null;
            var result = await running.StopAsync();
            log?.Write("shutdown", new
            {
                stopCompleted = !result.SealFailed, rounds = running.Engine.State.Rounds, skipped = running.Engine.State.Skipped,
                endReason = result.EndReason.ToString(), observed = result.Summary?.Observed, committed = result.Summary?.Committed, unsaved = result.Unsaved,
            });
            return;
        }
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
