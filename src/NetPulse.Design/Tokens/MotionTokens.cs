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
