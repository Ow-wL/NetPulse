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

    /// <summary>요소 등장, 퇴장</summary>
    public static readonly Easing Enter = new(0, 0, 0, 1);
    /// <summary>있던 요소의 이동</summary>
    public static readonly Easing Move = new(0.55, 0.55, 0, 1);

    /// <summary>누름 가라앉음 비율, 등장 시 올라오는 거리(px)</summary>
    public const double PressScale = 0.97, RiseDistance = 12;

    /// <summary>배경 빛이 한 번 오가는 시간 (느린 흐름)</summary>
    public static readonly TimeSpan[] LightDrift = [TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(27), TimeSpan.FromSeconds(31)];
}
