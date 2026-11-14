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

    private string Folder(bool withSummary = true)
    {
        var f = dir.Sub(Id);
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
        memos.Saved += (id, mark) => { lock (list) list.Add((id, mark)); };
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

    [Fact]
    public async Task HostDropsItsOldDraftWhenTheRecordsScreenSaves()
    {
        var data = dir.Sub("u");
        var scenario = Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json")));
        var world = new DemoAppWorld(new DemoWorld(scenario, new DemoTimeBase(Start, 600)), data, new DemoRunRules(), dir.Sub("fb"));
        var host = new MeasurementHost(world, data, new HostOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        host.SetDraft(Id, "k1", "측정 화면에 남은 옛 초안");
        var memos = new RecordMemos(host.Notes, () => Start);
        memos.Saved += host.MemoSavedElsewhere;
        var r = await memos.SaveAsync("known:x", [Folder()], Id, "k1", 10_000, "기록 화면에서 고친 글");
        Assert.Equal(EventWriteState.Saved, r.State);
        Assert.Null(host.Draft(Id, "k1"));   // 종료 때 FlushDraftsAsync가 옛 초안으로 덮어쓰지 않는다
        Assert.Empty(host.UnsavedDrafts());
    }
}
