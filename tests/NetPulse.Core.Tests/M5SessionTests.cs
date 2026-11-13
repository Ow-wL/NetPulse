using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M5 설계 3.1·3.2: 증상 표시·메모, 끝난 뒤 노트, 실시간 전달.</summary>
public sealed class M5SessionTests : IDisposable
{
    private const string Id = "20261113T000000Z_m5sess";
    private readonly TempDir dir = new();
    private readonly FaultyRoot primary;
    private readonly FaultyRoot fallback;

    public M5SessionTests()
    {
        primary = new FaultyRoot(new DiskRecordRoot(dir.Sub("p"), "primary"), "VOL-A");
        fallback = new FaultyRoot(new DiskRecordRoot(dir.Sub("f"), "fallback"), "VOL-A");
    }

    public void Dispose() => dir.Dispose();

    private static readonly Profile FiveMinutes = new("p", "시험",
        [
            new Target("a", "A", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
            new Target("b", "B", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
            new Target("c", "C", IPAddress.Parse("203.0.113.1"), TargetRole.External, "Y", ProbeMethod.Icmp),
        ], Profile.DefaultTimeout, TimeSpan.FromMinutes(5), ExpectedConnection.Wired);

    private sealed class Routes : IRouteObserver
    {
        public IReadOnlyList<RouteSample> Observe(IReadOnlyList<ProbeTarget> targets, long round) =>
            targets.Select(t => new RouteSample(t.Id, 7, round % 30 == 0 ? "wired" : null, true, null)).ToArray();
    }

    private SessionOptions Options(IClock clock, int pendingLimit = 300) => new()
    {
        Profile = FiveMinutes, Clock = clock, ProbeFor = _ => new RecordingProbe(), Primary = primary, Fallback = fallback, NewId = _ => Id,
        Routes = new Routes(), RetryTimeout = TimeSpan.FromMilliseconds(300), PendingLimit = pendingLimit,
    };

    private static async Task Rounds(ManualClock clock, MeasurementSession s, int count)
    {
        for (int i = 0; i < count; i++) await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));
        long target = (long)clock.Elapsed.TotalSeconds;
        await Eventually(() => s.Status.Observed >= target + 1, "관측");
    }

    private MeasurementRecord Reread() => RecordReader.Read(RecordReader.FindSegments(Id, [primary.Path!, fallback.Path!]));

    // ---------------- 증상 표시 ----------------

    [Fact]
    public async Task MarkFixesPressTimeAndRoundAndIsWrittenByTheSession()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 3);
        clock.Advance(TimeSpan.FromMilliseconds(400));                 // 3.4초에 누름
        var ticket = s.MarkSymptom("  화상회의\n중   끊김 ");
        Assert.NotNull(ticket);
        Assert.Equal(("s1", 3400L, 3L), (ticket!.Mark, ticket.AtMs, ticket.Round));
        Assert.Equal(EventWriteState.Saved, await ticket.Saved.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var second = s.MarkSymptom(null);
        Assert.Equal("s2", second!.Mark);
        await second.Saved.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await s.StopAsync();
        Assert.Null(s.MarkSymptom("끝난 뒤"));                         // 측정이 돌 때만

        var (marks, ignored) = SymptomMarks.FromEvents(Reread().Events);
        Assert.Equal(0, ignored);
        Assert.Equal(2, marks.Count);
        Assert.Equal(("s1", 3400L, (long?)3, "화상회의 중 끊김"), (marks[0].Mark, marks[0].AtMs, marks[0].Round, marks[0].Memo));
        Assert.Null(marks[1].Memo);
    }

    [Fact]
    public async Task LastMemoWinsAndUnknownMarksAreRefused()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 2);
        var t = s.MarkSymptom("처음");
        await t!.Saved.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await s.EditSymptomMemo("s1", "고친 메모")!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await s.EditSymptomMemo("s1", "다시 고친 메모")!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(s.EditSymptomMemo("s9", "없는 표시"));
        await s.StopAsync();
        Assert.Null(s.EditSymptomMemo("s1", "끝난 뒤"));                 // 끝난 뒤는 노트로

        var mark = Assert.Single(SymptomMarks.FromEvents(Reread().Events).Marks);
        Assert.Equal("다시 고친 메모", mark.Memo);
        Assert.NotNull(mark.EditedAt);
        Assert.Equal(t.AtMs, Reread().Events.Last(e => (string?)e["type"] == SymptomMarks.MemoType)["at"]!.GetValue<long>());
    }

    [Fact]
    public async Task FailedWriteIsPendingThenSavedThroughLiveStateItems()
    {
        var clock = new ManualClock(Start);
        bool failEvents = false;
        primary.Next = n => failEvents && n.StartsWith("events", StringComparison.Ordinal) ? Fault.FlushFails : Fault.None;
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 2);
        var start = s.Live.AnalysisInputs().At;
        failEvents = true;
        var t = s.MarkSymptom("저장 지연");
        Assert.Equal(EventWriteState.Pending, await t!.Saved.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        failEvents = false;
        await Rounds(clock, s, 1);                                    // 다음 회차에서 재시도
        var delta = s.Live.Read(start);
        var symptom = Assert.Single(delta.Items.OfType<EventItem>(), i => i.Event.Type == SymptomMarks.MarkType);
        Assert.Equal(EventWriteState.Pending, symptom.Event.State);
        var state = Assert.Single(delta.Items.OfType<StateItem>(), i => i.EventId == symptom.Event.EventId);
        Assert.Equal(EventWriteState.Saved, state.State);
        await s.StopAsync();
        Assert.Single(SymptomMarks.FromEvents(Reread().Events).Marks);
    }

    [Fact]
    public async Task EventsOverTheLimitAreLostAndReportedAsSuch()
    {
        var clock = new ManualClock(Start);
        primary.Next = n => n.StartsWith("events", StringComparison.Ordinal) ? Fault.FlushFails : Fault.None;
        var s = MeasurementSession.Start(Options(clock, pendingLimit: 2));
        await Rounds(clock, s, 1);
        var start = s.Live.AnalysisInputs().At;
        var first = s.MarkSymptom("1");
        await first!.Saved.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (int i = 0; i < 3; i++) await s.MarkSymptom($"{i + 2}")!.Saved.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var delta = s.Live.Read(start);
        var firstEvent = delta.Items.OfType<EventItem>().First(i => i.Event.Json.Contains("\"s1\"", StringComparison.Ordinal));
        Assert.Contains(delta.Items.OfType<StateItem>(), i => i.EventId == firstEvent.Event.EventId && i.State == EventWriteState.Lost);
        await s.StopAsync();
    }

    [Fact]
    public async Task MarksAcceptedBeforeStopAreWrittenBeforeTheSummary()
    {
        // 끝내기는 입력 통로를 닫고, 이미 들어온 표시를 요약 전에 쓴다. 닫힌 뒤는 거부.
        var clock = new ManualClock(Start);
        var hold = new ManualResetEventSlim(false);
        var inHook = new ManualResetEventSlim(false);
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 1);
        s.TestHookAfterDequeue = r => { if (r == 2) { inHook.Set(); hold.Wait(TimeSpan.FromSeconds(10)); } };
        await AdvanceWhenWaiting(clock, TimeSpan.FromSeconds(1));       // 회차 2: 처리 작업이 막힘
        Assert.True(inHook.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "처리 작업이 막힘");
        var a = s.MarkSymptom("막힌 동안 1");
        var b = s.MarkSymptom("막힌 동안 2");
        var stopping = s.StopAsync();
        Assert.Null(s.MarkSymptom("닫힌 뒤"));
        hold.Set();
        var result = await stopping;
        Assert.Equal(EventWriteState.Saved, await a!.Saved);
        Assert.Equal(EventWriteState.Saved, await b!.Saved);
        Assert.NotNull(result.Summary);
        Assert.Equal(2, SymptomMarks.FromEvents(Reread().Events).Marks.Count);
    }

    // ---------------- 끝난 뒤 노트 ----------------

    [Fact]
    public async Task NotesAfterTheEndAreReadAfterEventsAndBrokenLinesAreCounted()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        await Rounds(clock, s, 2);
        var t = s.MarkSymptom("측정 중 메모");
        await t!.Saved;
        await s.StopAsync();
        var folder = RecordReader.FindSegments(Id, [primary.Path!, fallback.Path!]).Single();
        RecordNotes.AppendMemo(folder, Id, "s1", t.AtMs, Start.AddMinutes(10), "끝난 뒤 고친 메모");
        File.AppendAllText(Path.Combine(folder, RecordNotes.FileName), "{\"깨진 줄");                 // 잘린 줄
        RecordNotes.AppendMemo(folder, Id, "s1", t.AtMs, Start.AddMinutes(11), "마지막 메모");     // 잘린 줄 뒤에도 새 줄로 이어 쓴다

        var record = Reread();
        Assert.Equal(1, record.DamagedNotes);
        Assert.Equal(RecordStatus.Ok, record.Status);                 // 노트 손상은 기록 본문 손상이 아니다
        var mark = Assert.Single(SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks);
        Assert.Equal("마지막 메모", mark.Memo);
        Assert.Equal(Start.AddMinutes(11), mark.EditedAt);
    }

    [Theory]
    [InlineData("  a \r\n b\tc  ", "a b c", false)]
    [InlineData("", null, false)]
    [InlineData("   ", null, false)]
    [InlineData(null, null, false)]
    public void MemoIsCleaned(string? input, string? expected, bool truncated) =>
        Assert.Equal((expected, truncated), SymptomMarks.Clean(input));

    [Fact]
    public void LongMemoIsCutAtTwoHundredVisibleCharacters()
    {
        var (text, truncated) = SymptomMarks.Clean(string.Concat(Enumerable.Repeat("가👍", 150)));
        Assert.True(truncated);
        Assert.Equal(200, new System.Globalization.StringInfo(text!).LengthInTextElements);
        Assert.False(char.IsHighSurrogate(text![^1]));               // 이모지를 반쪽으로 자르지 않는다
    }

    [Fact]
    public void DuplicateMarksAndUnknownMemosAreIgnored()
    {
        JsonObject E(string json) => JsonNode.Parse(json)!.AsObject();
        var events = new[]
        {
            E("""{"id":"x","type":"symptom","at":1000,"wall":"2026-11-13T09:00:01+09:00","round":1,"mark":"s1","memo":"a"}"""),
            E("""{"id":"x","type":"symptom","at":2000,"wall":"2026-11-13T09:00:02+09:00","round":2,"mark":"s1"}"""),
            E("""{"id":"x","type":"symptomMemo","at":1000,"wall":"2026-11-13T09:00:03+09:00","mark":"s7","memo":"b"}"""),
            E("""{"id":"x","type":"symptomMemo","at":1000,"wall":"2026-11-13T09:00:04+09:00","mark":"s1","memo":null}"""),
        };
        var (marks, ignored) = SymptomMarks.FromEvents(events);
        Assert.Equal(2, ignored);
        var m = Assert.Single(marks);
        Assert.Null(m.Memo);                                          // 빈 메모 = 지움
    }

    // ---------------- 실시간 전달 ----------------

    [Fact]
    public async Task RoundItemsArriveWithTheirRouteEventsInRecordOrder()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        var first = s.Live.Read(null);
        Assert.True(first.Resync);                                    // 처음은 사본으로 시작
        var at = s.Live.AnalysisInputs().At;
        await Rounds(clock, s, 4);
        var delta = s.Live.Read(at);
        Assert.False(delta.Resync);
        var rounds = delta.Items.OfType<RoundItem>().ToArray();
        Assert.Equal(s.ObservedRounds.Select(r => r.Round), rounds.Select(r => r.Round.Round));
        Assert.Equal(s.ObservedRounds, rounds.Select(r => r.Round));  // 같은 값
        var r0 = rounds.Single(r => r.Round.Round == 0);
        Assert.Equal(3, r0.Related.Count(e => e.Type == "route"));    // 첫 회차의 경로 이벤트가 그 회차와 함께
        Assert.True(delta.Items.Zip(delta.Items.Skip(1)).All(p => p.Second.Seq == p.First.Seq + 1));
        var again = s.Live.Read(delta.Next);
        Assert.Empty(again.Items);
        await s.StopAsync();
    }

    [Fact]
    public async Task CursorOfAnotherSessionIsRejectedAndTrimmedCursorAsksToResync()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        s.LiveBufferLimit = 4;
        var at = s.Live.AnalysisInputs().At;
        await Rounds(clock, s, 10);
        Assert.True(s.Live.Read(at).Resync);                          // 버린 범위
        Assert.Throws<ArgumentException>(() => s.Live.Read(new LiveCursor("other", at.Seq)));
        var inputs = s.Live.AnalysisInputs();
        Assert.Equal(s.ObservedRounds.Count, inputs.Rounds.Count);   // 다시 만들 사본은 버리지 않은 전체
        Assert.False(s.Live.Read(inputs.At).Resync);
        await s.StopAsync();
    }

    [Fact]
    public async Task AnalysisInputsKeepEveryEventEvenWhenTheBufferIsTrimmed()
    {
        var clock = new ManualClock(Start);
        var s = MeasurementSession.Start(Options(clock));
        s.LiveBufferLimit = 3;
        await Rounds(clock, s, 40);                                   // 회차 30에서 경로 종류가 다시 쓰여도 앞 경로는 남는다
        var before = s.Live.AnalysisInputs();
        await s.StopAsync();
        var record = Reread();
        // 사본의 이벤트 = 기록 파일의 이벤트 (같은 순서, 같은 내용)
        var recordEvents = record.Events.Select(e => e.ToJsonString()).ToArray();
        Assert.Equal(before.Events.Select(e => e.ToJsonString()), recordEvents.Take(before.Events.Count));
        var provisional = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(before.Header, before.Rounds, before.Gaps, before.Events, null, true, before.ElapsedMs, before.Links);
        var fromFile = NetPulse.Core.Analysis.MeasurementAnalysis.Analyze(record.Header!, record.Rounds.Take(before.Rounds.Count).ToArray(), record.Gaps,
            record.Events.Take(before.Events.Count).ToArray(), null, true, before.ElapsedMs);
        Assert.Equal(fromFile.Rounds.Select(r => r.Verdict), provisional.Rounds.Select(r => r.Verdict));
    }
}
