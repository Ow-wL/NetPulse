using System.Diagnostics;
using Microsoft.UI.Xaml.Media;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>
/// 구간 하나의 프레임 간격 통계 (ms). M12 설계 G2: 구간을 잰 모니터의 주사율(표시 설정 값)과 출처, 그 주사율의 "긴 간격"
/// (dt &gt; max(33, 2.5T)) 횟수, 관측용 추정 놓침(dt &gt; 1.5T) 수를 함께 남긴다. 기존 <see cref="Over33"/>도 관측으로 남는다.
/// 주사율을 읽지 못했거나 구간 중에 바뀌었으면 <see cref="Hz"/>가 null이거나 <see cref="RefreshChanged"/>가 참이고, 시험은 실패로 본다.
/// </summary>
public sealed record FrameStats(string Segment, int Samples, double Seconds, double P50, double P95, double Max, int Over33,
    double? Hz = null, string? RefreshSource = null, bool RefreshChanged = false, double? LongGapMs = null, int LongGaps = 0, int Missed = 0)
{
    public static FrameStats From(string segment, IReadOnlyList<double> intervals, double seconds, RefreshInfo? refresh = null, bool refreshChanged = false)
    {
        double? hz = refresh?.Hz;
        double? longGap = hz is { } h ? Math.Round(FrameContract.LongGap(h), 2) : null;
        double? missed = hz is { } m ? FrameContract.Missed(m) : null;
        if (intervals.Count == 0) return new FrameStats(segment, 0, seconds, 0, 0, 0, 0, hz, refresh?.Source, refreshChanged, longGap);
        var s = intervals.OrderBy(x => x).ToArray();
        double Pct(double p) => s[Math.Min(s.Length - 1, (int)(s.Length * p))];
        return new FrameStats(segment, s.Length, Math.Round(seconds, 2), Math.Round(Pct(0.5), 2), Math.Round(Pct(0.95), 2), Math.Round(s[^1], 2), s.Count(x => x > FrameBudget.Hitch),
            hz, refresh?.Source, refreshChanged, longGap,
            longGap is { } lg ? s.Count(x => x > FrameContract.LongGap(hz!.Value)) : 0,
            missed is { } mi ? s.Count(x => x > mi) : 0);
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
    private readonly Func<RefreshInfo> refresh;
    private double last = -1;
    private List<double>? segment;
    private double segmentStart;
    // 구간의 직전 시각 (M12 설계 G2: 첫 간격은 구간 시작부터 잰다. 전에는 구간 시작 전 콜백부터 재 앞 시간이 섞였다)
    private double segmentLast;
    private RefreshInfo? segmentRefresh;
    private readonly List<double> window = [];
    private double windowStart;
    private int overWindows;
    private double goodSeconds;          // 유효한 창(전환·크기 조절 밖)에서만 쌓은 양호 시간
    private double suspendedUntil = double.NegativeInfinity;
    private double lastSwitch = double.NegativeInfinity;
    private bool low;

    public FramePacer(Func<bool> paused, Action<bool> setLow, Func<RefreshInfo>? refresh = null)
    {
        this.paused = paused;
        this.setLow = setLow;
        this.refresh = refresh ?? (() => new RefreshInfo(null, "not-queried", null));
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>기준 간격 T (ms). 처음 2초가 지나기 전에는 null.</summary>
    public double? Baseline { get; private set; }

    public bool Low => low;

    public void BeginSegment()
    {
        segmentRefresh = refresh();
        segment = [];
        segmentStart = clock.Elapsed.TotalSeconds;
        segmentLast = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 구간을 끝낸다. 마지막 콜백부터 끝까지의 시간도 한 표본으로 넣는다 (끝에서 멈춘 것이 빠지지 않게, M12 설계 G2).
    /// 끝에서 주사율을 다시 읽어 시작과 다르면 표시한다.
    /// </summary>
    public FrameStats EndSegment(string name)
    {
        var s = segment ?? [];
        segment = null;
        double now = clock.Elapsed.TotalMilliseconds;
        if (now > segmentLast) s.Add(now - segmentLast);
        var end = refresh();
        bool changed = segmentRefresh?.Hz != end.Hz || segmentRefresh?.Monitor != end.Monitor;
        return FrameStats.From(name, s, clock.Elapsed.TotalSeconds - segmentStart, segmentRefresh, changed);
    }

    /// <summary>지금 창의 모니터 주사율 (앱 기록·고지 bench용).</summary>
    public RefreshInfo Refresh() => refresh();

    /// <summary>지금 구간에 쌓인 표본 수와, 그 위치부터의 최대 간격 (bench가 단계별로 어디서 튀는지 본다).</summary>
    public int SegmentSamples => segment?.Count ?? 0;
    public double SegmentMaxFrom(int index) => segment is { } s && index < s.Count ? s.Skip(index).Max() : 0;

    public void Detach() => CompositionTarget.Rendering -= OnRendering;

    private bool detachedForHide;

    /// <summary>창 숨김 (M9 설계 A1): 렌더링 구독을 끊는다. 다시 열면 첫 간격은 버린다.</summary>
    public void Pause()
    {
        if (detachedForHide) return;
        detachedForHide = true;
        CompositionTarget.Rendering -= OnRendering;
    }

    public void Resume()
    {
        if (!detachedForHide) return;
        detachedForHide = false;
        last = -1;
        CompositionTarget.Rendering += OnRendering;
    }

    public bool Paused => detachedForHide;

    /// <summary>창 크기 조절처럼 표본을 넣지 않을 구간 (G4 M2 1라운드 지적 9).</summary>
    public void Suspend(TimeSpan span) => suspendedUntil = Math.Max(suspendedUntil, clock.Elapsed.TotalMilliseconds + span.TotalMilliseconds);

    private void OnRendering(object? sender, object e)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (segment is not null) { segment.Add(now - segmentLast); segmentLast = now; }   // 구간은 시작 시각부터 (다시 열린 뒤 첫 콜백 포함)
        if (last >= 0)
        {
            double dt = now - last;
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
