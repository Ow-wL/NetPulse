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
/// - 화면 스레드와 무관하게 돈다. 구독자에게는 배달 작업 하나에서 차례로 알린다 (화면이 자기 스레드로 넘긴다).
/// - 화면이 구독하기 전에 도착한 결과는 상태로 보관하고, 구독하면 곧 최신 상태를 한 번 준다.
/// - 구독자마다 받은 상태의 번호를 기억해, 더 오래된 상태를 뒤늦게 주지 않는다. 중간 상태는 건너뛸 수 있다.
/// - 상태 잠금 안에서 구독자를 부르지 않는다. 구독자가 멈춰도 측정과 StopAsync는 막히지 않는다.
/// - 멈춘 뒤에는 도착하는 결과를 버린다. 예외를 던지지 않는다.
/// </summary>
public sealed class MeasurementEngine : IAsyncDisposable
{
    private readonly IClock clock;
    private readonly IProbe probe;
    private readonly IReadOnlyList<ProbeTarget> targets;
    private readonly RoundScheduler scheduler;
    private readonly object gate = new();
    private readonly List<Subscription> subscribers = [];
    private CancellationTokenSource? cancellation;
    private Task? loop;
    private EngineState state = EngineState.Initial;
    private long version;          // state가 바뀔 때마다 1씩 오른다 (gate 안)
    private int deliveryPending;   // 배달할 것이 있음 (0/1)
    private int delivering;        // 배달 작업이 돌고 있음 (0/1)
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
    /// 상태 변화를 구독한다. 곧 최신 상태를 한 번 알린다 (배달 작업에서, 다른 알림과 순서를 지켜).
    /// 돌려받은 객체를 Dispose하면 구독이 끊긴다.
    /// </summary>
    public IDisposable Subscribe(Action<EngineState> onState)
    {
        var subscription = new Subscription(this, onState);
        lock (gate) subscribers.Add(subscription);
        RequestDelivery();
        return subscription;
    }

    public void Start()
    {
        lock (gate)
        {
            if (loop != null || stopped) throw new InvalidOperationException("엔진은 한 번만 시작할 수 있습니다.");
            cancellation = new CancellationTokenSource();
            SetState(state with { Running = true });
            var token = cancellation.Token;
            loop = Task.Run(() => RunAsync(token));
        }
        RequestDelivery();
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
        lock (gate) SetState(state with { Running = false });
        RequestDelivery();
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
                    SetState(state with
                    {
                        Rounds = state.Rounds + 1,
                        Skipped = state.Skipped + tick.SkippedBefore,
                        MaxLate = tick.Late > state.MaxLate ? tick.Late : state.MaxLate,
                        Latest = snapshot,
                    });
                }
                RequestDelivery();
            }
            // 상한을 넘기며 끝났으면 내지 못한 나머지 회차도 건너뜀으로 센다 (회차 + 건너뜀 = 상한)
            lock (gate)
            {
                if (stopped) return;
                SetState(state with { Running = false, Finished = true, Skipped = state.Skipped + scheduler.TrailingSkipped });
            }
            RequestDelivery();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<ProbeResult> SafeProbe(ProbeTarget target, long round, CancellationToken token)
    {
        try { return await probe.ProbeAsync(target, round, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ProbeResult.Error("probe-exception:" + ex.GetType().Name); }
    }

    // gate 안에서만 부른다.
    private void SetState(EngineState next)
    {
        state = next;
        version++;
    }

    /// <summary>
    /// 배달 작업을 깨운다. 배달 작업은 한 번에 하나만 돈다 (구독자 호출이 겹치지 않는다).
    /// 배달 작업은 매번 최신 상태를 읽어, 아직 그 번호를 받지 않은 구독자에게만 준다.
    /// </summary>
    private void RequestDelivery()
    {
        Volatile.Write(ref deliveryPending, 1);
        if (Interlocked.CompareExchange(ref delivering, 1, 0) == 0) _ = Task.Run(Deliver);
    }

    private void Deliver()
    {
        while (true)
        {
            while (Interlocked.Exchange(ref deliveryPending, 0) == 1)
            {
                EngineState current;
                long currentVersion;
                Subscription[] targetsNow;
                lock (gate) { current = state; currentVersion = version; targetsNow = subscribers.ToArray(); }
                foreach (var subscription in targetsNow)
                {
                    if (!subscription.Active || subscription.Delivered >= currentVersion) continue;
                    subscription.Delivered = currentVersion;   // 배달 작업만 읽고 쓴다
                    try { subscription.OnState(current); } catch (Exception) { /* 화면 쪽 오류가 측정을 멈추지 않게 한다 */ }
                }
            }
            Volatile.Write(ref delivering, 0);
            // 내려놓는 사이에 들어온 요청이 있으면 다시 맡는다 (요청을 잃지 않는다)
            if (Volatile.Read(ref deliveryPending) == 0 || Interlocked.CompareExchange(ref delivering, 1, 0) != 0) return;
        }
    }

    private sealed class Subscription(MeasurementEngine engine, Action<EngineState> onState) : IDisposable
    {
        private volatile bool active = true;

        public Action<EngineState> OnState { get; } = onState;
        public bool Active => active;
        public long Delivered { get; set; } = -1;

        public void Dispose()
        {
            active = false;
            lock (engine.gate) engine.subscribers.Remove(this);
        }
    }
}
