namespace NetPulse.Design.Tokens;

/// <summary>성능 프로필 (M12 설계 G2). standard = 지금 기준(데스크톱), low = 노트북 최종 게이트. 게이트 인자로만 고른다 (하드웨어 자동 감지 없음).</summary>
public enum PerfProfile { Standard, Low }

/// <summary>한 프로필·주사율에서 실제로 적용하는 상한 (ms·횟수). 결과에 그대로 기록한다.</summary>
public sealed record FrameLimits(
    PerfProfile Profile, double Hz, double IntervalMs,
    double IdleP95, double LongGapMs,
    double TransitionMax, int TransitionLongGaps,
    double WorstOpenMax, int WorstOpenLongGaps,
    double WorstScrollMax, int WorstScrollLongGaps,
    double NoticeFirstLayout, double NoticeMax, int NoticeLongGaps,
    string Version);

/// <summary>
/// 프레임 기준 계약 (M12 설계 G2, DECISIONS "M12 노트북 성능 기준"). 재는 것은 렌더링 콜백 간격이고 실제 표시 프레임이 아니다.
/// 주사 간격 T는 앱 창이 있는 모니터의 표시 설정 값에서 온다 (앱이 잰 콜백 간격으로 기준을 늘리지 않는다).
/// <list type="bullet">
/// <item>공통 보정: idle 95백분위 ≤ max(16.7, 1.25T), 긴 간격 = dt &gt; max(33, 2.5T). 1.25·2.5는 공학적 여유 계수다.</item>
/// <item>standard: 전환·조작 50 ms·2회, 최악 열기 80 ms·6회, 최악 스크롤 80 ms·2회, 고지 첫 배치 50 ms·펼친 뒤 최대 50 ms·2회.</item>
/// <item>low: 전환·조작 100 ms·2회, 최악 열기 100 ms·6회, 최악 스크롤 80 ms·2회(늘리지 않음), 고지 첫 배치 100 ms·펼친 뒤 최대 100 ms·2회.
/// 100 ms는 저사양에서 한 번 멈춤을 어디까지 허용할지 정한 정책 상한이다 (체감 한계의 근거가 아님).</item>
/// </list>
/// </summary>
public static class FrameContract
{
    /// <summary>계약 버전 (수식·상한을 바꾸면 올린다)</summary>
    public const string Version = "m12-g2-1";
    public const string ProfileVariable = "NETPULSE_PERF_PROFILE";

    public static double IntervalMs(double hz) => hz > 0 ? 1000.0 / hz : throw new ArgumentOutOfRangeException(nameof(hz), "주사율은 0보다 커야 합니다");

    /// <summary>idle 95백분위 상한: max(16.7, 1.25T)</summary>
    public static double IdleP95(double hz) => Math.Max(FrameBudget.IdleP95, 1.25 * IntervalMs(hz));

    /// <summary>"긴 간격" 판정: dt &gt; max(33, 2.5T). 60 Hz 41.7, 165 Hz 33.</summary>
    public static double LongGap(double hz) => Math.Max(FrameBudget.Hitch, 2.5 * IntervalMs(hz));

    /// <summary>관측용: 한 주사 기회 이상 놓친 것으로 추정하는 간격 (dt &gt; 1.5T). 판정에 쓰지 않는다.</summary>
    public static double Missed(double hz) => 1.5 * IntervalMs(hz);

    /// <summary>프로필 이름 (비어 있으면 standard). 모르는 이름은 거부한다.</summary>
    public static PerfProfile Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "standard" => PerfProfile.Standard,
        "low" => PerfProfile.Low,
        var other => throw new ArgumentException($"성능 프로필은 standard 또는 low입니다: {other}"),
    };

    public static FrameLimits For(PerfProfile profile, double hz)
    {
        double t = IntervalMs(hz);
        bool low = profile == PerfProfile.Low;
        double interact = low ? 100 : FrameBudget.TransitionMax;
        return new FrameLimits(profile, hz, Math.Round(t, 3), Math.Round(IdleP95(hz), 2), Math.Round(LongGap(hz), 2),
            interact, FrameBudget.TransitionHitches,
            low ? 100 : 80, 6,
            80, FrameBudget.TransitionHitches,
            interact, interact, FrameBudget.TransitionHitches,
            Version);
    }
}
