using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Demo;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>Wi‑Fi 신호 통계 (M12 설계 C [2-3]): 요약·재계산·리포트가 함께 쓰는 순수 함수.</summary>
public sealed class M12SignalStatisticsTests
{
    private static SignalRun Ok(int? q, int? r, long first, long last, long n) => new(SignalState.Ok, q, r, first, last, n);

    [Fact]
    public void TimeWeightedAverageMatchesTheDesignExample()
    {
        // 1초 간격 t=0~98 80%, t=99 20% → 79.4% (간격이 고르면 표본 평균과 같다)
        var s = SignalStatistics.Compute([Ok(80, null, 0, 98_000, 99), Ok(20, null, 99_000, 99_000, 1)], 1000, 100_000);
        Assert.Equal(79.4, s.QualityAvg);
        Assert.Equal(20, s.QualityMin);
        Assert.Equal(100, s.Samples);
        Assert.Equal(100_000, s.StateMs.Ok);
        Assert.Null(s.RssiAvg);
        Assert.Equal(0, s.RssiSamples);
    }

    [Fact]
    public void LastSampleIsClippedAtTheObservationEnd()
    {
        // 마지막 표본(t=99)은 관측 종료 99.5초에서 잘려 0.5초만 갖는다
        var s = SignalStatistics.Compute([Ok(80, null, 0, 98_000, 99), Ok(20, null, 99_000, 99_000, 1)], 1000, 99_500);
        Assert.Equal(Math.Round((80.0 * 99_000 + 20.0 * 500) / 99_500, 2), s.QualityAvg);
        Assert.Equal(99_500, s.StateMs.Ok);
        // 관측 종료 뒤에 시작한 묶음은 넣지 않는다
        var early = SignalStatistics.Compute([Ok(80, null, 0, 98_000, 99), Ok(20, null, 99_000, 99_000, 1)], 1000, 50_000);
        Assert.Equal(80.0, early.QualityAvg);
        Assert.Equal(50_000, early.StateMs.Ok);
        Assert.Equal(80, early.QualityMin);
    }

    [Fact]
    public void QualityAndRssiUseSeparateDenominatorsAndBrokenStatesAreNotAveraged()
    {
        var s = SignalStatistics.Compute(
        [
            Ok(60, -50, 0, 0, 1),
            Ok(null, -70, 1000, 1000, 1),                                         // 품질 없이 RSSI만 → 품질 평균에 들지 않는다
            new SignalRun(SignalState.Denied, null, null, 2000, 4000, 3),          // 권한 없음: 시간은 상태별로만
        ], 1000, 5000);
        Assert.Equal(60.0, s.QualityAvg);
        Assert.Equal(-60.0, s.RssiAvg);
        Assert.Equal((1L, 2L), (s.QualitySamples, s.RssiSamples));
        Assert.Equal(new SignalStateCounts(2000, 0, 3000, 0, 0), s.StateMs);
        Assert.Equal(new SignalStateCounts(2, 0, 3, 0, 0), s.StateSamples);
        Assert.Equal(5, s.Samples);
    }

    [Fact]
    public void SingleSampleIsItsOwnAverageAndZeroValidTimeIsNone()
    {
        var one = SignalStatistics.Compute([Ok(0, -80, 5000, 5000, 1)], 1000, 10_000);
        Assert.Equal(0.0, one.QualityAvg);   // 품질 0%는 값이다 (없음과 다름)
        Assert.Equal(-80.0, one.RssiAvg);
        var zero = SignalStatistics.Compute([Ok(0, -80, 5000, 5000, 1)], 1000, 5000);   // 끝에서 잘려 유효 시간 0
        Assert.Null(zero.QualityAvg);
        Assert.Null(zero.RssiAvg);
        Assert.Equal(0, zero.QualityMin);
        var none = SignalStatistics.Compute([], 1000, 5000);
        Assert.Equal((0L, (double?)null, (int?)null), (none.Samples, none.QualityAvg, none.QualityMin));
    }

    [Fact]
    public void ReadingsAreNormalizedToTheRecordContract()
    {
        Assert.Equal(new SignalReading(SignalState.Ok, null, null, null, "realtime"),
            SignalReading.Normalize(new SignalReading(SignalState.Ok, 101, 5, null, "realtime")));   // 범위 밖은 값 없음
        Assert.Equal(new SignalReading(SignalState.Ok, 0, -127, null, "realtime"),
            SignalReading.Normalize(new SignalReading(SignalState.Ok, 0, -127, null, "realtime")));
        Assert.Equal(new SignalReading(SignalState.Denied, null, null, "access-denied", null),
            SignalReading.Normalize(new SignalReading(SignalState.Denied, 50, -60, "access-denied", null)));   // 정상이 아니면 값을 버린다
        Assert.Equal(SignalState.Failed, SignalReading.Normalize(null).State);
    }

    [Fact]
    public void RecordsWithoutSignalEventsHaveNoSignalData()
    {
        var events = new List<JsonObject>
        {
            new() { ["id"] = "x", ["type"] = "link", ["at"] = 0, ["wall"] = RecordSchema.Iso(Start), ["source"] = "direct", ["collection"] = "up",
                ["adapter"] = "a1", ["interfaceIndex"] = 7, ["kind"] = "wireless", ["virtual"] = false, ["eventMs"] = 0 },
        };
        var ev = SignalEvidenceBuilder.FromEvents(events, 1000);
        Assert.False(ev.HasData);
        Assert.Same(SignalEvidence.None, ev);
    }

    [Fact]
    public void ChangeEventClosesThePreviousRunEvenWhenItsStartWasLost()
    {
        // 앞 변경 이벤트(t=0)를 잃어도 prev* 필드로 앞 묶음을 복원한다
        var events = new List<JsonObject>
        {
            Signal("change", 70_000, new JsonObject { ["state"] = "denied", ["sampleMs"] = 70_000, ["prevState"] = "ok", ["prevQuality"] = 70, ["prevRssi"] = -60,
                ["prevFirstMs"] = 0, ["prevLastMs"] = 69_000, ["prevSamples"] = 70 }),
        };
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(events, 1000).Adapters);
        Assert.Equal(new SignalRun(SignalState.Ok, 70, -60, 0, 69_000, 70), a.Runs[0]);
        Assert.Equal(new SignalRun(SignalState.Denied, null, null, 70_000, 70_000, 1, true), a.Runs[1]);
        Assert.True(a.LastObserved);
        Assert.Equal(71_000, a.Stats.EndMs);   // 요약이 없으면 마지막 저장 표본도 간격 하나를 갖는다
    }

    /// <summary>G4 M12 1라운드 1: 가운데 변경 이벤트(1초 20%)를 잃어도 80% 묶음을 늘리지 않고 prev 값으로 20% 묶음을 복원한다.</summary>
    [Fact]
    public void LosingAMiddleChangeEventDoesNotMergeDifferentRuns()
    {
        var events = new List<JsonObject>
        {
            Signal("change", 0, new JsonObject { ["state"] = "ok", ["quality"] = 80, ["sampleMs"] = 0 }),
            // 1초 20% 변경 이벤트는 잃었다
            Signal("change", 2_000, new JsonObject { ["state"] = "ok", ["quality"] = 90, ["sampleMs"] = 2_000, ["prevState"] = "ok", ["prevQuality"] = 20,
                ["prevFirstMs"] = 1_000, ["prevLastMs"] = 1_000, ["prevSamples"] = 1 }),
        };
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(events, 1000).Adapters);
        Assert.Equal(3, a.Runs.Count);
        Assert.Equal(new SignalRun(SignalState.Ok, 80, null, 0, 0, 1), a.Runs[0]);
        Assert.Equal(new SignalRun(SignalState.Ok, 20, null, 1_000, 1_000, 1), a.Runs[1]);
        Assert.Equal(90, a.Runs[2].Quality);
        Assert.Equal(Math.Round((80.0 + 20.0 + 90.0) / 3, 2), a.Stats.QualityAvg);
    }

    /// <summary>G4 M12 3라운드 1: 변경 이벤트를 잃은 뒤 다른 묶음의 체크포인트만 와도 앞 묶음(이미 체크포인트로 저장된 범위)을 버리지 않는다.</summary>
    [Fact]
    public void CheckpointOfAnotherRunAfterALostChangeKeepsTheEarlierRun()
    {
        var events = new List<JsonObject>
        {
            Signal("change", 0, new JsonObject { ["state"] = "ok", ["quality"] = 80, ["sampleMs"] = 0 }),
            Signal("checkpoint", 60_000, new JsonObject { ["reason"] = "periodic", ["state"] = "ok", ["quality"] = 80, ["firstMs"] = 0, ["lastMs"] = 60_000, ["runSamples"] = 61 }),
            // 70초 20% 변경 이벤트는 잃었다
            Signal("checkpoint", 130_000, new JsonObject { ["reason"] = "periodic", ["state"] = "ok", ["quality"] = 20, ["firstMs"] = 70_000, ["lastMs"] = 130_000, ["runSamples"] = 61 }),
        };
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(events, 1000).Adapters);
        Assert.Equal([new SignalRun(SignalState.Ok, 80, null, 0, 60_000, 61), new SignalRun(SignalState.Ok, 20, null, 70_000, 130_000, 61)], a.Runs);
        Assert.False(a.Runs[1].Continues);   // 사이를 모르므로 잇지 않는다
        Assert.Equal(122, a.Stats.Samples);
    }

    /// <summary>G4 M12 1라운드 2: 같은 어댑터를 다시 관측해도 앞 묶음의 마지막 표본은 그 묶음의 관측 종료에서 잘린다.</summary>
    [Fact]
    public void ResumedObservationKeepsTheEarlierEndBoundary()
    {
        var events = new List<JsonObject>
        {
            Signal("change", 900, new JsonObject { ["state"] = "ok", ["quality"] = 80, ["sampleMs"] = 900 }),
            Signal("checkpoint", 1_000, new JsonObject { ["reason"] = "end", ["state"] = "ok", ["quality"] = 80, ["firstMs"] = 900, ["lastMs"] = 900,
                ["runSamples"] = 1, ["endMs"] = 1_000 }),
            Signal("change", 2_000, new JsonObject { ["state"] = "ok", ["quality"] = 40, ["sampleMs"] = 2_000 }),
            Signal("checkpoint", 3_000, new JsonObject { ["reason"] = "end", ["state"] = "ok", ["quality"] = 40, ["firstMs"] = 2_000, ["lastMs"] = 2_000,
                ["runSamples"] = 1, ["endMs"] = 3_000 }),
        };
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(events, 1000).Adapters);
        Assert.Equal(1_000, a.Runs[0].EndMs);
        Assert.Equal(100, SignalStatistics.Weight(a.Runs[0], 1000, 3_000));          // 900 → 1000 (유선으로 바뀐 때)
        Assert.Equal(1_100, a.Stats.StateMs.Ok);                                      // 100 + 1000
        Assert.Equal(Math.Round((80.0 * 100 + 40.0 * 1000) / 1_100, 2), a.Stats.QualityAvg);
        var trace = NetPulse.Core.Presentation.SignalTrace.FromEvidence(new SignalEvidence([a]), 1000);
        Assert.Equal(1_000, trace.Runs[0].ToMs);                                     // 그림도 유선으로 바뀐 때까지만
    }

    private static JsonObject Signal(string collection, long at, JsonObject fields)
    {
        var o = new JsonObject { ["id"] = "x", ["type"] = "signal", ["at"] = at, ["wall"] = RecordSchema.Iso(Start), ["adapter"] = "a1", ["interfaceIndex"] = 9,
            ["kind"] = "wireless", ["collection"] = collection };
        foreach (var (k, v) in fields.ToArray()) { fields.Remove(k); o[k] = v; }
        return o;
    }
}

/// <summary>세션의 Wi‑Fi 신호 기록: 표본 → 파일 → 다시 읽기 (M12 설계 C [1-4]·[2-2]·[2-3]).</summary>
public sealed class M12SignalSessionTests : IDisposable
{
    private const string Id = "20261122T000000Z_wifi01";
    private readonly TempDir dir = new();
    private readonly DiskRecordRoot primary;
    private readonly DiskRecordRoot fallback;

    public M12SignalSessionTests()
    {
        primary = new DiskRecordRoot(dir.Sub("p"), "primary");
        fallback = new DiskRecordRoot(dir.Sub("f"), "fallback");
    }

    public void Dispose() => dir.Dispose();

    private static readonly Profile FiveMinutes = new("p", "시험",
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(5), ExpectedConnection.Wireless);

    internal static SignalReading Good(int? q, int? r) => new(SignalState.Ok, q, r, null, q is null ? "rssi" : "realtime");

    /// <summary>경로·링크·신호를 한 번에 흉내 낸다. 유선(3)·무선(9) 어댑터, 경로 번호는 회차로 정한다.</summary>
    internal sealed class FakeNet : IRouteObserver, ILinkObserver, ISignalSource
    {
        public static readonly AdapterInfo Wired = new("{WIRED-KEY-0002}", 3, "wired", false);
        public static readonly AdapterInfo Wifi = new("{WIFI-KEY-0001}", 9, "wireless", false);
        private readonly HashSet<int> watched = [];
        private Action<LinkSample>? sink;
        private long round;
        public Func<long, int?> RouteIndex = _ => Wifi.InterfaceIndex;
        public Func<long, SignalReading> SignalAt = _ => Good(70, -60);
        public int WiredReads;

        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round)
        {
            Volatile.Write(ref this.round, round);
            var index = RouteIndex(round);
            return targets.Select(t => index is int i
                ? new RouteSample(t.Id, i, round % 30 == 0 ? (i == Wifi.InterfaceIndex ? "wireless" : "wired") : null, true, null)
                : new RouteSample(t.Id, null, null, false, "E1168")).ToArray();
        }

        public (LinkCapability Capability, string? Code) Start(Action<LinkSample> sink) { this.sink = sink; return (LinkCapability.Supported, null); }
        public void Watch(IReadOnlyCollection<int> interfaceIndexes) { lock (watched) foreach (var i in interfaceIndexes) watched.Add(i); }
        public void Poll() { foreach (var a in Adapters) sink?.Invoke(new LinkSample(a, LinkState.Up, true)); }
        public IReadOnlyList<AdapterInfo> Adapters { get { lock (watched) return new[] { Wired, Wifi }.Where(a => watched.Contains(a.InterfaceIndex)).ToArray(); } }

        public SignalReading Read(AdapterInfo adapter)
        {
            if (adapter.Key != Wifi.Key) { Interlocked.Increment(ref WiredReads); throw new InvalidOperationException("유선 어댑터를 읽음"); }
            return SignalAt(Volatile.Read(ref round));
        }

        public void Dispose() { }
    }

    private sealed class FakePower : IPowerEvents
    {
        private Action? suspend, resume;
        public IDisposable Subscribe(Action onSuspend, Action onResume) { suspend = onSuspend; resume = onResume; return new Nothing(); }
        public void Suspend() => suspend!();
        public void Resume() => resume!();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private SessionOptions Options(IClock clock, FakeNet net, bool signal = true, IPowerEvents? power = null) => new()
    {
        Profile = FiveMinutes, Clock = clock, ProbeFor = _ => new RecordingProbe(), Primary = primary, Fallback = fallback, NewId = _ => Id,
        Routes = net, Links = net, Signal = signal ? net : null, Power = power, LinkLogSettle = TimeSpan.Zero, RetryTimeout = TimeSpan.FromMilliseconds(300),
    };

    /// <summary>SessionLinkTests와 같은 진행: 넘길 때마다 관측이 따라오기를 기다리고, 끝에 게시까지 기다린다.</summary>
    private static async Task Rounds(ManualClock clock, MeasurementSession s, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
            long due = (long)clock.Elapsed.TotalSeconds;
            await Eventually(() => s.Status.Observed >= due, $"관측 따라옴 {due}");
        }
        long target = (long)clock.Elapsed.TotalSeconds;
        await Eventually(() => s.Engine.State.Latest?.Tick.Index >= target, $"엔진 회차 {target}");
        await Eventually(() => s.Status.Observed >= target + 1, "관측");
        await Eventually(() => s.Live.AnalysisInputs().Rounds.Count >= target + 1, "게시");
    }

    private MeasurementRecord Reread() => RecordReader.Read(RecordReader.FindSegments(Id, [primary.Path!, fallback.Path!]));

    private static JsonObject[] Signals(IEnumerable<JsonObject> events) => events.Where(e => (string?)e["type"] == "signal").ToArray();

    private static string?[] Collections(IEnumerable<JsonObject> events) =>
        Signals(events).Select(e => (string?)e["collection"] + ((string?)e["reason"] is { } r ? ":" + r : "")).ToArray();

    private static SignalReading Script(long r) => r switch
    {
        < 70 => Good(70, -60),
        < 75 => new SignalReading(SignalState.Denied, null, null, "access-denied", null),
        < 80 => new SignalReading(SignalState.NotConnected, null, null, "not-connected", null),
        _ => Good(0, -90),
    };

    [Fact]
    public async Task ChangesCheckpointsAndSummarySurviveTheFileAndMatchRecomputation()
    {
        var clock = new ManualClock(Start);
        var net = new FakeNet { SignalAt = Script };
        var s = MeasurementSession.Start(Options(clock, net));
        await Rounds(clock, s, 130);
        await s.StopAsync();

        var rec = Reread();
        var header = rec.Header!;
        Assert.Equal(131, rec.Rounds.Count);   // 권한 없음·연결 안 됨 동안에도 측정은 계속한다
        // 바뀔 때만 변경, 반복은 60초마다 체크포인트, 끝에 마지막 체크포인트 → 요약 (M12 설계 C [1-4]·[2-2])
        Assert.Equal(["change", "checkpoint:periodic", "change", "change", "change", "checkpoint:end", "summary"], Collections(rec.Events));
        var denied = Signals(rec.Events)[2];
        Assert.Equal(("denied", "access-denied", 70_000L), ((string?)denied["state"], (string?)denied["code"], (long)denied["sampleMs"]!));
        Assert.Equal(("ok", 70, -60), ((string?)denied["prevState"], (int)denied["prevQuality"]!, (int)denied["prevRssi"]!));
        Assert.Equal((0L, 69_000L, 70L), ((long)denied["prevFirstMs"]!, (long)denied["prevLastMs"]!, (long)denied["prevSamples"]!));
        Assert.Equal((71L, 70L, 1L), ((long)denied["samples"]!, (long)denied["stateSamples"]!["ok"]!, (long)denied["stateSamples"]!["denied"]!));
        Assert.Equal(60_000L, (long)Signals(rec.Events)[1]["at"]!);

        var ev = SignalEvidenceBuilder.FromEvents(rec.Events, header.IntervalMs);
        var a = Assert.Single(ev.Adapters);
        Assert.Equal(("a1", 9, "wireless"), (a.Alias, a.InterfaceIndex, a.Kind));
        Assert.Equal(
        [
            new SignalRun(SignalState.Ok, 70, -60, 0, 69_000, 70),
            new SignalRun(SignalState.Denied, null, null, 70_000, 74_000, 5, true),
            new SignalRun(SignalState.NotConnected, null, null, 75_000, 79_000, 5, true),
            new SignalRun(SignalState.Ok, 0, -90, 80_000, 130_000, 51, true, a.Summary!.EndMs),   // 측정 끝의 마지막 체크포인트가 관측 종료
        ], a.Runs);
        Assert.False(a.LastObserved);
        Assert.NotNull(a.Summary);
        Assert.Equal(a.Summary, a.Recomputed);   // 같은 관측 종료 시각에서 요약 = 이벤트 재계산 [2-3]
        var stats = a.Summary;
        Assert.Equal(130_000, stats.EndMs);
        Assert.Equal(131, stats.Samples);
        Assert.Equal(new SignalStateCounts(120_000, 5000, 5000, 0, 0), stats.StateMs);
        Assert.Equal((40.83, 0, -72.5, -90), (stats.QualityAvg!.Value, stats.QualityMin!.Value, stats.RssiAvg!.Value, stats.RssiMin!.Value));

        // 개인 정보: 어댑터 Key(GUID)·SSID·BSSID를 쓰지 않는다. 유선 어댑터는 읽지 않는다
        Assert.DoesNotContain(rec.Events, e => e.ToJsonString().Contains("KEY-000", StringComparison.Ordinal));
        Assert.All(Signals(rec.Events), e => Assert.False(e.ContainsKey("ssid") || e.ContainsKey("bssid") || e.ContainsKey("key")));
        Assert.Equal(0, net.WiredReads);

        // 기록 호환: 신호 이벤트가 손상·링크 근거·분석을 바꾸지 않는다
        Assert.Equal(RecordStatus.Ok, rec.Status);
        Assert.Equal(0, rec.Damage.DamagedEvents);
        var without = rec with { Events = rec.Events.Where(e => (string?)e["type"] != "signal").ToArray() };
        static object[] LinkView(LinkEvidence l) => [.. l.Summaries.Select(x => (x.Adapter, x.Source, x.Display, x.Code)), l.Downs.Count, l.Unmapped, l.Unlinked];
        Assert.Equal(LinkView(LinkEvidenceBuilder.FromEvents(without.Events)), LinkView(LinkEvidenceBuilder.FromEvents(rec.Events)));
        var withSignal = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(rec);
        var withoutSignal = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(without);
        Assert.Equal((withoutSignal.Grade.Grade, withoutSignal.Grade.Reason), (withSignal.Grade.Grade, withSignal.Grade.Reason));

        // 여러 조각으로 나뉜 기록도 같게 복원한다
        var signals = Signals(rec.Events);
        int cut = rec.Events.ToList().IndexOf(signals[3]);
        var seg0 = WriteSegment("split0", header with { Segment = 0 }, rec.Events.Take(cut));
        var seg1 = WriteSegment("split1", header with { Segment = 1, LocationKind = "fallback" }, rec.Events.Skip(cut));
        var split = RecordReader.Read([seg0, seg1]);
        var b = Assert.Single(SignalEvidenceBuilder.FromEvents(split.Events, split.Header!.IntervalMs).Adapters);
        Assert.Equal(a.Runs, b.Runs);
        Assert.Equal(a.Summary, b.Summary);
        Assert.Equal(a.Recomputed, b.Recomputed);
    }

    private string WriteSegment(string name, RecordHeader header, IEnumerable<JsonObject> events)
    {
        var folder = dir.Sub(name);
        TempDir.Write(Path.Combine(folder, "measurement.json"), RecordSchema.HeaderJson(header));
        TempDir.Write(Path.Combine(folder, "events.jsonl"), string.Concat(events.Select(e => e.ToJsonString() + "\n")));
        return folder;
    }

    [Fact]
    public async Task AbnormalEndRestoresUpToTheLastSavedCheckpointAndLiveCopyHasTheLastSample()
    {
        var clock = new ManualClock(Start);
        var net = new FakeNet();
        var s = MeasurementSession.Start(Options(clock, net));
        await Rounds(clock, s, 150);

        // 지금 멈췄다면(요약 없음) 기록에 남은 것: 0초 변경, 60·120초 체크포인트
        var inputs = s.Live.AnalysisInputs();
        Assert.Equal(["change", "checkpoint:periodic", "checkpoint:periodic"], Collections(inputs.Events));
        var cut = Assert.Single(SignalEvidenceBuilder.FromEvents(inputs.Events, 1000).Adapters);
        Assert.True(cut.LastObserved);
        Assert.Null(cut.Summary);
        Assert.Equal([new SignalRun(SignalState.Ok, 70, -60, 0, 120_000, 121)], cut.Runs);
        Assert.Equal(121_000, cut.Stats.EndMs);
        Assert.Equal((70.0, 121L), (cut.Stats.QualityAvg!.Value, cut.Stats.Samples));
        Assert.True(150_000 - cut.Runs[^1].LastMs <= MeasurementSession.SignalCheckpointMs, "잃는 것은 최대 60초");

        // 실시간 사본은 줄로 쓰지 않은 반복 표본까지 안다 (LinkLive와 같은 방식)
        Assert.True(inputs.Signal.Configured);
        Assert.False(inputs.Signal.SummaryWritten);
        var live = Assert.Single(inputs.Signal.Adapters);
        Assert.Equal("a1", live.Alias);
        Assert.Equal(new SignalRun(SignalState.Ok, 70, -60, 0, 150_000, 151), live.Current);
        Assert.Equal(70.0, live.StatsAt(inputs.ElapsedMs, inputs.Signal.IntervalMs).QualityAvg);
        Assert.Equal(inputs.Signal.Adapters.Count, s.Live.Read(null).Signal.Adapters.Count);

        await s.StopAsync();
        Assert.True(s.Live.AnalysisInputs().Signal.SummaryWritten);
        var full = Assert.Single(SignalEvidenceBuilder.FromEvents(Reread().Events, 1000).Adapters);
        Assert.False(full.LastObserved);
        Assert.Equal(151, full.Summary!.Samples);
    }

    [Fact]
    public async Task SuspendGapBreaksTheRunEvenWithTheSameValue()
    {
        var clock = new ManualClock(Start);
        var power = new FakePower();
        var s = MeasurementSession.Start(Options(clock, new FakeNet(), power: power));
        await Rounds(clock, s, 2);                    // 0, 1, 2
        power.Suspend();
        clock.ShiftWall(TimeSpan.FromHours(1));
        power.Resume();
        await Rounds(clock, s, 2);                    // 3, 4
        await s.StopAsync();

        var rec = Reread();
        var change = Signals(rec.Events).Where(e => (string?)e["collection"] == "change").ToArray();
        Assert.Equal(2, change.Length);
        Assert.Equal(("gap", 2000L, 3L), ((string?)change[1]["break"], (long)change[1]["prevLastMs"]!, (long)change[1]["prevSamples"]!));
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(rec.Events, 1000).Adapters);
        Assert.Equal([new SignalRun(SignalState.Ok, 70, -60, 0, 2000, 3), new SignalRun(SignalState.Ok, 70, -60, 3000, 4000, 2, EndMs: a.Summary!.EndMs)], a.Runs);
        Assert.False(a.Runs[1].Continues);   // 공백을 건너 선을 잇지 않는다
        Assert.Equal(a.Summary, a.Recomputed);
    }

    [Fact]
    public async Task SourceExceptionIsRecordedAsFailedAndMeasuringContinues()
    {
        var clock = new ManualClock(Start);
        var net = new FakeNet { SignalAt = r => r is 3 or 4 ? throw new InvalidOperationException("드라이버 오류") : Good(55, -66) };
        var s = MeasurementSession.Start(Options(clock, net));
        await Rounds(clock, s, 8);
        await s.StopAsync();

        var rec = Reread();
        Assert.Equal(9, rec.Rounds.Count);
        var failed = Signals(rec.Events).Single(e => (string?)e["state"] == "failed");
        Assert.Equal(("InvalidOperationException", 3000L), ((string?)failed["code"], (long)failed["sampleMs"]!));
        Assert.DoesNotContain(rec.Events, e => e.ToJsonString().Contains("드라이버 오류", StringComparison.Ordinal));   // 예외 메시지는 쓰지 않는다
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(rec.Events, 1000).Adapters);
        Assert.Equal([SignalState.Ok, SignalState.Failed, SignalState.Ok], a.Runs.Select(r => r.State));
        Assert.Equal(2, a.Summary!.StateSamples.Failed);
        Assert.Equal(55.0, a.Summary.QualityAvg);   // 읽기 실패 구간은 평균에 넣지 않는다
    }

    [Fact]
    public async Task SignalAliasesMatchLinkAliasesAndRouteChangeEndsTheObservation()
    {
        var clock = new ManualClock(Start);
        var net = new FakeNet { RouteIndex = r => r is < 5 or (>= 15 and < 25) ? FakeNet.Wired.InterfaceIndex : FakeNet.Wifi.InterfaceIndex };
        var s = MeasurementSession.Start(Options(clock, net));
        await Rounds(clock, s, 30);
        await s.StopAsync();

        var rec = Reread();
        var linkAlias = rec.Events.Where(e => (string?)e["type"] == "link" && e["interfaceIndex"] is not null)
            .GroupBy(e => (int)e["interfaceIndex"]!).ToDictionary(g => g.Key, g => g.Select(e => (string?)e["adapter"]).Distinct().Single());
        Assert.Equal("a1", linkAlias[FakeNet.Wired.InterfaceIndex]);   // 유선이 먼저 관측됨
        Assert.Equal("a2", linkAlias[FakeNet.Wifi.InterfaceIndex]);
        Assert.All(Signals(rec.Events), e => Assert.Equal(("a2", "wireless"), ((string?)e["adapter"], (string?)e["kind"])));
        Assert.Equal(0, net.WiredReads);

        // 경로가 유선으로 바뀌면 무선 관측 종료(마지막 체크포인트), 돌아오면 앞 묶음 없이 새로 시작
        Assert.Equal(["change", "checkpoint:end", "change", "checkpoint:end", "summary"], Collections(rec.Events));
        Assert.Equal(15_000L, (long)Signals(rec.Events)[1]["endMs"]!);
        Assert.Null(Signals(rec.Events)[2]["prevLastMs"]);
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(rec.Events, 1000).Adapters);
        Assert.Equal([new SignalRun(SignalState.Ok, 70, -60, 5000, 14_000, 10, EndMs: 15_000), new SignalRun(SignalState.Ok, 70, -60, 25_000, 30_000, 6, EndMs: a.Summary!.EndMs)], a.Runs);
        Assert.Equal(15_000, a.Summary!.StateMs.Ok);
        Assert.Equal(a.Summary, a.Recomputed);
    }

    [Fact]
    public async Task WiredOnlyOrUnconfiguredMeasurementsHaveNoSignalData()
    {
        var clock = new ManualClock(Start);
        var wiredOnly = new FakeNet { RouteIndex = _ => FakeNet.Wired.InterfaceIndex };
        var s = MeasurementSession.Start(Options(clock, wiredOnly));
        await Rounds(clock, s, 3);
        Assert.True(s.Live.AnalysisInputs().Signal.Configured);
        Assert.Empty(s.Live.AnalysisInputs().Signal.Adapters);
        await s.StopAsync();
        Assert.False(SignalEvidenceBuilder.FromEvents(Reread().Events, 1000).HasData);
        Assert.Equal(0, wiredOnly.WiredReads);
    }

    [Fact]
    public async Task WithoutASignalSourceTheLiveCopyIsNone()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock, new FakeNet(), signal: false));
        await Rounds(clock, s, 2);
        Assert.Same(SignalLive.None, s.Live.AnalysisInputs().Signal);
        await s.StopAsync();
        Assert.Empty(Signals(Reread().Events));
    }
}

/// <summary>데모 시나리오의 신호 (M12 설계 C 데모): 약해졌다 끊김, 강한데 끊김.</summary>
public sealed class M12SignalDemoTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private static Scenario Load(string name) => Scenario.Load(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json"));

    private static string Json(string network, string extra) => $$"""
        { "version": 1, "name": "t", "seed": 1, "durationRounds": 10, {{network}}
          "targets": [ { "id": "gateway", "role": "local", "address": "192.0.2.1" } ],
          "segments": [ { "from": 0, "to": 10, "target": "*", "rtt": { "base": 2 } } ]{{extra}} }
        """;

    private const string Wireless = "\"network\": { \"kind\": \"wireless\" },";

    [Fact]
    public void ScenarioSignalValuesAndDefaults()
    {
        var weak = Load("wifi-weak-drop");
        Assert.Equal(new SignalReading(SignalState.Ok, 86, -51, null, "realtime"), weak.SignalAt(0));
        Assert.Equal(14, weak.SignalAt(115)!.Quality);
        Assert.Equal(SignalState.NotConnected, weak.SignalAt(120)!.State);
        Assert.True(weak.LinkDownAt(120));
        var strong = Load("wifi-strong-outage");
        Assert.Equal(91, strong.SignalAt(105)!.Quality);
        Assert.False(strong.LinkDownAt(105));
        Assert.Null(Load("steady").SignalAt(0));   // 유선 시나리오는 신호가 없다

        // 무선인데 signal이 없으면 기본값, 링크가 내려간 동안은 연결 안 됨
        var plain = Scenario.Parse(Json(Wireless, ", \"links\": [ { \"from\": 3, \"to\": 5 } ]"));
        Assert.Equal(new SignalReading(SignalState.Ok, Scenario.DefaultQuality, Scenario.DefaultRssi, null, "realtime"), plain.SignalAt(0));
        Assert.Equal(SignalState.NotConnected, plain.SignalAt(4)!.State);
        // RSSI만 있으면 RSSI 방법
        var rssiOnly = Scenario.Parse(Json(Wireless, ", \"signal\": [ { \"from\": 0, \"to\": 10, \"rssi\": -70 } ]"));
        Assert.Equal(new SignalReading(SignalState.Ok, null, -70, null, "rssi"), rssiOnly.SignalAt(2));
        var denied = Scenario.Parse(Json(Wireless, ", \"signal\": [ { \"from\": 0, \"to\": 10, \"state\": \"denied\" } ]"));
        Assert.Equal(SignalState.Denied, denied.SignalAt(2)!.State);
    }

    [Theory]
    [InlineData("", "{ \"from\": 0, \"to\": 10, \"quality\": 50 }")]                          // 유선 시나리오에 신호
    [InlineData(Wireless, "{ \"from\": 0, \"to\": 10 }")]                                      // ok인데 값 없음
    [InlineData(Wireless, "{ \"from\": 0, \"to\": 10, \"state\": \"denied\", \"quality\": 5 }")]   // ok가 아닌데 값
    [InlineData(Wireless, "{ \"from\": 0, \"to\": 10, \"quality\": 101 }")]
    [InlineData(Wireless, "{ \"from\": 0, \"to\": 10, \"rssi\": 3 }")]
    [InlineData(Wireless, "{ \"from\": 5, \"to\": 5, \"quality\": 50 }")]
    [InlineData(Wireless, "{ \"from\": 0, \"to\": 10, \"state\": \"weak\" }")]
    [InlineData(Wireless, "{ \"from\": 0, \"to\": 10, \"quality\": 50, \"ssid\": \"x\" }")]       // 모르는 필드
    public void InvalidSignalEntriesAreRejected(string network, string entry) =>
        Assert.Throws<ScenarioException>(() => Scenario.Parse(Json(network, ", \"signal\": [ " + entry + " ]")));

    [Fact]
    public async Task WeakThenDropScenarioRecordsTheSignalOnTheLinkAdapter()
    {
        var scenario = Load("wifi-weak-drop");
        var demo = new DemoProbe(scenario);
        var network = new DemoNetwork(scenario);
        var profile = new Profile("demo", "데모",
            scenario.ProbeTargets.Select(t => new Target(t.Id, t.Id, t.Address, t.Role == "local" ? TargetRole.Local : TargetRole.External, t.Id, ProbeMethod.Icmp)).ToArray(),
            Profile.DefaultTimeout, TimeSpan.FromMinutes(3), ExpectedConnection.Wireless);
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(new SessionOptions
        {
            Profile = profile, Clock = clock, ProbeFor = _ => demo, Routes = network, Links = network, Signal = network, NewId = _ => "demo-wifi",
            Primary = new DiskRecordRoot(dir.Sub("p"), "primary"), Fallback = new DiskRecordRoot(dir.Sub("f"), "fallback"),
        });
        for (int i = 0; i < 179; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        var result = await s.Completion.WaitAsync(SessionLimit, TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);

        var rec = RecordReader.Read(RecordReader.FindSegments("demo-wifi", [dir.Sub("p")]));
        var a = Assert.Single(SignalEvidenceBuilder.FromEvents(rec.Events, rec.Header!.IntervalMs).Adapters);
        (SignalState State, int? Quality, long FirstMs, long LastMs)[] expected =
        [
            (SignalState.Ok, 86, 0, 59_000), (SignalState.Ok, 62, 60_000, 89_000), (SignalState.Ok, 38, 90_000, 109_000),
            (SignalState.Ok, 14, 110_000, 117_000), (SignalState.NotConnected, null, 118_000, 125_000), (SignalState.Ok, 82, 126_000, 179_000),
        ];
        Assert.Equal(expected, a.Runs.Select(r => (r.State, r.Quality, r.FirstMs, r.LastMs)));
        Assert.Equal(a.Summary, a.Recomputed);
        Assert.Equal(8000, a.Summary!.StateMs.NotConnected);
        // 신호 선과 링크 끊김이 같은 어댑터(별칭)다
        var down = Assert.Single(LinkEvidenceBuilder.FromEvents(rec.Events).Downs, d => d.Source == LinkSource.Direct);
        Assert.Equal(a.Alias, down.Adapter);
        Assert.Equal(118_000, down.FromMs);
    }
}
