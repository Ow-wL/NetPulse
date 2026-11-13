using NetPulse.Core.Timing;

namespace NetPulse.Core.Measurement;

/// <summary>회차 하나의 결과.</summary>
public sealed record RoundSnapshot(RoundTick Tick, DateTimeOffset At, IReadOnlyList<(ProbeTarget Target, ProbeResult Result)> Results);

/// <summary>엔진의 현재 상태. 화면은 이것만 구독한다.</summary>
public sealed record EngineState(bool Running, long Rounds, long Skipped, TimeSpan MaxLate, RoundSnapshot? Latest, bool Finished)
{
    public static readonly EngineState Initial = new(false, 0, 0, TimeSpan.Zero, null, false);
}

/// <summary>
/// 측정 엔진. 수명은 앱이 소유하고, 화면은 구독만 소유한다 (M1 설계 2절).
/// - 화면 스레드와 무관하게 돈다. 구독자에게는 엔진 스레드에서 알린다 (화면이 자기 스레드로 넘긴다).
/// - 화면이 구독하기 전에 도착한 결과는 상태로 보관하고, 구독하는 순간 최신 상태를 한 번 준다.
/// - 멈춘 뒤에는 도착하는 결과를 버린다. 예외를 던지지 않는다.
/// </summary>
public sealed class MeasurementEngine : IAsyncDisposable
{
    private readonly IClock clock;
    private readonly IProbe probe;
    private readonly IReadOnlyList<ProbeTarget> targets;
    private readonly RoundScheduler scheduler;
    private readonly object gate = new();
    private readonly List<Action<EngineState>> subscribers = [];
    private CancellationTokenSource? cancellation;
    private Task? loop;
    private EngineState state = EngineState.Initial;
    private bool stopped;

    public MeasurementEngine(IClock clock, IProbe probe, IReadOnlyList<ProbeTarget> targets, TimeSpan interval, long? roundLimit = null)
    {
        this.clock = clock;
        this.probe = probe;
        this.targets = targets;
        scheduler = new RoundScheduler(clock, interval, roundLimit);
    }

    public EngineState State { get { lock (gate) return state; } }

    /// <summary>
    /// 상태 변화를 구독한다. 이미 상태가 있으면 바로 한 번 알린다. 돌려받은 객체를 Dispose하면 구독이 끊긴다.
    /// </summary>
    public IDisposable Subscribe(Action<EngineState> onState)
    {
        EngineState current;
        lock (gate) { subscribers.Add(onState); current = state; }
        try { onState(current); } catch (Exception) { /* 구독자 오류가 구독이나 측정을 막지 않게 한다 */ }
        return new Unsubscriber(this, onState);
    }

    public void Start()
    {
        lock (gate)
        {
            if (loop != null || stopped) throw new InvalidOperationException("엔진은 한 번만 시작할 수 있습니다.");
            cancellation = new CancellationTokenSource();
            Publish(state with { Running = true });
            var token = cancellation.Token;
            loop = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>
    /// 멈춘다: 취소 요청 → 실행 중인 회차가 끝날 때까지 기다림(제한시간) → 이후 결과는 버림.
    /// 제한시간 안에 끝났으면 true.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Task? running;
        lock (gate)
        {
            if (stopped) return true;
            stopped = true;
            cancellation?.Cancel();
            running = loop;
        }
        bool completed = true;
        if (running != null)
        {
            var finished = await Task.WhenAny(running, Task.Delay(timeout)).ConfigureAwait(false);
            completed = finished == running;
        }
        lock (gate) Publish(state with { Running = false });
        return completed;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        cancellation?.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var tick in scheduler.RunAsync(token).ConfigureAwait(false))
            {
                var at = clock.Now;
                var probes = targets.Select(t => SafeProbe(t, tick.Index, token)).ToArray();
                var results = await Task.WhenAll(probes).ConfigureAwait(false);
                var snapshot = new RoundSnapshot(tick, at, targets.Zip(results, (t, r) => (t, r)).ToArray());
                lock (gate)
                {
                    if (stopped) return;   // 멈춘 뒤 도착한 결과는 버린다
                    Publish(state with
                    {
                        Rounds = state.Rounds + 1,
                        Skipped = state.Skipped + tick.SkippedBefore,
                        MaxLate = tick.Late > state.MaxLate ? tick.Late : state.MaxLate,
                        Latest = snapshot,
                    });
                }
            }
            lock (gate) if (!stopped) Publish(state with { Running = false, Finished = true });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<ProbeResult> SafeProbe(ProbeTarget target, long round, CancellationToken token)
    {
        try { return await probe.ProbeAsync(target, round, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ProbeResult.Error("probe-exception:" + ex.GetType().Name); }
    }

    // gate 안에서만 부른다. 구독자 예외가 엔진을 멈추지 않게 한다.
    private void Publish(EngineState next)
    {
        state = next;
        foreach (var subscriber in subscribers.ToArray())
        {
            try { subscriber(next); } catch (Exception) { /* 화면 쪽 오류가 측정을 멈추지 않게 한다 */ }
        }
    }

    private sealed class Unsubscriber(MeasurementEngine engine, Action<EngineState> onState) : IDisposable
    {
        public void Dispose() { lock (engine.gate) engine.subscribers.Remove(onState); }
    }
}
