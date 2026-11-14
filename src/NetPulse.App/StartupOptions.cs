using System.Globalization;
using NetPulse.Design.Settings;

namespace NetPulse.App;

/// <summary>
/// 실행 인수. 모르는 인수는 거부한다.
/// 시험용 인수(M2 설계 5.7)는 Windows 설정을 바꾸지 않고 같은 코드 경로를 타게 한다. 기록에 "시뮬레이션"으로 남는다.
/// </summary>
public sealed record StartupOptions
{
    /// <summary>데모 시나리오 이름 또는 경로. 없으면 실제 측정</summary>
    public string? Demo { get; init; }
    public double DemoSpeed { get; init; } = 1;
    /// <summary>사용자 데이터 폴더. 없으면 %LOCALAPPDATA%\NetPulse (테스트는 실행마다 새 폴더를 준다)</summary>
    public string UserData { get; init; } = "";
    /// <summary>0보다 크면 그 시간 뒤 스스로 닫힌다 (자동화용)</summary>
    public int RunSeconds { get; init; }

    // ---- M5 (설계 4.1): 캡처·bench 재현용. 기록 머리와 앱 기록에 남는다 ----
    /// <summary>시작 화면 (home, measure, firstrun, profile, settings, diagnose, records, report)</summary>
    public string? Screen { get; init; }
    /// <summary>저장된 프로필로 바로 측정을 시작 (데모에서 프로필이 없으면 데모 탐지 제안으로)</summary>
    public bool Start { get; init; }
    /// <summary>데모: 지금 측정이 이 회차 + 0.5초에 닿으면 시간축이 멈춘다</summary>
    public long? DemoHold { get; init; }
    /// <summary>데모: 지금 측정이 이 회차에 닿으면 배속을 바꾼다</summary>
    public (long Round, double Speed)? DemoSpeedAfter { get; init; }
    /// <summary>최근 측정 분석을 시작하지 않는다 (불러오는 중 화면)</summary>
    public bool HoldAnalysis { get; init; }
    /// <summary>측정 시작 직전에 멈춘다 (시작 중 화면)</summary>
    public bool HoldStart { get; init; }
    /// <summary>데모: 지금 측정이 이 회차에 닿으면 증상을 표시하고 메모를 붙인다 (캡처에서 표시가 멈춘 시각 가장자리에 오지 않게)</summary>
    public long? DemoMark { get; init; }
    /// <summary>한 번 도는 모션을 이 진행도(0~1)에서 멈춘다 (화면 전환 캡처)</summary>
    public double? MotionHold { get; init; }

    /// <summary>부품 전시 화면 (M2)</summary>
    public bool Showcase { get; init; }
    /// <summary>이번 실행에만 쓰는 설정 덮어쓰기 (저장하지 않음)</summary>
    public ThemePreference? Theme { get; init; }
    public bool? Aurora { get; init; }
    public bool Problem { get; init; }
    public string? Section { get; init; }
    public double UiScale { get; init; } = 1;
    public bool FreezeMotion { get; init; }
    public bool HideMeasuredText { get; init; }
    public bool Bench { get; init; }
    public string? SelfTest { get; init; }
    /// <summary>원형 전환을 이 진행도(0~1)에서 멈춘다 (캡처용)</summary>
    public double? RevealHold { get; init; }
    /// <summary>대비 시험 장면의 무늬를 바꾼다 (효과 줄이기 표면 검사용)</summary>
    public bool AltPattern { get; init; }
    /// <summary>시작할 때 대화상자 절의 대화상자를 연다 (배치 검사·캡처용)</summary>
    public bool OpenDialog { get; init; }

    /// <summary>시뮬레이션: 고대비(aquatic|desert), 투명 효과 끔, 애니메이션 끔, 배터리 절약, 저성능</summary>
    public string? SimulateHighContrast { get; init; }
    public bool SimulateTransparencyOff { get; init; }
    public bool SimulateAnimationsOff { get; init; }
    public bool SimulateEnergySaver { get; init; }
    public bool SimulateLowPerformance { get; init; }

    public bool AnySimulation => SimulateHighContrast is not null || SimulateTransparencyOff || SimulateAnimationsOff || SimulateEnergySaver || SimulateLowPerformance;

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var o = new StartupOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} 뒤에 값이 필요합니다.");
            bool OnOff(string v) => v switch { "on" => true, "off" => false, _ => throw new ArgumentException($"on 또는 off여야 합니다: {v}") };
            o = args[i] switch
            {
                "--demo" => o with { Demo = Next() },
                "--demo-speed" => o with { DemoSpeed = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--user-data" => o with { UserData = Next() },
                "--run-seconds" => o with { RunSeconds = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--screen" => o with { Screen = Next() is var sc && ScreenIds.Contains(sc) ? sc : throw new ArgumentException($"알 수 없는 화면입니다: {sc}") },
                "--start" => o with { Start = true },
                "--demo-hold" => o with { DemoHold = long.Parse(Next(), CultureInfo.InvariantCulture) },
                "--demo-speed-after" => o with { DemoSpeedAfter = ParseSpeedAfter(Next()) },
                "--hold-analysis" => o with { HoldAnalysis = true },
                "--hold-start" => o with { HoldStart = true },
                "--demo-mark" => o with { DemoMark = long.Parse(Next(), CultureInfo.InvariantCulture) },
                "--motion-hold" => o with { MotionHold = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--showcase" => o with { Showcase = true },
                "--theme" => o with
                {
                    Theme = Next() switch
                    {
                        "dark" => ThemePreference.Dark, "light" => ThemePreference.Light, "system" => ThemePreference.System,
                        var v => throw new ArgumentException($"테마는 dark, light, system 중 하나여야 합니다: {v}"),
                    },
                },
                "--aurora" => o with { Aurora = OnOff(Next()) },
                "--state" => o with { Problem = Next() switch { "normal" => false, "problem" => true, var v => throw new ArgumentException($"상태는 normal 또는 problem: {v}") } },
                "--section" => o with { Section = Next() },
                "--ui-scale" => o with { UiScale = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--freeze-motion" => o with { FreezeMotion = true },
                "--hide-measured-text" => o with { HideMeasuredText = true },
                "--bench" => o with { Bench = true },
                "--selftest" => o with { SelfTest = Next() },
                "--reveal-hold" => o with { RevealHold = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--alt-pattern" => o with { AltPattern = true },
                "--open-dialog" => o with { OpenDialog = true },
                "--simulate-high-contrast" => o with { SimulateHighContrast = Next() is "aquatic" or "desert" ? args[i] : throw new ArgumentException("aquatic 또는 desert") },
                "--simulate-transparency-off" => o with { SimulateTransparencyOff = true },
                "--simulate-animations-off" => o with { SimulateAnimationsOff = true },
                "--simulate-energy-saver" => o with { SimulateEnergySaver = true },
                "--simulate-low-performance" => o with { SimulateLowPerformance = true },
                _ => throw new ArgumentException($"알 수 없는 인수입니다: {args[i]}"),
            };
        }
        if (o.UiScale is < 1 or > 3) throw new ArgumentException("--ui-scale은 1~3");
        if (o.UserData.Length == 0)
        {
            // 데모 기록이 실제 기록 목록에 섞이지 않게 데모는 따로 (M5 설계 3.4)
            var baseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse");
            o = o with { UserData = o.Demo is null ? baseFolder : Path.Combine(baseFolder, "demo") };
        }
        return o;
    }

    public static readonly IReadOnlySet<string> ScreenIds = new HashSet<string> { "home", "measure", "firstrun", "profile", "settings", "diagnose", "records", "report" };

    private static (long, double) ParseSpeedAfter(string v)
    {
        var parts = v.Split(':');
        if (parts.Length != 2) throw new ArgumentException("--demo-speed-after는 회차:배속 형식입니다 (예: 42600:1)");
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
