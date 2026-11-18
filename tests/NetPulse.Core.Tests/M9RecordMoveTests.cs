using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Tests;

/// <summary>M9 설계 B3·B4·C1: 정본 규칙, 기록 옮기기(단계마다 고장), 위치 검사, 출입문, 진단 기본값, 경로 줄이기.</summary>
public sealed class M9RecordMoveTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 4, 0, 0, TimeSpan.Zero);
    private static readonly Target[] Three =
    [
        new("r", "R", IPAddress.Parse("192.0.2.1"), TargetRole.Local, null, ProbeMethod.Icmp),
        new("a", "A", IPAddress.Parse("198.51.100.1"), TargetRole.External, "X", ProbeMethod.Icmp),
        new("b", "B", IPAddress.Parse("203.0.113.1"), TargetRole.External, "Y", ProbeMethod.Icmp),
    ];

    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static string Id(int hour, string tail) => $"20261113T{hour:00}0000Z_{tail}";

    /// <summary>머리·요약·회차(<paramref name="rounds"/>개)·증상 표시 하나를 쓴 기록 조각 폴더 (M7 시험과 같은 꼴).</summary>
    private static string Make(string root, string id, int hour, int segment = 0, int rounds = 30)
    {
        var folder = Path.Combine(root, segment == 0 ? id : $"{id}~{segment + 1}");
        Directory.CreateDirectory(folder);
        var started = Start.AddHours(hour);
        File.WriteAllText(Path.Combine(folder, "measurement.json"),
            RecordSchema.HeaderJson(new RecordHeader(id, segment, "1.0", started, 540, Three, 1000, 800, 600_000, "primary", ExpectedConnection.Wired)));
        var lines = Enumerable.Range(0, rounds).Select(r => RecordSchema.RoundLine(id, new RoundData(r, r * 1000, r * 1000 + 3, started.AddSeconds(r), 0, false,
            [new("r", ProbeOutcome.Success, 2, null), new("a", ProbeOutcome.Success, 20, null), new("b", ProbeOutcome.Success, 24, null)], r * 1000 + 50)));
        File.WriteAllLines(Path.Combine(folder, "rounds.jsonl"), lines);
        File.WriteAllText(Path.Combine(folder, "events.jsonl"),
            RecordSchema.EventLine(id, SymptomMarks.MarkType, 10, 10_000, started.AddSeconds(10), new JsonObject { ["mark"] = "k1" }) + "\n");
        File.WriteAllText(Path.Combine(folder, "summary.json"), RecordSchema.SummaryJson(
            new RecordSummary(id, EndReason.Stopped, started.AddMinutes(1), rounds, rounds, 0, 0, 0, rounds - 1, false, null, 0, rounds * 1000)));
        return folder;
    }

    /// <summary>고장을 넣는 파일 작업.</summary>
    private sealed class Faulty : DiskMoveOps
    {
        public bool Cross;
        public Func<string, bool> FailCopy = _ => false, FailDelete = _ => false, FailRename = _ => false, Corrupt = _ => false;
        public Action<string>? OnCopy;
        public override bool SameVolume(string a, string b) => !Cross && base.SameVolume(a, b);
        public override void CopyFile(string from, string to)
        {
            OnCopy?.Invoke(from);
            if (FailCopy(from)) throw new IOException("시험: 복사 실패");
            base.CopyFile(from, to);
            if (Corrupt(from)) File.AppendAllText(to, "x");
        }
        public override void DeleteDirectory(string path)
        {
            if (FailDelete(path)) throw new IOException("시험: 삭제 실패");
            base.DeleteDirectory(path);
        }
        public override void RenameDirectory(string from, string to)
        {
            if (FailRename(from)) throw new IOException("시험: 이름 바꾸기 실패");
            base.RenameDirectory(from, to);
        }
    }

    private static MoveOutcome Run(MovePlan plan, IMoveOps ops, List<string>? done = null) =>
        RecordMover.Execute(plan, ops, id => done?.Add(id), TestContext.Current.CancellationToken);

    // ---------------- 정본 규칙 ----------------

    [Fact]
    public void FirstRootWinsForListSegmentsAndNotes()
    {
        var front = dir.Sub("front");
        var back = dir.Sub("back");
        var id = Id(1, "aaaaaa");
        Make(front, id, 1, rounds: 30);
        Make(back, id, 1, rounds: 50);                     // 같은 이름, 다른 내용 (뒤 위치)
        Make(back, id, 1, segment: 1, rounds: 5);          // 뒤 위치에만 있는 조각은 합쳐진다
        var entry = Assert.Single(RecordCatalog.List([front, back], 10, null));
        Assert.Equal(2, entry.Segments.Count);
        Assert.Contains(entry.Segments, s => s.StartsWith(front, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(s) == id);
        Assert.DoesNotContain(entry.Segments, x => x.StartsWith(back, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(x) == id);   // 뒤 사본은 빠짐
        Assert.StartsWith(front, RecordMemos.NotesFolder(entry.Segments)!, StringComparison.OrdinalIgnoreCase);   // 메모 쓸 조각도 앞 위치
        var segs = RecordReader.FindSegments(id, [front, back]);
        Assert.StartsWith(front, RecordMemos.NotesFolder(segs)!, StringComparison.OrdinalIgnoreCase);              // 호스트 경로(위치 순서)도 같음
        Assert.Equal(RecordReader.Read(entry.Segments).Rounds.Count, RecordReader.Read(segs).Rounds.Count);      // 읽기는 조각 번호마다 앞 것
        Assert.Equal(30, RecordReader.Read(segs).Rounds.Count(r => r.Round < 30));
    }

    // ---------------- 옮기기 ----------------

    [Fact]
    public void SameVolumeMovesWholeRecordsAndTheyStayReadable()
    {
        var from = dir.Sub("old");
        var to = dir.Sub("new");
        var a = Id(1, "aaaaaa"); var b = Id(2, "bbbbbb");
        Make(from, a, 1); Make(from, a, 1, segment: 1); Make(from, b, 2);
        Directory.CreateDirectory(Path.Combine(from, "not-a-record"));
        int rounds = RecordReader.Read(RecordReader.FindSegments(a, [from])).Rounds.Count;
        var plan = RecordMover.Plan(MoveKind.New, from, to);
        Assert.Equal(2, plan.Count);
        Assert.Empty(plan.Conflicts);
        var done = new List<string>();
        var r = Run(plan, new DiskMoveOps(), done);
        Assert.True(r.AllMoved(plan));
        Assert.Equal([a, b], done);
        Assert.Equal(["not-a-record"], Directory.EnumerateDirectories(from).Select(Path.GetFileName));   // 기록이 아닌 폴더는 그대로
        Assert.Equal(2, RecordCatalog.List([to, from], 10, null).Count);
        Assert.Equal(2, RecordReader.FindSegments(a, [to, from]).Count);   // 두 조각 모두 새 위치
        Assert.Equal(rounds, RecordReader.Read(RecordReader.FindSegments(a, [to, from])).Rounds.Count);
    }

    [Fact]
    public void CrossVolumeCopiesVerifiesAndLeavesNoMovingFolder()
    {
        var from = dir.Sub("old"); var to = dir.Sub("new");
        var a = Id(1, "aaaaaa");
        var src = Make(from, a, 1);
        var hash = RecordMover.FolderHash(src).Hash;
        var r = Run(RecordMover.Plan(MoveKind.New, from, to), new Faulty { Cross = true });
        Assert.Single(r.Moved);
        Assert.False(Directory.Exists(src));
        Assert.Equal(hash, RecordMover.FolderHash(Path.Combine(to, a)).Hash);
        Assert.Empty(Directory.EnumerateDirectories(to, "*" + RecordMover.MovingSuffix));
        Assert.False(File.Exists(Path.Combine(to, a, RecordMover.Marker)));
    }

    [Fact]
    public void CopyOrVerifyFailureKeepsTheSourceAndCleansUp()
    {
        var from = dir.Sub("old"); var to = dir.Sub("new");
        var a = Id(1, "aaaaaa"); var b = Id(2, "bbbbbb"); var c = Id(3, "cccccc");
        Make(from, a, 1); Make(from, b, 2); Make(from, c, 3);
        var ops = new Faulty { Cross = true, FailCopy = f => f.Contains(b), Corrupt = f => f.Contains(c) && f.EndsWith("rounds.jsonl") };
        var r = Run(RecordMover.Plan(MoveKind.New, from, to), ops);
        Assert.Equal([a], r.Moved);
        Assert.Equal("copy", r.Failed[b]);
        Assert.Equal("verify", r.Failed[c]);
        Assert.True(Directory.Exists(Path.Combine(from, b)));
        Assert.True(Directory.Exists(Path.Combine(from, c)));
        Assert.Empty(Directory.EnumerateDirectories(to, "*" + RecordMover.MovingSuffix));
        Assert.False(Directory.Exists(Path.Combine(to, b)));
    }

    [Fact]
    public void SameContentAtTheDestinationIsAlreadyMovedAndDifferentContentIsAConflict()
    {
        var from = dir.Sub("old"); var to = dir.Sub("new");
        var a = Id(1, "aaaaaa"); var b = Id(2, "bbbbbb");
        Make(from, a, 1); Make(to, a, 1);                  // 같은 내용
        Make(from, b, 2); Make(to, b, 2, rounds: 10);      // 다른 내용
        var plan = RecordMover.Plan(MoveKind.New, from, to);
        Assert.Equal([b], plan.Conflicts);
        var item = Assert.Single(plan.Items);
        Assert.True(item.Segments.All(s => s.AlreadyThere));
        var r = Run(plan, new DiskMoveOps());
        Assert.Equal([a], r.Moved);
        Assert.False(Directory.Exists(Path.Combine(from, a)));
        Assert.True(Directory.Exists(Path.Combine(from, b)));   // 충돌은 건드리지 않는다
    }

    [Fact]
    public void DeleteFailureRenamesTheOldCopyOrLeavesItShadowed()
    {
        var from = dir.Sub("old"); var to = dir.Sub("new");
        var a = Id(1, "aaaaaa"); var b = Id(2, "bbbbbb");
        Make(from, a, 1); Make(from, b, 2);
        var ops = new Faulty { Cross = true, FailDelete = p => p.StartsWith(from), FailRename = p => p.StartsWith(from) && p.Contains(b) };
        var r = Run(RecordMover.Plan(MoveKind.New, from, to), ops);
        Assert.Equal([a, b], r.Moved);
        Assert.Equal([a, b], r.Leftovers);
        Assert.True(Directory.Exists(Path.Combine(from, a + RecordMover.MovedSuffix)));   // 이름 바꾸기로 목록에서 빠짐
        Assert.True(Directory.Exists(Path.Combine(from, b)));                             // 이름 바꾸기도 실패: 그대로
        var list = RecordCatalog.List([to, from], 10, null);
        Assert.Equal(2, list.Count);
        Assert.All(list, e => Assert.All(e.Segments, s => Assert.StartsWith(to, s, StringComparison.OrdinalIgnoreCase)));   // 정본은 새 위치
    }

    [Fact]
    public void StopBetweenRecordsAndDuringCopy()
    {
        var from = dir.Sub("old"); var to = dir.Sub("new");
        var a = Id(1, "aaaaaa"); var b = Id(2, "bbbbbb");
        Make(from, a, 1); Make(from, b, 2);
        using var stop = new CancellationTokenSource();
        var r = RecordMover.Execute(RecordMover.Plan(MoveKind.New, from, to), new DiskMoveOps(), _ => stop.Cancel(), stop.Token);
        Assert.True(r.Stopped);
        Assert.Equal([a], r.Moved);
        Assert.True(Directory.Exists(Path.Combine(from, b)));

        using var stop2 = new CancellationTokenSource();
        var ops = new Faulty { Cross = true, OnCopy = f => { if (f.EndsWith("rounds.jsonl")) stop2.Cancel(); } };
        var r2 = RecordMover.Execute(RecordMover.Plan(MoveKind.Retry, from, to), ops, _ => { }, stop2.Token);
        Assert.True(r2.Stopped);
        Assert.Empty(r2.Moved);
        Assert.Empty(Directory.EnumerateDirectories(to, "*" + RecordMover.MovingSuffix));   // 복사 중 멈춤: 임시 폴더 지움
        Assert.True(Directory.Exists(Path.Combine(from, b)));
    }

    [Fact]
    public void ForeignMovingFolderIsLeftAloneAndOwnIsCleanedOnRecovery()
    {
        var from = dir.Sub("old"); var to = dir.Sub("new");
        var a = Id(1, "aaaaaa"); var b = Id(2, "bbbbbb");
        Make(from, a, 1); Make(from, b, 2);
        Directory.CreateDirectory(Path.Combine(to, a + RecordMover.MovingSuffix));                 // 남의 것
        var own = Directory.CreateDirectory(Path.Combine(to, b + RecordMover.MovingSuffix)).FullName;
        File.WriteAllText(Path.Combine(own, RecordMover.Marker), "");
        var plan = RecordMover.Plan(MoveKind.New, from, to);
        Assert.Equal([a], plan.ForeignMoving);
        Assert.Equal(b, Assert.Single(plan.Items).Id);
        Assert.Equal(1, RecordMover.CleanupOwnMoving(to));
        Assert.True(Directory.Exists(Path.Combine(to, a + RecordMover.MovingSuffix)));
        Assert.False(Directory.Exists(own));
    }

    [Fact]
    public void DamagedRecordMovesByteForByte()
    {
        var from = dir.Sub("old"); var to = dir.Sub("new");
        var a = Id(1, "aaaaaa");
        var src = Make(from, a, 1);
        File.AppendAllText(Path.Combine(src, "rounds.jsonl"), "{깨진 줄\n");
        var before = File.ReadAllBytes(Path.Combine(src, "rounds.jsonl"));
        Run(RecordMover.Plan(MoveKind.New, from, to), new Faulty { Cross = true });
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(to, a, "rounds.jsonl")));
    }

    [Fact]
    public void InterruptedAtAnyStepEveryRecordIsStillFoundWithBothLocationsKnown()
    {
        // 위치(새+옛)를 먼저 확정한 뒤라면 어느 단계에서 끊겨도 두 위치를 읽는 목록에서 모든 기록이 보이고 내용이 같다
        foreach (var step in new[] { "afterCopy", "afterRename", "midDelete" })
        {
            var from = dir.Sub("old-" + step); var to = dir.Sub("new-" + step);
            var ids = new[] { Id(1, "aaaaaa"), Id(2, "bbbbbb"), Id(3, "cccccc") };
            for (int i = 0; i < ids.Length; i++) Make(from, ids[i], i + 1);
            var expected = ids.ToDictionary(i => i, i => RecordReader.Read(RecordReader.FindSegments(i, [from])).Rounds.Count);
            var ops = new Faulty { Cross = true };
            using var stop = new CancellationTokenSource();
            int copies = 0;
            ops.OnCopy = _ => { if (step == "afterCopy" && ++copies == 7) throw new OperationCanceledException(); };
            if (step == "afterRename") ops.FailDelete = p => p.StartsWith(from) ? throw new OperationCanceledException() : false;
            if (step == "midDelete") ops.FailDelete = p => p.Contains(ids[1]) && p.StartsWith(from) ? throw new OperationCanceledException() : false;
            try { Run(RecordMover.Plan(MoveKind.New, from, to), ops); } catch (OperationCanceledException) { }   // 프로세스가 그 순간 끝남
            RecordMover.CleanupOwnMoving(to);   // 다음 실행 복구
            var list = RecordCatalog.List([to, from], 10, null);
            Assert.Equal(ids.Order(), list.Select(e => e.Id).Order());
            foreach (var id in ids) Assert.Equal(expected[id], RecordReader.Read(RecordReader.FindSegments(id, [to, from])).Rounds.Count);
        }
    }

    [Fact]
    public void JournalIsValidatedAsOneUnit()
    {
        var to = dir.Sub("new"); var from = dir.Sub("old");
        string[] ids = [Id(1, "aaaaaa")];
        Assert.Null(RecordMover.ValidateJournal("new", "moving", from, to, ids, [], to, [from]));
        Assert.Equal("kind", RecordMover.ValidateJournal("what", "moving", from, to, ids, [], to, [from]));
        Assert.Equal("to", RecordMover.ValidateJournal("new", "moving", from, to, ids, [], from, [from]));
        Assert.Equal("from", RecordMover.ValidateJournal("new", "moving", from, to, ids, [], to, []));
        Assert.Equal("ids", RecordMover.ValidateJournal("new", "moving", from, to, ids, [Id(2, "zzzzzz")], to, [from]));
        Assert.Equal("path", RecordMover.ValidateJournal("new", "moving", "rel", to, ids, [], to, [from]));
    }

    // ---------------- 위치 검사 ----------------

    private KnownLocations Known(string current, params string[] extra) =>
        new(current, extra, dir.Sub("fallback"), dir.Sub("user"), Path.Combine(dir.Sub("user"), "records"));

    private static string? Check(string dest, MoveKind kind, KnownLocations k) => RecordFolderRules.Check(dest, kind, k, _ => null, 40);

    [Fact]
    public void DestinationRules()
    {
        var current = dir.Sub("cur");
        var old = dir.Sub("old");
        var k = Known(current, old);
        Assert.Null(Check(dir.Sub("good"), MoveKind.New, k));
        Assert.NotNull(Check(current, MoveKind.New, k));                                     // 같음
        Assert.NotNull(Check(Directory.CreateDirectory(Path.Combine(current, "in")).FullName, MoveKind.New, k));   // 안
        Assert.NotNull(Check(dir.Path, MoveKind.New, k));                                    // 품음
        Assert.NotNull(Check(old, MoveKind.New, k));                                         // 옛 위치
        Assert.NotNull(Check(k.Fallback, MoveKind.New, k));                                  // 대체 위치
        Assert.NotNull(Check(Directory.CreateDirectory(Path.Combine(k.UserData, "diagnoses")).FullName, MoveKind.New, k));   // 앱 데이터
        Assert.NotNull(Check(Path.Combine(dir.Path, "missing"), MoveKind.New, k));
        Assert.NotNull(RecordFolderRules.Check(dir.Sub("long"), MoveKind.New, k, _ => null, 400));   // 경로 길이
        // 종류별 예외
        Assert.Null(Check(current, MoveKind.Retry, k));
        Assert.NotNull(Check(dir.Sub("elsewhere"), MoveKind.Retry, k));
        var def = Directory.CreateDirectory(k.Default).FullName;
        Assert.Null(Check(def, MoveKind.Default, Known(current, old, def)));                 // 사용자 폴더 안이어도, 옛 위치에 있어도
        Assert.NotNull(Check(def, MoveKind.New, k));
    }

    [Fact]
    public void JunctionDestinationIsRefused()
    {
        var real = dir.Sub("real");
        var link = Path.Combine(dir.Path, "link");
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(10000);
        Assert.True(Directory.Exists(link));
        Assert.NotNull(Check(link, MoveKind.New, Known(dir.Sub("cur"))));
        Directory.Delete(link);
    }

    [Fact]
    public void PathsAreShortenedForDisplayAndLogs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Path.GetFileName(profile);
        Assert.Equal(@"%LOCALAPPDATA%\NetPulse\records", RecordFolderRules.Display(Path.Combine(local, "NetPulse", "records")));
        Assert.StartsWith("%TEMP%", RecordFolderRules.Display(Path.Combine(Path.GetTempPath(), "x")));
        Assert.Equal(@"E:\기록", RecordFolderRules.Display(@"E:\기록"));                       // 그 밖은 그대로 (자기 화면)
        var text = $"파일을 찾을 수 없음: '{Path.Combine(profile, "Documents", "a.json")}' 그리고 E:\\data\\b.json, \\\\server\\share\\c";
        var clean = RecordFolderRules.Sanitize(text);
        Assert.DoesNotContain(user, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\Documents\a.json", clean);
        Assert.DoesNotContain(@"E:\data", clean);
        Assert.DoesNotContain("server", clean);
        Assert.Contains("<path>", clean);
    }

    // ---------------- 출입문 ----------------

    [Fact]
    public async Task ExclusiveClosesFirstThenWaitsForRunningWork()
    {
        var gate = new RecordWorkGate();
        var ticket = gate.TryEnter();
        Assert.NotNull(ticket);
        var exclusive = gate.EnterExclusiveAsync(TimeSpan.FromSeconds(10));
        Assert.True(gate.Closed);
        Assert.Null(gate.TryEnter());                            // 문을 먼저 닫는다
        Assert.False(exclusive.IsCompleted);
        ticket!.Dispose();
        Assert.True(await exclusive);
        int reopened = 0;
        gate.Reopened += () => reopened++;
        gate.ExitExclusive();
        Assert.Equal(1, reopened);
        Assert.NotNull(gate.TryEnter());
    }

    [Fact]
    public async Task ExclusiveTimesOutAndReopens()
    {
        var gate = new RecordWorkGate();
        using var busy = gate.TryEnter();
        Assert.False(await gate.EnterExclusiveAsync(TimeSpan.FromMilliseconds(200)));
        Assert.False(gate.Closed);
        Assert.NotNull(gate.TryEnter());
    }

    [Fact]
    public async Task LibraryStopsWhileClosedAndRereadsWhenReopened()
    {
        var root = dir.Sub("lib");
        Make(root, Id(1, "aaaaaa"), 1);
        var gate = new RecordWorkGate();
        var lib = new RecordLibrary(() => [root]) { WorkGate = gate };
        Assert.True(await gate.EnterExclusiveAsync(TimeSpan.FromSeconds(5)));
        await lib.RefreshAsync(null);
        Assert.False(lib.Listed);                                // 닫힌 동안 읽지 않는다
        gate.ExitExclusive();
        await EventuallyWithin(() => lib.Listed && lib.Items.Count == 1, "다시 열린 뒤 목록", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MemoSaveIsRefusedWhileMovingAndStartIsBlocked()
    {
        var gate = new RecordWorkGate();
        var memos = new RecordMemos(new NotesWriter(), () => Start) { WorkGate = gate };
        var folder = Make(dir.Sub("m"), Id(1, "aaaaaa"), 1);
        Assert.True(await gate.EnterExclusiveAsync(TimeSpan.FromSeconds(5)));
        var r = await memos.SaveAsync("k", [folder], Id(1, "aaaaaa"), "k1", 10_000, "글");
        Assert.Equal(EventWriteState.Lost, r.State);
        Assert.False(File.Exists(Path.Combine(folder, "notes.jsonl")));
        gate.ExitExclusive();
    }

    private static Task EventuallyWithin(Func<bool> condition, string what, TimeSpan limit) => TestSupport.EventuallyWithin(condition, what, limit);

    // ---------------- 진단 기본값 ----------------

    [Fact]
    public void DiagnosisDefaultsValidation()
    {
        Assert.Empty(DiagnosisDefaults.Validate(["192.0.2.1", "198.51.100.1"], ["www.example.com", "example.net"]));
        var e = DiagnosisDefaults.Validate(["192.0.2.1", "192.0.2.1"], ["example", "www.example.com"]);
        Assert.Contains("tcp.1", e.Keys);
        Assert.Contains("domain.0", e.Keys);
        Assert.False(DiagnosisDefaults.IsIpv4("192.0.2"));
        Assert.False(DiagnosisDefaults.IsIpv4("192.000.2.1"));
        Assert.False(DiagnosisDefaults.IsHostName("-bad.example.com"));
        Assert.False(DiagnosisDefaults.IsHostName("example.123"));
        Assert.False(DiagnosisDefaults.IsHostName("한글.example"));
        var (s, rejected) = DiagnosisDefaults.From(["192.0.2.1", "198.51.100.1"], ["a.example.com", "a.example.com"]);
        Assert.Equal(IPAddress.Parse("198.51.100.1"), s.TcpTargets[1]);
        Assert.Equal(new DiagnosisSettings().Domains, s.Domains);   // 쌍 단위로 기본값
        Assert.Equal(["domains"], rejected);
    }
}
