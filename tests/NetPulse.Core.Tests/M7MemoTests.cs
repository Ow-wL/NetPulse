using System.Text.Json.Nodes;
using NetPulse.Core.Demo;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>기록 화면의 메모 고치기 (M7 설계 3.5 [1-10], 구현 중 결정 4).</summary>
public sealed class M7MemoTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 4, 0, 0, TimeSpan.Zero);
    private const string Id = "20261113T040000Z_memo01";
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private string Folder(bool withSummary = true, string? root = null)
    {
        var f = Path.Combine(root ?? dir.Path, Id);
        Directory.CreateDirectory(f);
        File.WriteAllText(Path.Combine(f, "events.jsonl"),
            RecordSchema.EventLine(Id, SymptomMarks.MarkType, 10, 10_000, Start.AddSeconds(10), new JsonObject { ["mark"] = "k1" }) + "\n");
        if (withSummary) File.WriteAllText(Path.Combine(f, "summary.json"), "{}");
        return f;
    }

    private static string? MemoOf(string folder)
    {
        var events = File.ReadAllLines(Path.Combine(folder, "events.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject());
        var notesPath = Path.Combine(folder, RecordNotes.FileName);
        var notes = File.Exists(notesPath) ? File.ReadAllLines(notesPath).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()) : [];
        return SymptomMarks.FromEvents(events, notes).Marks.Single().Memo;
    }

    private static RecordMemos New(out List<(string Id, string Mark)> saved, NotesWriter? writer = null)
    {
        var memos = new RecordMemos(writer ?? new NotesWriter { RetryDelays = [TimeSpan.FromMilliseconds(10)] }, () => Start.AddHours(1));
        var list = new List<(string, string)>();
        memos.Saved += (_, id, mark) => { lock (list) list.Add((id, mark)); };
        saved = list;
        return memos;
    }

    [Fact]
    public async Task SavingWritesTheNoteAndTellsWhoeverHoldsAnOldDraft()
    {
        var folder = Folder();
        var memos = New(out var saved);
        var r = await memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "  끝난 뒤\n고친 메모 ");
        Assert.Equal((EventWriteState.Saved, true, false, "끝난 뒤 고친 메모"), (r.State, r.Latest, r.Truncated, r.Text));
        Assert.Equal("끝난 뒤 고친 메모", MemoOf(folder));
        Assert.Equal([(Id, "k1")], saved);

        var empty = await memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "   ");
        Assert.Null(empty.Text);
        Assert.Null(MemoOf(folder));   // 빈 글 = 지움
        var longText = new string('가', SymptomMarks.MaxMemoLength + 5);
        Assert.True((await memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, longText)).Truncated);
    }

    [Fact]
    public async Task OnlyTheNewestRequestIsLatestAndTheFileEndsWithIt()
    {
        var folder = Folder();
        var memos = New(out _);
        var first = memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "첫째");
        var second = memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "둘째");
        var results = await Task.WhenAll(first, second);
        Assert.False(results[0].Latest);   // 오래된 완료는 화면에 반영하지 않는다
        Assert.True(results[1].Latest);
        Assert.Equal("둘째", MemoOf(folder));   // 직렬 작업이라 마지막 요청이 마지막 줄
    }

    [Fact]
    public async Task ASlowSavingHandlerDoesNotLetTheLaterRequestQueueFirst()
    {
        // 앱처럼 저장 시작 알림을 구독한 채: 앞 요청 A의 알림 처리가 멈춘 동안 B를 저장해도 B가 A보다 먼저 줄 서지 않아
        // 파일에는 B가 마지막으로 남는다 (G4 M7 2라운드 1)
        var folder = Folder();
        var memos = New(out _);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        memos.Saving += (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.SetResult();
            release.Task.Wait(TestContext.Current.CancellationToken);
        };
        var a = memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "A 옛 글");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var b = memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "B 새 글");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(b.IsCompleted);   // A가 줄 서기 전에는 B도 줄 서지 않는다
        release.SetResult();
        var results = await Task.WhenAll(a, b);
        Assert.False(results[0].Latest);
        Assert.True(results[1].Latest);
        Assert.Equal("B 새 글", MemoOf(folder));
    }

    [Fact]
    public async Task FailureKeepsTheTextForLaterAndNotesGoToTheSummarySegment()
    {
        var memos = New(out var saved);
        var missing = Path.Combine(dir.Path, "gone", Id);
        var r = await memos.SaveAsync("ext:0:x", [missing], Id, "k1", 10_000, "못 쓴 메모");
        Assert.Equal(EventWriteState.Lost, r.State);
        Assert.Empty(saved);
        Assert.True(memos.TryFailedDraft("ext:0:x", "k1", out var kept));
        Assert.Equal("못 쓴 메모", kept);

        var a = dir.Sub("seg-a");
        var b = dir.Sub("seg-b");
        File.WriteAllText(Path.Combine(a, "summary.json"), "{}");
        Assert.Equal(a, RecordMemos.NotesFolder([a, b]));   // 요약이 있는 조각
        Assert.Equal(b, RecordMemos.NotesFolder([dir.Sub("seg-c"), b]));   // 없으면 마지막 조각
    }

    /// <summary>데모 세계의 호스트와 같은 직렬 쓰기를 쓰는 기록 화면 메모 (앱과 같은 연결). 기록 폴더는 호스트가 아는 곳.</summary>
    private (MeasurementHost Host, RecordMemos Memos, string Folder) Hosted()
    {
        var data = dir.Sub("u");
        var scenario = Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json")));
        var world = new DemoAppWorld(new DemoWorld(scenario, new DemoTimeBase(Start, 600)), data, new DemoRunRules(), dir.Sub("fb"));
        var host = new MeasurementHost(world, data, new HostOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        var memos = new RecordMemos(host.Notes, () => Start);
        memos.Saving += host.MemoSavingElsewhere;
        memos.Saved += host.MemoSavedElsewhere;
        return (host, memos, Folder(root: Path.Combine(data, "records")));
    }

    private static int MemoLines(string folder)
    {
        var path = Path.Combine(folder, RecordNotes.FileName);
        return File.Exists(path) ? File.ReadAllLines(path).Count(l => l.Length > 0) : 0;
    }

    [Fact]
    public async Task HostDropsItsOldDraftWhenTheRecordsScreenSaves()
    {
        var (host, memos, folder) = Hosted();
        host.SetDraft(Id, "k1", "측정 화면에 남은 옛 초안");
        var r = await memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "기록 화면에서 고친 글");
        Assert.Equal(EventWriteState.Saved, r.State);
        Assert.Null(host.Draft(Id, "k1"));   // 종료 때 FlushDraftsAsync가 옛 초안으로 덮어쓰지 않는다
        Assert.Empty(host.UnsavedDrafts());
    }

    [Fact]
    public async Task ClosingWhileTheRecordsScreenSaveIsStillRetryingDoesNotWriteTheOldDraftLast()
    {
        // 기록 화면의 새 글이 재시도를 기다리는 동안 창을 닫아도(초안 넣기) 옛 초안이 그 뒤에 들어가 덮지 않는다 (G4 M7 1라운드 a1)
        var (host, memos, folder) = Hosted();
        host.SetDraft(Id, "k1", "측정 화면에 남은 옛 초안");
        Task<MemoSaveResult> save;
        using (new FileStream(Path.Combine(folder, RecordNotes.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            save = memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "기록 화면에서 고친 글");
            await TestSupport.Eventually(() => host.Draft(Id, "k1") is null, "기록 화면 저장 시작 때 초안 버림");   // 쓰기를 넣기 전에 초안을 버렸다
            await host.FlushDraftsAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(EventWriteState.Saved, (await save).State);
        await host.Notes.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("기록 화면에서 고친 글", MemoOf(folder));
        Assert.Equal(1, MemoLines(folder));
    }

    [Fact]
    public async Task AnOldDraftWriteAlreadyQueuedIsSkippedOnceTheRecordsScreenStartsSaving()
    {
        // 측정 화면의 옛 초안 쓰기가 이미 직렬 작업에 들어가 재시도를 기다리는 중에 기록 화면이 저장을 시작하면,
        // 옛 쓰기는 실행될 때 건너뛰고 새 글 한 줄만 남는다 (세대 검사, G4 M7 1라운드 a1)
        var (host, memos, folder) = Hosted();
        Task<EventWriteState> old;
        Task<MemoSaveResult> fresh;
        using (new FileStream(Path.Combine(folder, RecordNotes.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            old = host.SaveMemoAsync(Id, "k1", "측정 화면의 옛 초안");
            fresh = memos.SaveAsync("known:x", [folder], Id, "k1", 10_000, "기록 화면에서 고친 글");
            await TestSupport.Eventually(() => host.Draft(Id, "k1") is null, "기록 화면 저장 시작 때 초안 버림");
        }
        Assert.Equal(EventWriteState.Saved, await old);
        Assert.Equal(EventWriteState.Saved, (await fresh).State);
        Assert.Equal("기록 화면에서 고친 글", MemoOf(folder));
        Assert.Equal(1, MemoLines(folder));
        Assert.Empty(host.UnsavedDrafts());
    }

    [Fact]
    public async Task SavingACopyFromAnotherLocationLeavesTheOriginalsDraftAlone()
    {
        // 같은 측정 ID라도 출처가 다르면 다른 항목: 다른 위치 복사본의 메모 저장이 원본의 초안을 지우지 않는다 (G4 M7 1라운드 a2)
        var (host, memos, _) = Hosted();
        host.SetDraft(Id, "k1", "원본의 저장 안 된 초안");
        var copy = Folder(root: dir.Sub("copy"));
        var r = await memos.SaveAsync("ext:0:copy", [copy], Id, "k1", 10_000, "복사본에서 고친 글");
        Assert.Equal(EventWriteState.Saved, r.State);
        Assert.Equal("원본의 저장 안 된 초안", host.Draft(Id, "k1"));
    }
}
