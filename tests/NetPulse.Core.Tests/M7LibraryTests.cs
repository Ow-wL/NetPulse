using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

/// <summary>기록 목록·분석 큐·보유 범위·다른 위치 조각 (M7 설계 3.1·3.7, 구현 중 결정 3·5·6).</summary>
public sealed class M7LibraryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 4, 0, 0, TimeSpan.Zero);
    private static readonly Target Router = new("r", "R", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp);
    private static readonly Target ExtA = new("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp);
    private static readonly Target ExtB = new("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "Y", ProbeMethod.Icmp);
    private static readonly Target[] Three = [Router, ExtA, ExtB];
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private static string Id(int hour, string tail) => $"20261113T{hour:00}0000Z_{tail}";

    /// <summary>머리·요약·회차(60개)·증상 표시 하나를 쓴 기록 폴더.</summary>
    private static string Make(string root, string id, int hour, int segment = 0, bool summary = true, int timeoutMs = 800, string? folderName = null)
    {
        var folder = Path.Combine(root, folderName ?? (segment == 0 ? id : $"{id}~{segment + 1}"));
        Directory.CreateDirectory(folder);
        var started = Start.AddHours(hour);
        File.WriteAllText(Path.Combine(folder, "measurement.json"),
            RecordSchema.HeaderJson(new RecordHeader(id, segment, "1.0", started, 540, Three, 1000, timeoutMs, 600_000, "primary", ExpectedConnection.Wired)));
        var lines = Enumerable.Range(0, 60).Select(r => RecordSchema.RoundLine(id, new RoundData(r, r * 1000, r * 1000 + 3, started.AddSeconds(r), 0, false,
            [new("r", ProbeOutcome.Success, 2, null), new("a", ProbeOutcome.Success, 20, null), new("b", ProbeOutcome.Success, 24, null)], r * 1000 + 50)));
        File.WriteAllLines(Path.Combine(folder, "rounds.jsonl"), lines);
        File.WriteAllText(Path.Combine(folder, "events.jsonl"),
            RecordSchema.EventLine(id, SymptomMarks.MarkType, 10, 10_000, started.AddSeconds(10), new JsonObject { ["mark"] = "k1" }) + "\n");
        if (summary)
            File.WriteAllText(Path.Combine(folder, "summary.json"), RecordSchema.SummaryJson(
                new RecordSummary(id, EndReason.Stopped, started.AddMinutes(1), 60, 60, 0, 0, 0, 59, false, null, 0, 60_000)));
        return folder;
    }

    private static async Task Settled(RecordLibrary lib)
    {
        await lib.AnalyzedAsync(lib.Items.Select(i => i.Key).ToArray()).WaitAsync(Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EveryRefreshRequestGetsAListPassThatStartsAfterIt()
    {
        // 갱신 루프가 끝내기로 정한 뒤 작업이 완료되기 전에 온 요청이 합쳐지기만 하고 사라지면, 측정 종료 갱신을 놓쳐
        // 목록에 "측정 중"이 남는다 (G4 M7 1라운드 a4). 그 틈에서 요청을 넣고, 그 요청 뒤에 시작한 읽기가 있는지 본다
        var p = dir.Sub("roots");
        long requested = 0, seen = 0;
        var lib = new RecordLibrary(() => { Interlocked.Exchange(ref seen, Interlocked.Read(ref requested)); return [p]; });
        var ct = TestContext.Current.CancellationToken;
        Task? late = null;
        long lateStamp = 0;
        lib.AfterLastPassForTest = () =>
        {
            if (late is not null) return;
            lateStamp = Interlocked.Increment(ref requested);
            late = lib.RefreshAsync(null);
        };
        long first = Interlocked.Increment(ref requested);
        await lib.RefreshAsync(null).WaitAsync(Wait, ct);
        Assert.True(Interlocked.Read(ref seen) >= first);
        Assert.NotNull(late);
        await late!.WaitAsync(Wait, ct);
        Assert.True(Interlocked.Read(ref seen) >= lateStamp, "끝내기로 정한 뒤에 온 요청 뒤에 시작한 목록 읽기가 없습니다");
    }

    [Fact]
    public async Task ListHasNoLimitIsNewestFirstAndKeepsUnreadableAndRunningRecords()
    {
        var p = dir.Sub("p");
        for (int h = 0; h < 6; h++) Make(p, Id(h, $"aaaaa{h}"), h);
        Directory.CreateDirectory(Path.Combine(p, Id(7, "broken")));   // 머리 없음
        var running = Id(8, "runnin");
        Make(p, running, 8, summary: false);
        var lib = new RecordLibrary(() => [p]);
        await lib.RefreshAsync(running).WaitAsync(Wait, TestContext.Current.CancellationToken);
        await Settled(lib);
        Assert.Equal(8, lib.Items.Count);
        Assert.Equal(running, lib.Items[0].Entry.Id);
        Assert.False(lib.Items[0].Summary.Analyzing);
        Assert.Null(lib.Items[0].Summary.Badge);                    // 측정 중은 분석하지 않음
        Assert.Equal(RecordIntegrity.Unreadable, lib.Items.Single(i => i.Entry.Id == Id(7, "broken")).Entry.Integrity);   // 읽지 못한 폴더도 목록에
        Assert.All(lib.Items.Where(i => i.Entry.Id.Contains("_aaaaa", StringComparison.Ordinal)), i => Assert.NotNull(i.Summary.Badge));
        Assert.Equal(lib.Items.Select(i => i.Entry.StartedAt).OrderByDescending(x => x), lib.Items.Select(i => i.Entry.StartedAt));   // 최신순
        Assert.Null(await lib.OpenAsync(lib.Items[0].Key));         // 측정 중은 열지 않음 (측정 화면으로)
    }

    [Fact]
    public async Task ChangingAFileChangesTheStampAndTheSummaryIsRedone()
    {
        var p = dir.Sub("p");
        var id = Id(1, "bbbbbb");
        var folder = Make(p, id, 1);
        var lib = new RecordLibrary(() => [p]);
        await lib.RefreshAsync(null);
        await Settled(lib);
        var before = lib.Items.Single();
        Assert.Null(before.Summary.FirstMemo);
        RecordNotes.AppendMemo(folder, id, "k1", 10_000, Start.AddHours(1).AddMinutes(5), "끝난 뒤 고친 메모");
        await lib.RefreshAsync(null);
        await Settled(lib);
        var after = lib.Items.Single();
        Assert.NotEqual(before.Stamp, after.Stamp);
        Assert.Equal("끝난 뒤 고친 메모", after.Summary.FirstMemo);
    }

    [Fact]
    public async Task OpenedRecordsAreHeldAtMostThreeAndPinnedOnesStay()
    {
        var p = dir.Sub("p");
        for (int h = 0; h < 5; h++) Make(p, Id(h, $"ccccc{h}"), h);
        var lib = new RecordLibrary(() => [p]);
        await lib.RefreshAsync(null);
        var keys = lib.Items.Select(i => i.Key).ToArray();
        lib.SetPinned([keys[0], keys[1]]);   // 열린 비교 둘
        foreach (var k in keys)
            Assert.NotNull(await lib.OpenAsync(k).WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(RecordLibrary.HoldLimit, lib.HeldCount);
        // 핀은 남아 있어 바로 (같은 인스턴스)
        var again = await lib.OpenAsync(keys[0]);
        Assert.Same(again, await lib.OpenAsync(keys[0]));
    }

    [Fact]
    public async Task OpenRequestsGoBeforeTheRestOfTheList()
    {
        var p = dir.Sub("p");
        for (int h = 0; h < 12; h++) Make(p, Id(h, $"ddddd{h:x}"), h);
        var lib = new RecordLibrary(() => [p]);
        // 순서를 고정한다: 첫 목록 분석이 시작한 자리에서 멈춘 동안 열기를 요청하고, 열기 뒤의 목록 분석은 확인이 끝날 때까지
        // 멈춘다. 작은 기록 12개의 분석이 열기 요청보다 먼저 다 끝나 실패하던 시간 경쟁 (M8 PR CI)
        var ct = TestContext.Current.CancellationToken;
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseRest = new ManualResetEventSlim();
        string? last = null, first = null;
        int calls = 0;
        lib.BeforeAnalyzeForTest = key =>
        {
            if (Interlocked.Increment(ref calls) == 1) { Volatile.Write(ref first, key); firstStarted.Set(); releaseFirst.Wait(Wait); }
            else if (key != Volatile.Read(ref last)) releaseRest.Wait(Wait);
        };
        try
        {
            await lib.RefreshAsync(null);
            Assert.True(firstStarted.Wait(Wait, ct), "목록 분석이 시작하지 않음");
            Volatile.Write(ref last, lib.Items[^1].Key);
            Assert.NotNull(first);
            Assert.NotEqual(first, last);
            var open = lib.OpenAsync(last!);
            releaseFirst.Set();
            var opened = await open.WaitAsync(Wait, ct);
            Assert.NotNull(opened);
            // 열기 요청을 받은 뒤에는 진행 중이던 목록 분석 하나만 먼저 끝나고, 그다음은 열기다
            var done = lib.Items.Where(i => !i.Summary.Analyzing).Select(i => i.Key).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { first!, last! }.Order(StringComparer.Ordinal), done);
        }
        finally
        {
            releaseFirst.Set();
            releaseRest.Set();
            // 실패·취소·시간 초과로 끝나도 작업자가 멈추기를 기다린 뒤에 이벤트를 폐기하고 임시 폴더를 지운다 (G4 M8 9라운드 사소).
            // 이미 취소된 시험 토큰은 쓰지 않는다: 정리만은 끝까지 (한도 안에서)
            try { await lib.AnalyzedAsync(lib.Items.Select(i => i.Key).ToArray()).WaitAsync(Wait, CancellationToken.None); }
            catch (TimeoutException) { }
        }
        Assert.All(lib.Items, i => Assert.False(i.Summary.Analyzing));
    }

    [Fact]
    public async Task ExternalCopyOfTheSameIdIsASeparateItem()
    {
        var p = dir.Sub("p");
        var ext = dir.Sub("usb");
        var id = Id(2, "eeeeee");
        Make(p, id, 2);
        var copy = Make(ext, id, 2);
        var lib = new RecordLibrary(() => [p]);
        var (key, error, _) = lib.AddExternal(copy);
        Assert.Null(error);
        await lib.RefreshAsync(null);
        Assert.Equal(2, lib.Items.Count(i => i.Entry.Id == id));
        Assert.True(lib.Items.Single(i => i.Key == key).External);
        Assert.Contains(copy, lib.ExternalFolders);
    }

    [Fact]
    public void LocatorDropsSegmentsWithAnotherHeaderAndPrefersTheSelectedSide()
    {
        var p = dir.Sub("known");
        var usb = dir.Sub("usb");
        var id = Id(3, "ffffff");
        var picked = Make(usb, id, 3, segment: 0);
        Make(usb, id, 3, segment: 1);                         // 같은 상위 폴더의 이웃 조각
        Make(p, id, 3, segment: 1, folderName: id + "~2");    // 알려진 위치의 같은 번호 → 고른 쪽이 먼저
        Make(p, id, 3, segment: 2, timeoutMs: 2000);          // 머리가 다름 → 뺀다
        var found = RecordLocator.FindExternal(picked, [p]);
        Assert.Null(found.Error);
        Assert.Equal(2, found.Segments.Count);
        Assert.All(found.Segments, s => Assert.StartsWith(usb, s));
        Assert.Contains(found.Notices, n => n.Contains("머리가 다른 조각 1개"));
        Assert.Contains(found.Notices, n => n.Contains("같은 조각이 여러 곳"));
        Assert.Contains(found.Notices, n => n.Contains("찾은 조각 2개만"));
        Assert.DoesNotContain(found.Notices, n => n.Contains(usb) || n.Contains(p));   // 경로를 문장에 넣지 않는다

        var notRecord = dir.Sub("photos");
        Assert.NotNull(RecordLocator.FindExternal(notRecord, [p]).Error);
    }
}
