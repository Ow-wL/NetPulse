using System.Diagnostics;
using NetPulse.Core.Detection;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;

namespace NetPulse.Core.Demo;

/// <summary>
/// 데모 시간축 (M5 설계 3.4). 앱 전체에 하나. 벽시계(<see cref="Now"/>)와 측정 시계(<see cref="DemoClock"/>)를 모두 여기서 만든다:
/// 배속 전환·멈춤·절전 건너뛰기가 두 시간에 똑같이 적용되어 둘의 차이가 바뀌지 않는다.
/// <para>
/// 배속이 <see cref="VirtualAbove"/>를 넘으면 실제 시간에 맞추지 않고 <b>처리 속도에 맞춰</b> 진행한다:
/// 기다림이 있을 때만 시간이 목표까지 건너뛰고, 그 전에 <c>ready</c>(측정 처리가 따라왔는지)를 확인한다.
/// 운영체제 타이머 해상도(약 15 ms)로는 600배속 회차(1.7 ms)를 맞출 수 없고, 앞서 가면 엔진이 회차를 건너뛰거나 처리 큐가 넘치기 때문이다.
/// </para>
/// </summary>
public sealed class DemoTimeBase
{
    public const double VirtualAbove = 50;
    private readonly object gate = new();
    private readonly Stopwatch real = Stopwatch.StartNew();
    private readonly DateTimeOffset startAt;
    private TimeSpan anchorScaled, anchorReal;
    private double speed;
    private bool held;

    public DemoTimeBase(DateTimeOffset startAt, double speed)
    {
        if (!(speed > 0) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed), "배속은 0보다 커야 합니다.");
        this.startAt = startAt;
        this.speed = speed;
    }

    public double Speed { get { lock (gate) return speed; } }
    public bool Held { get { lock (gate) return held; } }
    private bool Virtual => speed > VirtualAbove;

    /// <summary>시작부터의 배속 경과.</summary>
    public TimeSpan Scaled { get { lock (gate) return Current(); } }

    public DateTimeOffset Now => startAt + Scaled;

    private TimeSpan Current() => held || Virtual ? anchorScaled : anchorScaled + TimeSpan.FromTicks((long)((real.Elapsed - anchorReal).Ticks * speed));

    public void SetSpeed(double next)
    {
        if (!(next > 0) || double.IsInfinity(next)) throw new ArgumentOutOfRangeException(nameof(next));
        lock (gate)
        {
            anchorScaled = Current();
            anchorReal = real.Elapsed;
            speed = next;
        }
    }

    /// <summary>
    /// 시간을 멈춘다. <paramref name="at"/>을 주면 정확히 그 시점에 멈춘다: 실제 시간 진행에서는 타이머가 늦게 깨어 조금 지나칠 수 있는데,
    /// 멈춤은 캡처의 "같은 시각"을 위한 것이라 정확한 시점이 중요하고, 멈춘 뒤에는 회차가 처리되지 않으므로 되돌려도 기록에 영향이 없다.
    /// </summary>
    public void Freeze(TimeSpan? at = null)
    {
        lock (gate)
        {
            anchorScaled = at ?? Current();
            held = true;
        }
    }

    /// <summary>시간을 건너뛴다 (데모 절전).</summary>
    public void Jump(TimeSpan by)
    {
        lock (gate)
        {
            anchorScaled = Current() + by;
            anchorReal = real.Elapsed;
        }
    }

    /// <summary>배속 경과가 <paramref name="target"/>에 닿을 때까지 기다린다. 멈춰 있으면 취소될 때까지.</summary>
    public async Task WaitUntil(TimeSpan target, Func<bool>? ready, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan now;
            bool isHeld, isVirtual;
            double sp;
            lock (gate) { now = Current(); isHeld = held; isVirtual = Virtual; sp = speed; }
            if (now >= target) return;
            if (isHeld)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (isVirtual)
            {
                if (ready?.Invoke() ?? true)
                {
                    lock (gate) if (Virtual && !held && anchorScaled < target) anchorScaled = target;
                    continue;
                }
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var wait = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, (long)((target - now).Ticks / sp)));
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>측정 하나의 데모 규칙 (M5 설계 3.4): 멈춤 회차, 배속 전환 회차, 절전 공백.</summary>
/// <param name="HoldRound">이 회차 + 0.5초에 시간축 전체가 멈춘다</param>
/// <param name="SpeedAfter">이 회차에 닿으면 시간축 배속을 바꾼다</param>
/// <param name="AtRound">시간축이 정확히 이 회차 + 0.5초일 때 한 번 부른다 (측정 작업자에서 동기로; 재현용 사용자 동작. 막히는 일을 하면 안 된다)</param>
public sealed record DemoRunRules(long? HoldRound = null, (long Round, double Speed)? SpeedAfter = null, (long Round, Action Act)? AtRound = null);

/// <summary>
/// 측정 시계 (데모). <c>Elapsed</c>는 측정 시작부터, <c>Now</c>는 시간축의 벽시계. 멈춤·배속 전환·절전은 시간축에 건다.
/// </summary>
public sealed class DemoClock : IClock
{
    private readonly DemoTimeBase timeBase;
    private readonly TimeSpan origin;
    private readonly DemoRunRules rules;
    private readonly Func<bool>? ready;
    private readonly ScenarioFaults? faults;
    private readonly DemoPower? power;
    private bool switched, suspended, acted;

    internal DemoClock(DemoTimeBase timeBase, DemoRunRules rules, Func<bool>? ready, ScenarioFaults? faults, DemoPower? power)
    {
        this.timeBase = timeBase;
        this.rules = rules;
        this.ready = ready;
        this.faults = faults;
        this.power = power;
        origin = timeBase.Scaled;
    }

    public TimeSpan Elapsed => timeBase.Scaled - origin;
    public DateTimeOffset Now => timeBase.Now;

    public async Task Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero) return;
        var target = Elapsed + duration;
        if (rules.SpeedAfter is { } sa && !switched && target >= TimeSpan.FromSeconds(sa.Round))
        {
            await timeBase.WaitUntil(origin + TimeSpan.FromSeconds(sa.Round), ready, cancellationToken).ConfigureAwait(false);
            timeBase.SetSpeed(sa.Speed);
            switched = true;
        }
        if (faults?.SuspendAt is long at && faults.SuspendSeconds is int seconds && !suspended && target >= TimeSpan.FromSeconds(at))
        {
            await timeBase.WaitUntil(origin + TimeSpan.FromSeconds(at), ready, cancellationToken).ConfigureAwait(false);
            suspended = true;
            power?.Suspend();
            timeBase.Jump(TimeSpan.FromSeconds(seconds));
            power?.Resume();
            target = Elapsed > target ? Elapsed : target;
        }
        if (rules.AtRound is { } ar && !acted && target >= TimeSpan.FromSeconds(ar.Round) + TimeSpan.FromMilliseconds(500))
        {
            await timeBase.WaitUntil(origin + TimeSpan.FromSeconds(ar.Round) + TimeSpan.FromMilliseconds(500), ready, cancellationToken).ConfigureAwait(false);
            acted = true;
            ar.Act();
        }
        if (rules.HoldRound is long hold && target >= TimeSpan.FromSeconds(hold) + TimeSpan.FromMilliseconds(500))
        {
            var point = origin + TimeSpan.FromSeconds(hold) + TimeSpan.FromMilliseconds(500);
            await timeBase.WaitUntil(point, ready, cancellationToken).ConfigureAwait(false);
            timeBase.Freeze(point);
        }
        await timeBase.WaitUntil(origin + target, ready, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>데모 절전 알림 (M3 절전 공백 경로를 탄다).</summary>
public sealed class DemoPower : IPowerEvents
{
    private readonly List<(Action Suspend, Action Resume)> subscribers = [];

    public IDisposable Subscribe(Action onSuspend, Action onResume)
    {
        var entry = (onSuspend, onResume);
        lock (subscribers) subscribers.Add(entry);
        return new Unsubscribe(() => { lock (subscribers) subscribers.Remove(entry); });
    }

    internal void Suspend() { foreach (var s in Snapshot()) s.Suspend(); }
    internal void Resume() { foreach (var s in Snapshot()) s.Resume(); }
    private (Action Suspend, Action Resume)[] Snapshot() { lock (subscribers) return subscribers.ToArray(); }

    private sealed class Unsubscribe(Action action) : IDisposable { public void Dispose() => action(); }
}

/// <summary>
/// 저장 고장을 넣는 기록 위치 (M5 설계 3.4). 실제 디스크 위치를 감싸고, 시작 실패·회차 구간 쓰기 실패만 바꾼다.
/// </summary>
public sealed class FaultyRecordRoot(IRecordRoot inner, bool failStart, Func<bool> failWrites) : IRecordRoot
{
    public string Kind => inner.Kind;
    public string? VolumeKey => inner.VolumeKey;
    public string? Path => inner.Path;

    public IRecordFolder? CreateFolder(string name)
    {
        if (failStart) throw new IOException("데모: 저장 위치에 쓸 수 없습니다.");
        return inner.CreateFolder(name) is { } folder ? new Folder(folder, failWrites) : null;
    }

    private sealed class Folder(IRecordFolder inner, Func<bool> failWrites) : IRecordFolder
    {
        public string Kind => inner.Kind;
        public IAppendFile OpenAppend(string name) => new File(inner.OpenAppend(name), failWrites);
        public bool WriteAllText(string name, string text, Func<bool>? mayCommit = null) => inner.WriteAllText(name, text, mayCommit);
    }

    private sealed class File(IAppendFile inner, Func<bool> failWrites) : IAppendFile
    {
        public long Length => inner.Length;
        public void Write(ReadOnlySpan<byte> bytes)
        {
            if (failWrites()) throw new IOException("데모: 쓰기 실패");
            inner.Write(bytes);
        }
        public void FlushToDisk()
        {
            if (failWrites()) throw new IOException("데모: 쓰기 실패");
            inner.FlushToDisk();
        }
        public void SetLength(long length) => inner.SetLength(length);
        public void Dispose() => inner.Dispose();
    }
}

/// <summary>측정 하나를 돌릴 데모 입력 (검사기·관측기·시계·기록 위치).</summary>
public sealed record DemoRun(IClock Clock, Func<Target, IProbe> ProbeFor, DemoNetwork Network, IPowerEvents Power, IRecordRoot Primary, IRecordRoot Fallback);

/// <summary>
/// 데모 세계 (M5 설계 3.4): 탐지 결과, 측정마다 새 입력. 데모는 가장 바깥(검사기·관측기·시계·저장 위치)만 바꿔 끼우고
/// 세션·기록·분석은 제품 코드를 그대로 탄다 (기획서 4.3).
/// </summary>
public sealed class DemoWorld
{
    private readonly DemoProbe probe;

    public DemoWorld(Scenario scenario, DemoTimeBase timeBase)
    {
        Scenario = scenario;
        TimeBase = timeBase;
        probe = new DemoProbe(scenario);
    }

    public Scenario Scenario { get; }
    public DemoTimeBase TimeBase { get; }

    /// <summary>그 대상의 그 회차 결과 (측정 검사기와 진단 검사기가 같은 답을 쓰도록).</summary>
    public ProbeResult Resolve(string targetId, long round) => probe.Resolve(targetId, round);

    /// <summary>탐지 (선택: 늦음, 실패). 실측 탐지와 같은 계획 함수를 거쳐 같은 안내 문장이 나온다.</summary>
    public async Task<DetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        if (Scenario.Faults?.DetectDelayMs is int delay && delay > 0) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return Detect();
    }

    public DetectionResult Detect()
    {
        var net = Scenario.Network ?? new ScenarioNetwork();
        var local = Scenario.Targets.FirstOrDefault(t => t.Role == "local");
        NetworkSnapshot snapshot;
        if (net.Kind == "none") snapshot = new NetworkSnapshot([], null, null);
        else
        {
            var kind = net.Kind == "wireless" ? InterfaceKind.Wireless : InterfaceKind.Wired;
            var interfaces = new List<InterfaceInfo> { new(DemoNetwork.InterfaceIndex, kind, net.Virtual ? Tristate.Yes : Tristate.No, local is not null) };
            for (int i = 0; i < net.OtherGateways; i++) interfaces.Add(new(100 + i, InterfaceKind.Other, Tristate.Unknown, true));
            snapshot = Scenario.Faults?.Detect switch
            {
                "unknown" => new NetworkSnapshot(interfaces, null, "demo-route-unavailable"),
                "no-ipv4" => new NetworkSnapshot(interfaces, null, null),
                _ => new NetworkSnapshot(interfaces, new BestRoute(DemoNetwork.InterfaceIndex,
                    local is null ? null : System.Net.IPAddress.Parse(local.Address)), null),
            };
        }
        var planned = DetectionPlanner.Plan(snapshot);
        if (planned.Proposal is not { } proposal) return planned;
        // 계획이 고른 구조(공유기 포함 여부)는 그대로, 대상은 시나리오의 문서용 주소로
        bool withLocal = proposal.Targets.Any(t => t.Role == TargetRole.Local);
        var targets = Scenario.Targets.Where(t => withLocal || t.Role != "local").Select(ToTarget).ToArray();
        return planned with { Proposal = proposal with { Targets = targets, Duration = PlannedDuration() } };
    }

    /// <summary>시나리오 길이를 측정 시간으로 (프로필 범위 안).</summary>
    public TimeSpan PlannedDuration()
    {
        var d = TimeSpan.FromSeconds(Scenario.RoundCount);
        return d < Profile.MinDuration ? Profile.MinDuration : d > Profile.MaxDuration ? Profile.MaxDuration : d;
    }

    public static Target ToTarget(ScenarioTarget t) =>
        new(t.Id, t.Name ?? t.Id, System.Net.IPAddress.Parse(t.Address), t.Role == "local" ? TargetRole.Local : TargetRole.External, t.Provider, ProbeMethod.Icmp);

    /// <summary>
    /// 측정 하나의 입력. 시계는 지금 시간축에서 새로 만든다 (측정마다 0회차부터). <paramref name="ready"/>는 처리 속도에 맞춘 진행에서
    /// 측정 처리가 따라왔는지 (밀려 있으면 시간을 더 진행하지 않는다).
    /// </summary>
    public DemoRun NewRun(IRecordRoot primary, IRecordRoot fallback, DemoRunRules rules, Func<bool>? ready)
    {
        var faults = Scenario.Faults;
        var power = new DemoPower();
        var clock = new DemoClock(TimeBase, rules, ready, faults, power);
        bool FailWrites()
        {
            if (faults?.StorageFailFrom is not long from) return false;
            long round = (long)clock.Elapsed.TotalSeconds;
            return round >= from && (faults.StorageRecoverAt is not long to || round < to);
        }
        bool failStart = faults?.StorageStartFail ?? false;
        IRecordRoot Wrap(IRecordRoot r) => failStart || faults?.StorageFailFrom is not null ? new FaultyRecordRoot(r, failStart, FailWrites) : r;
        return new DemoRun(clock, _ => probe, new DemoNetwork(Scenario), power, Wrap(primary), Wrap(fallback));
    }
}
