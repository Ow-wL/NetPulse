using System.Diagnostics;
using Microsoft.UI.Xaml.Media;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>구간 하나의 프레임 간격 통계 (ms).</summary>
public sealed record FrameStats(string Segment, int Samples, double Seconds, double P50, double P95, double Max, int Over33)
{
    public static FrameStats From(string segment, IReadOnlyList<double> intervals, double seconds)
    {
        if (intervals.Count == 0) return new FrameStats(segment, 0, seconds, 0, 0, 0, 0);
        var s = intervals.OrderBy(x => x).ToArray();
        double Pct(double p) => s[Math.Min(s.Length - 1, (int)(s.Length * p))];
        return new FrameStats(segment, s.Length, Math.Round(seconds, 2), Math.Round(Pct(0.5), 2), Math.Round(Pct(0.95), 2), Math.Round(s[^1], 2), s.Count(x => x > FrameBudget.Hitch));
    }
}

/// <summary>
/// 프레임 간격 (M2 설계 5.2, 5.9). CompositionTarget.Rendering 콜백 사이 시간을 잰다. 실제 표시 프레임과 같지 않을 수 있다.
/// - 구간 측정 (벤치)
/// - 저성능 대체: 시작 후 처음 정지 2초의 중앙값을 기준 간격 T로 두고, 2초 창의 95백분위가 max(25 ms, 1.5T)를 넘는 창이
///   2번 연속이면 낮춘다. 1.25T 이하가 10초 이어지면 되돌린다. 1분에 한 번 넘게 오가지 않는다. 전환 중 표본은 넣지 않는다.
/// </summary>
public sealed class FramePacer
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Func<bool> paused;
    private readonly Action<bool> setLow;
    private double last = -1;
    private List<double>? segment;
    private double segmentStart;
    private readonly List<double> window = [];
    private double windowStart;
    private int overWindows;
    private double goodSeconds;          // 유효한 창(전환·크기 조절 밖)에서만 쌓은 양호 시간
    private double suspendedUntil = double.NegativeInfinity;
    private double lastSwitch = double.NegativeInfinity;
    private bool low;

    public FramePacer(Func<bool> paused, Action<bool> setLow)
    {
        this.paused = paused;
        this.setLow = setLow;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>기준 간격 T (ms). 처음 2초가 지나기 전에는 null.</summary>
    public double? Baseline { get; private set; }

    public bool Low => low;

    public void BeginSegment()
    {
        segment = [];
        segmentStart = clock.Elapsed.TotalSeconds;
    }

    public FrameStats EndSegment(string name)
    {
        var s = segment ?? [];
        segment = null;
        return FrameStats.From(name, s, clock.Elapsed.TotalSeconds - segmentStart);
    }

    public void Detach() => CompositionTarget.Rendering -= OnRendering;

    /// <summary>창 크기 조절처럼 표본을 넣지 않을 구간 (G4 M2 1라운드 지적 9).</summary>
    public void Suspend(TimeSpan span) => suspendedUntil = Math.Max(suspendedUntil, clock.Elapsed.TotalMilliseconds + span.TotalMilliseconds);

    private void OnRendering(object? sender, object e)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (last >= 0)
        {
            double dt = now - last;
            segment?.Add(dt);
            if (!paused() && now >= suspendedUntil) Track(now, dt);
            else { window.Clear(); windowStart = now; }
        }
        last = now;
    }

    private void Track(double now, double dt)
    {
        if (window.Count == 0) windowStart = now;
        window.Add(dt);
        if (now - windowStart < FrameBudget.Window.TotalMilliseconds) return;
        var s = window.OrderBy(x => x).ToArray();
        double p50 = s[s.Length / 2], p95 = s[Math.Min(s.Length - 1, (int)(s.Length * 0.95))];
        double windowSeconds = (now - windowStart) / 1000;
        window.Clear();
        if (Baseline is null) { Baseline = p50; return; }
        double t = Baseline.Value;
        double secondsNow = now / 1000;
        if (!low)
        {
            overWindows = p95 > Math.Max(FrameBudget.DegradeFloor, FrameBudget.DegradeFactor * t) ? overWindows + 1 : 0;
            if (overWindows >= FrameBudget.DegradeWindows && secondsNow - lastSwitch >= FrameBudget.MinSwitchGap.TotalSeconds)
            {
                low = true; lastSwitch = secondsNow; overWindows = 0; goodSeconds = 0;
                setLow(true);
            }
        }
        else
        {
            // 양호한 시간은 유효한 창의 길이만 더한다 (전환·크기 조절 구간은 창이 비워져 더해지지 않는다)
            goodSeconds = p95 <= FrameBudget.RecoverFactor * t ? goodSeconds + windowSeconds : 0;
            if (goodSeconds >= FrameBudget.RecoverAfter.TotalSeconds && secondsNow - lastSwitch >= FrameBudget.MinSwitchGap.TotalSeconds)
            {
                low = false; lastSwitch = secondsNow; goodSeconds = 0;
                setLow(false);
            }
        }
    }
}
