using System.Diagnostics;

namespace NetPulse.Core.Timing;

/// <summary>
/// 측정 엔진이 쓰는 시계. 경과 시간(스케줄용)과 표시 시각(기록·화면용)을 따로 낸다.
/// 실제 시계, 배속 시계(데모), 손으로 돌리는 시계(테스트)가 같은 계약을 따른다.
/// </summary>
public interface IClock
{
    /// <summary>엔진 시작부터 흐른 시간. 회차 예정 시각의 기준.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>사람에게 보여주고 기록할 시각.</summary>
    DateTimeOffset Now { get; }

    /// <summary>경과 시간 기준으로 기다린다. 일찍 깰 수 있으므로 호출한 쪽이 다시 확인해야 한다.</summary>
    Task Delay(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>실제 시계. 경과 시간은 단조 증가하는 Stopwatch, 표시 시각은 시스템 시각.</summary>
public sealed class SystemClock : IClock
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => stopwatch.Elapsed;

    public DateTimeOffset Now => DateTimeOffset.Now;

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(duration, cancellationToken);
}

/// <summary>
/// 배속 시계 (데모). 경과 시간이 <see cref="Speed"/>배로 흐르고, 표시 시각은 시작 시각에 경과 시간을 더한 값이다.
/// </summary>
public sealed class ScaledClock : IClock
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly DateTimeOffset startAt;

    public ScaledClock(double speed, DateTimeOffset startAt)
    {
        if (!(speed > 0) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed), "배속은 0보다 커야 합니다.");
        Speed = speed;
        this.startAt = startAt;
    }

    public double Speed { get; }

    public TimeSpan Elapsed => TimeSpan.FromTicks((long)(stopwatch.Elapsed.Ticks * Speed));

    public DateTimeOffset Now => startAt + Elapsed;

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromTicks(Math.Max(1, (long)(duration.Ticks / Speed))), cancellationToken);
}

/// <summary>
/// 테스트용 시계. <see cref="Advance"/>로만 시간이 흐른다. 기다리는 작업은 경과 시간이 목표에 닿으면 끝난다.
/// <see cref="EarlyWake"/>를 켜면 기다림을 목표보다 일찍 끝내 "타이머가 일찍 깨는" 경우를 재현한다.
/// </summary>
public sealed class ManualClock : IClock
{
    private readonly object gate = new();
    private readonly List<(TimeSpan Due, TaskCompletionSource Source)> waiters = [];
    private TimeSpan elapsed;

    public ManualClock(DateTimeOffset startAt) => StartAt = startAt;

    public DateTimeOffset StartAt { get; }

    /// <summary>켜면 다음 Advance에서 기다림이 목표보다 이 값만큼 일찍 끝난다.</summary>
    public TimeSpan EarlyWake { get; set; }

    public TimeSpan Elapsed { get { lock (gate) return elapsed; } }

    public DateTimeOffset Now => StartAt + Elapsed;

    /// <summary>지금 기다리는 작업 수 (테스트에서 엔진이 다음 회차를 기다리기 시작했는지 확인할 때 쓴다).</summary>
    public int WaiterCount { get { lock (gate) return waiters.Count; } }

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero) return Task.CompletedTask;
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) waiters.Add((elapsed + duration, source));
        cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        return source.Task;
    }

    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource> ready;
        lock (gate)
        {
            elapsed += by;
            var limit = elapsed + EarlyWake;
            ready = waiters.Where(w => w.Due <= limit).Select(w => w.Source).ToList();
            waiters.RemoveAll(w => w.Due <= limit);
        }
        foreach (var source in ready) source.TrySetResult();
    }
}
