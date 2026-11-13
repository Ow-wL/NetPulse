using NetPulse.Core.Records;

namespace NetPulse.Core.Measurement;

/// <summary>절전·복귀 알림 (M3 설계 7절). 실제 구현은 Platform, 시험은 손으로 부른다.</summary>
public interface IPowerEvents
{
    /// <summary>구독한다. 알림은 아무 스레드에서나 올 수 있다. Dispose하면 끊긴다.</summary>
    IDisposable Subscribe(Action onSuspend, Action onResume);
}

/// <summary>회차 하나를 공백 규칙으로 본 결과.</summary>
/// <param name="Gap">절전 경계에 걸쳤거나 절전 중에 시작한 회차 (관측 없음)</param>
/// <param name="Skipped">기록할 건너뜀 수. 멈춤(stall)이나 절전으로 빠진 회차는 공백으로만 세므로 0</param>
/// <param name="Closed">이 회차로 경계가 정해진 공백들 (기록할 이벤트)</param>
public sealed record GapVerdict(bool Gap, long Skipped, IReadOnlyList<GapSpan> Closed);

/// <summary>
/// 공백·시각 변경 판정 (M3 설계 7절). 절전 알림은 아무 스레드에서나 받고(경과 시간·벽시계를 함께 적음),
/// 회차는 처리 작업이 순서대로 넣는다. 판정은 도착 순서가 아니라 경과 시간으로 한다 (처리가 밀려도 같은 결과).
/// - stall: 한 번에 건너뛴 회차가 10회 이상. 그 회차들은 공백으로만 센다.
/// - suspend: 절전 알림부터 복귀 알림까지. 경계에 걸친 회차와 절전 중 시작한 회차는 gap.
/// - clock: 벽시계 − 경과 시간이 5초 넘게 바뀌면 알린다 (절전이 끼면 기준만 다시 잡는다).
/// </summary>
public sealed class GapTracker(TimeSpan interval)
{
    public const int StallThreshold = 10;
    public static readonly TimeSpan ClockThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResumeWallSlack = TimeSpan.FromSeconds(2);

    /// <summary>마지막 <see cref="CheckClock"/> 호출 직전 표본의 경과 ms (시각 변경 이벤트의 prevAtMs, 불확실 구간 시작). 처리 작업에서만 읽는다.</summary>
    public long? PreviousSampleMs { get; private set; }

    private sealed class Suspend
    {
        public long FromMs;
        public DateTimeOffset FromWall;
        public long? ToMs;
        public DateTimeOffset? ToWall;
        public long? LastRoundBefore;
        public bool Emitted;
        public bool ClockRebased;   // 복귀 뒤 첫 표본에서 시각 기준을 다시 잡았는지
    }

    private readonly object gate = new();
    private readonly List<Suspend> suspends = [];
    private long? lastRound;
    private long lastStartMs = -1;
    private TimeSpan? clockBaseline;
    private long lastClockMs = long.MinValue;

    /// <summary>지금 절전 중(복귀 알림 전)인지.</summary>
    public bool Suspended { get { lock (gate) return suspends.Count > 0 && suspends[^1].ToMs is null; } }

    public void OnSuspend(TimeSpan elapsed, DateTimeOffset wall)
    {
        lock (gate)
        {
            if (suspends.Count > 0 && suspends[^1].ToMs is null) return;   // 이미 절전 중
            long from = Ms(elapsed);
            // 이미 처리한 회차 중 마지막 것이 직전 회차 후보 (뒤에 오는 회차가 더 늦게 시작했으면 OnRound가 고친다)
            suspends.Add(new Suspend { FromMs = from, FromWall = wall, LastRoundBefore = lastRound is not null && lastStartMs <= from ? lastRound : null });
        }
    }

    public void OnResume(TimeSpan elapsed, DateTimeOffset wall)
    {
        lock (gate)
        {
            if (suspends.Count == 0 || suspends[^1].ToMs is not null) return;   // 절전 알림 없이 온 복귀는 무시
            var s = suspends[^1];
            s.ToMs = Math.Max(s.FromMs, Ms(elapsed));
            s.ToWall = wall;
        }
    }

    /// <summary>회차를 순서대로 넣는다 (처리 작업 하나에서만).</summary>
    public GapVerdict OnRound(long round, long skippedBefore, long startMs, long endMs, DateTimeOffset wall)
    {
        var closed = new List<GapSpan>();
        bool gap;
        long skipped = skippedBefore;
        lock (gate)
        {
            // 절전 경계에 걸쳤거나(시작 < 절전 ≤ 끝) 절전 중에 시작한 회차
            gap = suspends.Any(s => (startMs < s.FromMs && s.FromMs <= endMs) || (s.FromMs <= startMs && (s.ToMs is null || startMs < s.ToMs)));

            // 직전 회차와 이 회차 사이에 절전이 있었으면 빠진 회차는 그 공백에 속한다
            bool suspendBetween = suspends.Any(s => s.FromMs <= startMs && (s.ToMs ?? long.MaxValue) >= lastStartMs);
            if (skippedBefore > 0 && suspendBetween) skipped = 0;
            else if (skippedBefore >= StallThreshold)
            {
                long fromMs = Ms(interval * (round - skippedBefore));
                long toMs = Ms(interval * round);
                closed.Add(new GapSpan(GapReason.Stall, fromMs, toMs, wall - TimeSpan.FromMilliseconds(startMs - fromMs), wall - TimeSpan.FromMilliseconds(startMs - toMs),
                    lastRound, round));
                skipped = 0;
            }

            // 복귀 뒤 첫 회차가 오면 그 절전 공백의 경계가 정해진다
            foreach (var s in suspends.Where(s => !s.Emitted))
            {
                if (!gap && startMs < s.FromMs) s.LastRoundBefore = round;
                if (!gap && s.ToMs is long to && startMs >= to)
                {
                    closed.Add(new GapSpan(GapReason.Suspend, s.FromMs, to, s.FromWall, s.ToWall!.Value, s.LastRoundBefore, round));
                    s.Emitted = true;
                }
            }
            lastRound = round;
            lastStartMs = startMs;
        }
        closed.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        return new GapVerdict(gap, skipped, closed);
    }

    /// <summary>측정 끝: 아직 닫히지 않은 절전 공백을 측정 끝에서 닫는다 (복귀가 계획 시간 뒤였거나 복귀 없이 끝남).</summary>
    public IReadOnlyList<GapSpan> Close(long endMs, DateTimeOffset endWall)
    {
        lock (gate)
        {
            var closed = new List<GapSpan>();
            foreach (var s in suspends.Where(s => !s.Emitted))
            {
                long to = Math.Max(s.FromMs, Math.Min(s.ToMs ?? endMs, endMs));
                var toWall = s.ToMs is long t && t <= endMs ? s.ToWall!.Value : endWall;
                closed.Add(new GapSpan(GapReason.Suspend, s.FromMs, to, s.FromWall, toWall, s.LastRoundBefore, null));
                s.Emitted = true;
            }
            return closed;
        }
    }

    /// <summary>
    /// 벽시계 변경 확인. 바뀐 크기(ms)를 돌려주고 기준을 새로 잡는다. 바뀌지 않았으면 null.
    /// 직전 표본과 이 표본 사이에 절전이 시작됐거나 이 표본이 절전 중이면, 차이가 절전 때문일 수 있으니 기준만 다시 잡는다
    /// (공백으로 이미 기록됨). 판단은 처리 시점이 아니라 표본의 경과 시간으로 한다: 절전 전에 시작해 복귀 뒤에 처리된 회차가
    /// 기준 재설정을 먼저 써 버리지 않게.
    /// </summary>
    public long? CheckClock(TimeSpan elapsed, DateTimeOffset wall)
    {
        lock (gate)
        {
            long now = Ms(elapsed);
            PreviousSampleMs = lastClockMs == long.MinValue ? null : lastClockMs;
            var offset = wall.UtcDateTime - DateTime.UnixEpoch - elapsed;
            // 직전 표본과 이 표본 사이에 절전 시작 또는 복귀가 있었거나, 이 표본이 절전 중이면 기준만 다시 잡는다
            // 복귀 경계는 "아직 반영하지 않은 복귀"로 따로 기억한다: 복귀 시각이 직전 표본과 같아도(단조 시계 정지, 밀리초 절삭)
            // 복귀 뒤 첫 표본에서 반드시 기준을 다시 잡는다 (M3 G4 3라운드 이월 1)
            bool crossed = false;
            foreach (var s in suspends)
            {
                if (s.FromMs >= lastClockMs && s.FromMs <= now) crossed = true;
                if (s.FromMs <= now && (s.ToMs is null || now < s.ToMs)) crossed = true;
                // 경과 시간이 복귀와 같으면 표본 벽시계로 가른다: 복귀 알림 벽시계보다 2초 넘게 이른 표본은 복귀 전에 잡힌 것 (M4 설계 2-7)
                if (s.ToMs is long to && !s.ClockRebased && (now > to || (now == to && wall >= s.ToWall!.Value - ResumeWallSlack)))
                {
                    s.ClockRebased = true;
                    crossed = true;
                }
            }
            lastClockMs = now;
            if (clockBaseline is null || crossed)
            {
                clockBaseline = offset;
                return null;
            }
            var delta = offset - clockBaseline.Value;
            if (delta.Duration() <= ClockThreshold) return null;
            clockBaseline = offset;
            return (long)delta.TotalMilliseconds;
        }
    }

    private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;
}
