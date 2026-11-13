using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M3 G4 1라운드 지적을 막는 경계 시험 (`.loop/reviews/m3-g4-r1-answer.md`).</summary>
public sealed class ReviewR1Tests : IDisposable
{
    private const string Id = "20261113T000000Z_test01";
    private static readonly DateTimeOffset W0 = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private readonly TempDir primaryDir = new();
    private readonly TempDir fallbackDir = new();
    private readonly FaultyRoot primary;
    private readonly FaultyRoot sameVolumeFallback;

    public ReviewR1Tests()
    {
        primary = new FaultyRoot(new DiskRecordRoot(primaryDir.Path, "primary"), "VOL-A");
        sameVolumeFallback = new FaultyRoot(new DiskRecordRoot(fallbackDir.Path, "fallback"), "VOL-A");
    }

    public void Dispose()
    {
        primaryDir.Dispose();
        fallbackDir.Dispose();
    }

    private static readonly Profile OneMinute = new("p", "시험",
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(1), ExpectedConnection.Any);

    private SessionOptions Options(IClock clock) => new()
    {
        Profile = OneMinute, Clock = clock, ProbeFor = _ => new RecordingProbe(), Primary = primary, Fallback = sameVolumeFallback,
        NewId = _ => Id, RetryTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static async Task Rounds(ManualClock clock, MeasurementSession s, int count)
    {
        for (int i = 0; i < count; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        long target = (long)clock.Elapsed.TotalSeconds;
        await Eventually(() => s.Engine.State.Latest?.Tick.Index >= target, $"엔진 회차 {target}");
    }

    private MeasurementRecord Reread() => RecordReader.Read(RecordReader.FindSegments(Id, [primaryDir.Path, fallbackDir.Path]));

    private sealed class EveryRoundRoutes : IRouteObserver
    {
        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round) =>
            targets.Select(t => new RouteSample(t.Id, (int)(round % 1000) + 1, null, true, null)).ToArray();
    }

    // 지적 1: 꺼낸 회차의 이벤트 쓰기가 막힌 동안 마감해도 관측 + 미처리 = 엔진 회차
    [Fact]
    public async Task SealWhileEventWriteIsBlockedCountsEveryRoundExactlyOnce()
    {
        var clock = new ManualClock(Start);
        var release = new ManualResetEventSlim(false);
        bool blockEvents = false;
        primary.DelayFor = name => { if (Volatile.Read(ref blockEvents) && name.StartsWith("events", StringComparison.Ordinal)) release.Wait(TimeSpan.FromSeconds(10)); return TimeSpan.Zero; };
        var s = MeasurementSession.Start(Options(clock) with
        {
            Routes = new EveryRoundRoutes(), DrainTimeout = TimeSpan.FromMilliseconds(200), SealTimeout = TimeSpan.FromSeconds(5),
        });
        await Rounds(clock, s, 2);
        await Eventually(() => s.Status.Committed == 3, "확정 3");
        Volatile.Write(ref blockEvents, true);
        await Rounds(clock, s, 4);   // 회차 3의 경로 이벤트 쓰기에서 막힌다
        var stopping = s.StopAsync();
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Volatile.Write(ref blockEvents, false);
        release.Set();
        var result = await stopping;
        Assert.False(result.SealFailed);
        Assert.Equal(s.Engine.State.Rounds, result.Summary!.Observed + result.Summary.Unprocessed);
        Assert.Equal(result.Summary.Observed, result.Summary.Committed + result.Summary.Pending + result.Summary.Dropped);
    }

    // 지적 2: 끝 재시도는 줄마다 기한을 본다 (느린 성공 쓰기가 제한을 넘기지 않는다)
    [Fact]
    public async Task EndRetryStopsAtDeadlineEvenWhenEachWriteSucceedsSlowly()
    {
        var clock = new ManualClock(Start);
        bool failing = true, slow = false;
        primary.Next = name => Volatile.Read(ref failing) && name.StartsWith("rounds", StringComparison.Ordinal) ? Fault.DiskFull : Fault.None;
        primary.DelayFor = name => Volatile.Read(ref slow) && name.StartsWith("rounds", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 11);
        // 처리 작업이 회차 11의 (실패한) 쓰기 시도까지 마쳤을 때 단계를 바꾼다 (관측 수만으로는 쓰기 완료를 보장하지 못함)
        await Eventually(() => s.Status.Observed == 12 && primary.Writes("rounds") == 12, "회차 쓰기 시도 12");
        Volatile.Write(ref slow, true);
        Volatile.Write(ref failing, false);
        var watch = Stopwatch.StartNew();
        var result = await s.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"종료가 {watch.Elapsed.TotalMilliseconds:F0} ms 걸림");
        Assert.NotNull(result.Summary);
        Assert.True(result.Unsaved >= 6, "미저장 " + result.Unsaved);
        Assert.Equal(result.Summary!.Observed, result.Summary.Committed + result.Summary.Pending);
    }

    // 지적 2: 끝 쓰기가 멈춰 돌아오지 않으면 요약 없이 마감 실패, 늦게 끝나도 요약을 쓰지 않는다
    [Fact]
    public async Task StuckEndWriteReturnsSealFailureAndNeverWritesSummary()
    {
        var clock = new ManualClock(Start);
        var release = new ManualResetEventSlim(false);
        bool failing = true, stuck = false;
        primary.Next = name => Volatile.Read(ref failing) && name.StartsWith("rounds", StringComparison.Ordinal) ? Fault.DiskFull : Fault.None;
        primary.DelayFor = name => { if (Volatile.Read(ref stuck) && name.StartsWith("rounds", StringComparison.Ordinal)) release.Wait(TimeSpan.FromSeconds(10)); return TimeSpan.Zero; };
        var s = MeasurementSession.Start(Options(clock) with { SealTimeout = TimeSpan.FromMilliseconds(300) });
        await Rounds(clock, s, 2);
        await Eventually(() => s.Status.Pending == 3 && primary.Writes("rounds") == 3, "회차 쓰기 시도 3");
        Volatile.Write(ref stuck, true);
        Volatile.Write(ref failing, false);
        var watch = Stopwatch.StartNew();
        var result = await s.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"종료가 {watch.Elapsed.TotalMilliseconds:F0} ms 걸림");
        Assert.True(result.SealFailed);
        Assert.Null(result.Summary);
        release.Set();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(primaryDir.Path, Id, "summary.json")));
        Assert.Same(result, await s.Completion);
    }

    // 지적 3: 이벤트 자료형 손상은 이벤트 손상으로만 센다
    [Fact]
    public void BadlyTypedEventsAreDamagedEventsOnly()
    {
        var folder = WriteSegment(primaryDir, 0, [Line(0), Line(1)]);
        var good = RecordSchema.EventLine(Id, "clock", 1, 1000, W0, new JsonObject { ["deltaMs"] = 9000 });
        var lines = new[]
        {
            good,
            $$"""{"id":"{{Id}}","type":7,"at":1,"wall":"2026-11-13T00:00:00.000Z"}""",
            $$"""{"id":"{{Id}}","type":"x","at":"늦게","wall":"2026-11-13T00:00:00.000Z"}""",
            $$"""{"id":"{{Id}}","type":"x","at":1,"wall":"어제"}""",
            $$"""{"id":"{{Id}}","type":"x","at":1,"wall":"2026-11-13T00:00:00.000Z","round":"둘"}""",
            $$"""{"id":"{{Id}}","type":"gap","at":1,"wall":"2026-11-13T00:00:00.000Z","reason":5,"fromMs":0,"toMs":1}""",
            $$"""{"id":"{{Id}}","type":"gap","at":1,"wall":"2026-11-13T00:00:00.000Z","reason":"stall","fromMs":"0","toMs":1,"fromWall":"2026-11-13T00:00:00.000Z","toWall":"2026-11-13T00:00:00.000Z"}""",
            "[1,2]",
        };
        TempDir.Write(Path.Combine(folder, "events.jsonl"), string.Join("\n", lines) + "\n");
        var rec = RecordReader.Read([folder]);
        Assert.Equal(7, rec.Damage.DamagedEvents);
        Assert.Equal(0, rec.Damage.Total);
        Assert.Equal(2, rec.Stats!.Rounds);
        Assert.Empty(rec.Gaps);
    }

    // 지적 4: 되돌리기 실패로 이어 쓴 이벤트 조각도 읽는다 (특히 회차 번호가 이어지는 절전 공백)
    [Fact]
    public void EventPartFilesAreRead()
    {
        var folder = WriteSegment(primaryDir, 0, [Line(0), Line(1), Line(2), Line(3)]);
        var gap = new JsonObject
        {
            ["reason"] = "suspend", ["fromMs"] = 1500, ["toMs"] = 1510, ["fromWall"] = RecordSchema.Iso(W0), ["toWall"] = RecordSchema.Iso(W0.AddHours(1)),
            ["lastRoundBefore"] = 1, ["firstRoundAfter"] = 2,
        };
        TempDir.Write(Path.Combine(folder, "events.jsonl"), RecordSchema.EventLine(Id, "clock", 0, 0, W0, new JsonObject { ["deltaMs"] = 6000 }) + "\n{반쪽");
        TempDir.Write(Path.Combine(folder, "events-2.jsonl"), RecordSchema.EventLine(Id, "gap", 2, 1510, W0.AddHours(1), gap) + "\n");
        var rec = RecordReader.Read([folder]);
        Assert.Single(rec.Gaps);
        Assert.Equal(2, rec.Events.Count);
    }

    // 지적 5: 머리를 열 수 없는 조각이 있어도 나머지 조각으로 읽고 누락을 표시한다
    [Fact]
    public void LockedSegmentIsTreatedAsMissing()
    {
        WriteSegment(primaryDir, 0, [Line(0), Line(1)]);
        WriteSegment(fallbackDir, 1, [Line(2), Line(3)]);
        using (new FileStream(Path.Combine(primaryDir.Path, Id, "measurement.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rec = Reread();
            Assert.Equal(RecordStatus.Damaged, rec.Status);
            Assert.Equal([0], rec.MissingSegments);
            Assert.Equal([2L, 3], rec.Rounds.Select(r => r.Round));
        }
        // 회차 파일만 잠겨도 그 파일만 빠진다
        using (new FileStream(Path.Combine(primaryDir.Path, Id, "rounds.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rec = Reread();
            Assert.Equal(1, rec.Damage.UnreadableFiles);
            Assert.Equal([2L, 3], rec.Rounds.Select(r => r.Round));
        }
    }

    // 지적 6: 의미가 틀린 머리는 읽을 수 없는 기록 (예외가 아님)
    [Theory]
    [InlineData("\"segment\": 0", "\"segment\": -2")]
    [InlineData("\"segment\": 0", "\"segment\": 99999999")]
    [InlineData("\"intervalMs\": 1000", "\"intervalMs\": 0")]
    [InlineData("\"id\": \"20261113T000000Z_test01\"", "\"id\": \"\"")]
    [InlineData("\"utcOffsetMinutes\": 540", "\"utcOffsetMinutes\": 99999")]
    [InlineData("\"appVersion\": \"0.5.0\"", "\"appVersion\": \"\"")]          // 2라운드 지적 5
    [InlineData("\"name\": \"A\"", "\"name\": \" \"")]
    public void SemanticallyBrokenHeaderIsUnreadable(string from, string to)
    {
        var folder = primaryDir.Sub("h" + Math.Abs(to.GetHashCode()));
        var json = RecordSchema.HeaderJson(Header(0));
        Assert.Contains(from, json);
        TempDir.Write(Path.Combine(folder, "measurement.json"), json.Replace(from, to));
        Assert.Equal(RecordStatus.Unreadable, RecordReader.Read([folder]).Status);
    }

    [Fact]
    public void HeaderWithoutTargetsIsUnreadable()
    {
        var folder = primaryDir.Sub("empty");
        TempDir.Write(Path.Combine(folder, "measurement.json"), RecordSchema.HeaderJson(Header(0) with { Targets = [] }));
        Assert.Equal(RecordStatus.Unreadable, RecordReader.Read([folder]).Status);
    }

    // 지적 7: 절전 전에 시작해 복귀 뒤에 처리된 회차가 기준 재설정을 먼저 써 버리지 않는다
    [Fact]
    public void LateProcessedPreSuspendSampleDoesNotCauseFalseClockChange()
    {
        var g = new GapTracker(TimeSpan.FromSeconds(1));
        Assert.Null(g.CheckClock(TimeSpan.Zero, W0));
        g.OnSuspend(TimeSpan.FromMilliseconds(1300), W0.AddMilliseconds(1300));
        g.OnResume(TimeSpan.FromMilliseconds(1310), W0.AddHours(1));
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(1000), W0.AddMilliseconds(1000)));             // 절전 전 표본 (늦게 처리)
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(2000), W0.AddHours(1).AddMilliseconds(690)));  // 복귀 뒤 첫 표본: 기준만 다시
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(3000), W0.AddHours(1).AddMilliseconds(1690)));
    }

    // 지적 8: 인터페이스 번호가 바뀌면 앞 인터페이스의 유형을 이어 붙이지 않는다
    [Fact]
    public async Task KindIsNotCarriedAcrossInterfaceChange()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock) with { Routes = new SwitchingRoutes() });
        await Rounds(clock, s, 3);
        await s.StopAsync();
        var routes = Reread().Events.Where(e => (string)e["type"]! == "route" && (string)e["target"]! == "a").ToArray();
        Assert.Equal(2, routes.Length);
        Assert.Equal("wired", (string?)routes[0]["kind"]);
        Assert.Equal(9, (int)routes[1]["interfaceIndex"]!);
        Assert.Null((string?)routes[1]["kind"]);
    }

    private sealed class SwitchingRoutes : IRouteObserver
    {
        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round) =>
            targets.Select(t => round < 2 ? new RouteSample(t.Id, 3, round == 0 ? "wired" : null, true, null) : new RouteSample(t.Id, 9, null, true, null)).ToArray();
    }

    // 지적 9: 계획 시간 뒤 복귀: 공백 끝의 경과 시간과 UTC가 같은 순간(계획 끝)이다
    [Fact]
    public async Task ResumeAfterPlannedEndClosesGapAtPlannedEndOnBothClocks()
    {
        var clock = new ManualClock(Start);
        var power = new FakePower();
        var s = MeasurementSession.Start(Options(clock) with { Power = power });
        await Rounds(clock, s, 9);
        await Eventually(() => s.Status.Observed == 10, "관측 10");
        power.Suspend();
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(100));   // 계획(60초)을 넘겨 복귀
        power.Resume();
        var result = await s.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(EndReason.Completed, result.EndReason);
        var gap = Assert.Single(Reread().Gaps);
        Assert.Equal(60_000, gap.ToMs);
        Assert.Equal(Start.AddSeconds(60), gap.ToWall);
        Assert.Null(gap.FirstRoundAfter);
        Assert.Equal(9, gap.LastRoundBefore);
    }

    private sealed class FakePower : IPowerEvents
    {
        private Action? suspend, resume;
        public IDisposable Subscribe(Action onSuspend, Action onResume) { suspend = onSuspend; resume = onResume; return new Nothing(); }
        public void Suspend() => suspend!();
        public void Resume() => resume!();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    // 지적 10: 프로필 저장·읽기에도 검증을 적용한다
    [Fact]
    public void ProfileStoreRefusesInvalidProfiles()
    {
        var bad = OneMinute with { Targets = [] };
        Assert.Throws<ArgumentException>(() => ProfileStore.Save(primaryDir.Path, new ProfileSet([bad], null)));
        Assert.False(File.Exists(Path.Combine(primaryDir.Path, ProfileStore.FileName)));

        ProfileStore.Save(primaryDir.Path, new ProfileSet([OneMinute], null));
        var path = Path.Combine(primaryDir.Path, ProfileStore.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"timeoutMs\": 800", "\"timeoutMs\": 5"));
        var (set, status, moved) = ProfileStore.Load(primaryDir.Path, W0);
        Assert.Equal(ProfileLoadStatus.Invalid, status);
        Assert.Null(set);
        Assert.True(File.Exists(Path.Combine(primaryDir.Path, moved!)), "원본은 지우지 않고 옮겨 둔다");
    }

    // ================ 2라운드 지적 ================

    // 2-1: 요약 쓰기 자체가 멈췄다가 마감 실패 반환 뒤 풀려도 요약을 게시하지 않는다
    [Fact]
    public async Task StuckSummaryWriteIsNeverPublishedAfterSealFailure()
    {
        var clock = new ManualClock(Start);
        var release = new ManualResetEventSlim(false);
        primary.DelayFor = name => { if (name == "summary.json") release.Wait(TimeSpan.FromSeconds(10)); return TimeSpan.Zero; };
        var s = MeasurementSession.Start(Options(clock) with { SealTimeout = TimeSpan.FromMilliseconds(300) });
        await Rounds(clock, s, 2);
        await Eventually(() => s.Status.Committed == 3, "확정 3");
        var result = await s.StopAsync();
        Assert.True(result.SealFailed);
        Assert.Null(result.Summary);
        release.Set();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(primaryDir.Path, Id, "summary.json")), "포기한 뒤 풀린 요약 쓰기는 게시하지 않는다");
        Assert.False(File.Exists(Path.Combine(primaryDir.Path, Id, "summary.json.tmp")), "임시 요약도 남기지 않는다");
        Assert.Same(result, await s.Completion);
        Assert.Equal(EndReason.Interrupted, Reread().EndReason);
    }

    // 2-2: 절전 알림 뒤(실제로 잠들기 전) 시작한 표본이 있어도 복귀 뒤 첫 표본에서 기준을 다시 잡는다
    [Fact]
    public void SampleTakenWhileSuspendingDoesNotLeaveFalseClockChangeAfterResume()
    {
        var g = new GapTracker(TimeSpan.FromSeconds(1));
        Assert.Null(g.CheckClock(TimeSpan.Zero, W0));
        g.OnSuspend(TimeSpan.FromMilliseconds(900), W0.AddMilliseconds(900));
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(1000), W0.AddMilliseconds(1000)));             // 잠들기 전 표본
        g.OnResume(TimeSpan.FromMilliseconds(1010), W0.AddHours(1));
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(2000), W0.AddHours(1).AddMilliseconds(990)));  // 복귀 경계를 지난 첫 표본
        Assert.Null(g.CheckClock(TimeSpan.FromMilliseconds(3000), W0.AddHours(1).AddMilliseconds(1990)));
        Assert.Equal(-20_000, g.CheckClock(TimeSpan.FromMilliseconds(4000), W0.AddHours(1).AddMilliseconds(2990 - 20_000)));   // 진짜 변경은 그대로 잡는다
    }

    // 2-3: 마지막 조각의 머리를 못 읽으면 누락 번호를 몰라도 불완전 복구로 표시한다
    [Fact]
    public void UnreadableLastSegmentIsFlagged()
    {
        WriteSegment(primaryDir, 0, [Line(0), Line(1)]);
        WriteSegment(fallbackDir, 1, [Line(2), Line(3)]);
        using (new FileStream(Path.Combine(fallbackDir.Path, Id, "measurement.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rec = Reread();
            Assert.Equal(RecordStatus.Damaged, rec.Status);
            Assert.Contains("열지 못한 조각 1개", rec.MissingNotice);
            Assert.Equal([0L, 1], rec.Rounds.Select(r => r.Round));
        }
        // 머리 파일이 아예 없는 조각 폴더도 같다
        File.Delete(Path.Combine(fallbackDir.Path, Id, "measurement.json"));
        Assert.Contains("열지 못한 조각 1개", Reread().MissingNotice);
    }

    // 2-4: 마감으로 미처리가 된 회차는 공백 상태를 건드리지 않는다: 닫히지 않은 절전 공백은 끝에서 닫혀 남는다
    [Fact]
    public async Task SuspendGapSurvivesWhenRoundsAfterResumeAreSealedAsUnprocessed()
    {
        var clock = new ManualClock(Start);
        var release = new ManualResetEventSlim(false);
        var power = new FakePower();
        bool block = false;
        primary.DelayFor = name => { if (Volatile.Read(ref block) && name.StartsWith("rounds", StringComparison.Ordinal)) release.Wait(TimeSpan.FromSeconds(10)); return TimeSpan.Zero; };
        var s = MeasurementSession.Start(Options(clock) with
        {
            Power = power, DrainTimeout = TimeSpan.FromMilliseconds(200), SealTimeout = TimeSpan.FromSeconds(5),
        });
        await Rounds(clock, s, 2);
        await Eventually(() => s.Status.Committed == 3, "확정 3");
        Volatile.Write(ref block, true);
        await Rounds(clock, s, 1);               // 회차 3: 쓰기에서 막힘
        await Eventually(() => primary.Writes("rounds") == 4, "회차 3 쓰기 진입");
        power.Suspend();
        power.Resume();
        await Rounds(clock, s, 3);               // 회차 4~6: 큐에서 기다리다 마감되면 미처리
        var stopping = s.StopAsync();
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Volatile.Write(ref block, false);
        release.Set();
        var result = await stopping;
        Assert.False(result.SealFailed);
        Assert.Equal(3, result.Summary!.Unprocessed);
        var gap = Assert.Single(Reread().Gaps);
        Assert.Equal(GapReason.Suspend, gap.Reason);
        Assert.Null(gap.FirstRoundAfter);      // 복귀 뒤 회차는 관측되지 않았다
        Assert.Equal(3, gap.LastRoundBefore);
    }

    // ---------------- 도우미 ----------------

    private static RecordHeader Header(int segment) => new(Id, segment, "0.5.0", W0, 540,
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        ], 1000, 800, 600_000, "primary");

    private static string Line(long r) => RecordSchema.RoundLine(Id, new RoundData(r, r * 1000, r * 1000, W0.AddSeconds(r), 0, false,
        [new TargetResult("a", ProbeOutcome.Success, 1, null), new TargetResult("b", ProbeOutcome.Lost, null, null)]));

    private static string WriteSegment(TempDir dir, int segment, IEnumerable<string> lines)
    {
        var folder = dir.Sub(Id);
        TempDir.Write(Path.Combine(folder, "measurement.json"), RecordSchema.HeaderJson(Header(segment)));
        TempDir.Write(Path.Combine(folder, "rounds.jsonl"), string.Concat(lines.Select(l => l + "\n")));
        return folder;
    }
}
