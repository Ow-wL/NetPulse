namespace NetPulse.Design.Tokens;

/// <summary>3차 베지어 곡선의 두 제어점.</summary>
public readonly record struct Easing(double X1, double Y1, double X2, double Y2);

/// <summary>모션 시간과 곡선 (기획서 7.4, M2 설계 4.4). 모든 애니메이션은 이 값만 쓴다.</summary>
public static class MotionTokens
{
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(167);
    public static readonly TimeSpan Mid = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(333);
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(83);
    public static readonly TimeSpan ThemeReveal = TimeSpan.FromMilliseconds(550);
    public static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan StateShift = TimeSpan.FromMilliseconds(1200);
    public static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SnapshotLimit = TimeSpan.FromMilliseconds(200);
    /// <summary>끊긴 구간 깜빡임 횟수</summary>
    public const int BlinkTimes = 3;

    /// <summary>요소 등장, 퇴장</summary>
    public static readonly Easing Enter = new(0, 0, 0, 1);
    /// <summary>있던 요소의 이동</summary>
    public static readonly Easing Move = new(0.55, 0.55, 0, 1);

    /// <summary>누름 가라앉음 비율, 등장 시 올라오는 거리(px)</summary>
    public const double PressScale = 0.97, RiseDistance = 12;

    /// <summary>배경 빛이 한 번 오가는 시간 (느린 흐름)</summary>
    public static readonly TimeSpan[] LightDrift = [TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(27), TimeSpan.FromSeconds(31)];
}

/// <summary>프레임 기준 (M2 설계 5.2 저성능 대체, 5.9 벤치). 이 PC 한 대의 기준이다.</summary>
public static class FrameBudget
{
    /// <summary>"멈칫"으로 세는 간격 (ms)</summary>
    public const double Hitch = 33;
    /// <summary>평상시 95백분위 상한, 전환 중 최대 간격 상한 (ms)</summary>
    public const double IdleP95 = 16.7, TransitionMax = 50;
    /// <summary>전환 1회당 멈칫 허용 횟수</summary>
    public const int TransitionHitches = 2;
    /// <summary>측정 무효 기준: 구간 1초당 최소 표본 수</summary>
    public const double MinSamplesPerSecond = 30;
    /// <summary>저성능 판정 창, 진입 하한·배수, 연속 창 수, 복구 배수·시간, 왕복 최소 간격</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);
    public const double DegradeFloor = 25, DegradeFactor = 1.5, RecoverFactor = 1.25;
    public const int DegradeWindows = 2;
    public static readonly TimeSpan RecoverAfter = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinSwitchGap = TimeSpan.FromMinutes(1);
    /// <summary>벤치 절차 (설계 5.9): 정지 구간, 전환 뒤 정리 여유, 전환 사이 간격, 전환 한 번의 최대 대기</summary>
    public static readonly TimeSpan BenchIdle = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BenchSettle = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan BenchGap = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan BenchSwitchTimeout = TimeSpan.FromSeconds(5);
    /// <summary>전환 20회→60회 사이 전용 메모리 증가 상한, 60회 뒤 전용 메모리 상한 (MB). 상한은 M0 스파이크 합격선 400 MB</summary>
    public const double PlateauGrowthMb = 20, PrivateLimitMb = 400;
}

/// <summary>기다림 기준 (M5): 이보다 오래 걸리면 화면이 "오래 걸린다"를 알리고 다시 시도할 수 있게 한다.</summary>
public static class Waits
{
    /// <summary>첫 실행 탐지 (M5 설계 8절: 5초 뒤 "다시 확인")</summary>
    public static readonly TimeSpan Detect = TimeSpan.FromSeconds(5);
    /// <summary>창을 닫을 때 진행 중인 테마 전환의 정리를 기다리는 한도 (M2 이월 1)</summary>
    public static readonly TimeSpan ThemeCleanup = TimeSpan.FromSeconds(2);
    /// <summary>창을 닫을 때 메모·노트 쓰기를 기다리는 한도 (M5 설계 4.5)</summary>
    public static readonly TimeSpan CloseWrites = TimeSpan.FromSeconds(3);
    /// <summary>시험 주입 <c>--report-fail slow</c>: 내보내기를 이만큼 늦춘다 (M8 "내보내는 중" 장면)</summary>
    public static readonly TimeSpan ReportSlowTest = TimeSpan.FromSeconds(4);
}

/// <summary>측정 화면 bench 구간 (M5 설계 5절 재현 절차).</summary>
public static class BenchTokens
{
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan IdleLive = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan ZoomFull = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PanStep = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan NavStep = TimeSpan.FromMilliseconds(1500);
    public const int PanCount = 10, NavCount = 6;

    // 진단 구간 (M6 설계 5절): 첫 진입 뒤 머무름, 진단 한 번의 제한, 결과 뒤 머무름, 홈 ↔ 진단 6번
    public static readonly TimeSpan DiagEnter = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DiagRunLimit = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DiagSettle = TimeSpan.FromSeconds(2);
    public const int DiagReenterCount = 6;

    // 기록 화면 구간 (M7 설계 5절): 첫 진입 뒤 머무름, 상세·비교 열기 제한, 열린 뒤 머무름, 재진입 횟수, 메모리 표본 간격
    public static readonly TimeSpan RecordsEnter = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RecordsOpenLimit = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RecordsSettle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MemorySample = TimeSpan.FromMilliseconds(500);
    public const int RecordsReenterCount = 6;
}
