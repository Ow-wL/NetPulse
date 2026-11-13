using NetPulse.Core.Timing;

namespace NetPulse.Core.Measurement;

/// <summary>회차 하나의 예정·실제 시각.</summary>
/// <param name="Index">0부터 시작하는 회차 번호. 경과 시간 / 주기에서 나온다.</param>
/// <param name="Scheduled">예정 경과 시간 (Index × 주기)</param>
/// <param name="Actual">실제로 시작한 경과 시간</param>
/// <param name="SkippedBefore">이 회차 직전에 건너뛴 회차 수</param>
public readonly record struct RoundTick(long Index, TimeSpan Scheduled, TimeSpan Actual, long SkippedBefore)
{
    public TimeSpan Late => Actual - Scheduled;
}

/// <summary>
/// 주기마다 회차를 낸다.
/// - 0번 회차부터 시작한다.
/// - 기다림이 예정보다 일찍 끝나면 다시 기다린다. 같은 회차를 두 번 내지 않는다 (M0 스파이크에서 찾은 버그).
/// - 늦어서 예정 시각을 넘긴 회차는 건너뛰고 그 수를 센다. 회차 번호는 항상 경과 시간에서 나온다.
/// - <paramref name="roundLimit"/>가 있으면 0 이상 그 값 미만의 회차만 낸다.
/// </summary>
public sealed class RoundScheduler(IClock clock, TimeSpan interval, long? roundLimit = null)
{
    public TimeSpan Interval { get; } = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval));

    public async IAsyncEnumerable<RoundTick> RunAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long next = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (roundLimit is long limit && next >= limit) yield break;
            var due = Interval * next;
            var now = clock.Elapsed;
            if (now < due)
            {
                try { await clock.Delay(due - now, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;   // 일찍 깼을 수 있으니 다시 확인한다
            }
            long slot = (long)(now.Ticks / Interval.Ticks);
            if (roundLimit is long cap && slot >= cap) yield break;
            long skipped = slot - next;
            next = slot + 1;
            yield return new RoundTick(slot, Interval * slot, now, skipped);
        }
    }
}
