using NetPulse.Core.Demo;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Live;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;

namespace NetPulse.Core.Tests;

/// <summary>
/// M9 G4 1라운드 부분 a: 옮기기의 정본 기준 충돌(a2)·앞 원본 우선(a3)·표시 파일 순서(a6)·일지 단계(a7)·임시 폴더 이름(a8)·
/// 기본 위치 다시 옮기기(a5)·진단 설정 시점(a11)·도메인 정규화(a12)·절전 뒤 구간 닫힘(a13).
/// </summary>
public sealed class M9ReviewR1Tests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 13, 20, 0, TimeSpan.FromHours(9));
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static string Id(int hour, string tail) => $"20261113T{hour:00}0000Z_{tail}";

    /// <summary>머리 조각 번호 <paramref name="segment"/>인 기록 조각을 <paramref name="name"/> 폴더 이름으로 만든다.</summary>
    private string MakeAs(string root, string id, int segment, string name, int rounds)
    {
        var made = M9RecordMoveTests.Make(dir.Sub("tmp-" + Guid.NewGuid().ToString("N")), id, 1, segment, rounds);
        Directory.CreateDirectory(root);
        var to = Path.Combine(root, name);
        Directory.Move(made, to);
        return to;
    }

    private static MoveOutcome Run(MovePlan plan, Func<string, bool>? earlierMoved = null) =>
        RecordMover.Execute(plan, new DiskMoveOps(), _ => { }, Ct, earlierMoved);

    // ---------------- a2 ----------------

    [Fact]
    public void ConflictIsJudgedBySegmentNumberEvenUnderAnotherFolderName()
    {
        var id = Id(1, "aaaaaa");
        var from = dir.Sub("from");
        M9RecordMoveTests.Make(from, id, 1, 0, 30);
        // 목적지에 같은 조각 0이 다른 폴더 이름(id~5)과 다른 내용으로 있다: 예전에는 이름만 비교해 놓쳤다
        var to = dir.Sub("to");
        MakeAs(to, id, 0, id + "~5", 20);
        var plan = RecordMover.Plan(MoveKind.New, from, to);
        Assert.Equal([id], plan.Conflicts);
        Assert.Empty(plan.Items);

        // 같은 내용이면 이름이 달라도 이미 옮겨짐
        var to2 = dir.Sub("to2");
        MakeAs(to2, id, 0, id + "~5", 30);
        var plan2 = RecordMover.Plan(MoveKind.New, from, to2);
        Assert.Empty(plan2.Conflicts);
        Assert.True(Assert.Single(Assert.Single(plan2.Items).Segments).AlreadyThere);
    }

    // ---------------- a3 ----------------

    [Fact]
    public void LaterOldLocationCannotReplaceTheFrontCopy()
    {
        var id = Id(2, "bbbbbb");
        var a = dir.Sub("a");
        var b = dir.Sub("b");
        var dest = dir.Sub("dest");
        M9RecordMoveTests.Make(a, id, 2, 0, 30);
        M9RecordMoveTests.Make(b, id, 2, 0, 20);                     // 같은 조각, 다른 내용
        var planA = RecordMover.Plan(MoveKind.Retry, a, dest);
        var planB = RecordMover.Plan(MoveKind.Retry, b, dest, planA.Claims);
        Assert.Equal([id], planB.Conflicts);                          // 뒤 원본은 충돌: 앞 원본이 실패해도 대신 정본이 되지 않는다

        var b2 = dir.Sub("b2");
        M9RecordMoveTests.Make(b2, id, 2, 0, 30);                    // 같은 내용의 사본
        var planB2 = RecordMover.Plan(MoveKind.Retry, b2, dest, planA.Claims);
        Assert.True(Assert.Single(Assert.Single(planB2.Items).Segments).Duplicate);
        var notYet = Run(planB2, _ => false);                         // 앞 원본이 옮겨지지 않았으면 사본을 두고 옮기지 않는다
        Assert.Equal("shadowed", notYet.Failed[id]);
        Assert.True(Directory.Exists(Path.Combine(b2, id)));
        Assert.False(Directory.Exists(Path.Combine(dest, id)));
        var moved = Run(planB2, x => x == id);                        // 앞 원본이 옮겨졌으면 사본만 지운다
        Assert.Equal([id], moved.Moved);
        Assert.False(Directory.Exists(Path.Combine(b2, id)));
        Assert.False(Directory.Exists(Path.Combine(dest, id)));       // 사본은 목적지로 가지 않는다
    }

    [Fact]
    public async Task RetryKeepsTheOldLocationsInReadOrder()
    {
        var userData = dir.Sub("u");
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json"));
        var world = new DemoAppWorld(new DemoWorld(Scenario.Parse(text), new DemoTimeBase(Start, 600)), userData, new DemoRunRules(), dir.Sub("fb"));
        var a = dir.Sub("old-a");
        var b = dir.Sub("old-b");
        var current = world.CurrentRecordFolder;
        Directory.CreateDirectory(current);
        var settings = new Folders { Current = new RecordFolders(null, [a, b], new MoveLog("retry", b, current, [], new Dictionary<string, string>(), [])) };
        var gate = new RecordWorkGate();
        var service = new RecordMoveService(world, gate, new MeasurementHost(world, userData, new HostOptions { WorkGate = gate }), settings, _ => null);
        var (sources, dest) = service.Route(MoveKind.Retry, null);
        Assert.Equal([a, b], sources);                                // 일지의 원본(b)을 앞으로 당기지 않는다
        Assert.Equal(current, dest);
        // 같은 측정이 두 옛 위치에 다른 내용으로: 옮기기 전체를 충돌로 멈춘다 (아무것도 옮기지 않음)
        var id = Id(3, "cccccc");
        M9RecordMoveTests.Make(a, id, 3, 0, 30);
        M9RecordMoveTests.Make(b, id, 3, 0, 20);
        var report = await service.RunAsync(MoveKind.Retry, null, null, Ct);
        Assert.Equal(MoveState.Conflict, report.State);
        Assert.True(Directory.Exists(Path.Combine(a, id)) && Directory.Exists(Path.Combine(b, id)));
        Assert.True(service.Running.IsCompleted);
    }

    private sealed class Folders : IRecordFolderSettings
    {
        public RecordFolders Current = new(null, [], null);
        public bool Writable => true;
        public RecordFolders Read() => Current;
        public bool Save(RecordFolders next) { Current = next; return true; }
    }

    // ---------------- a6 ----------------

    [Fact]
    public void MarkerLeftInAFinalFolderIsCleanedAndIgnored()
    {
        var id = Id(4, "dddddd");
        var from = dir.Sub("from");
        var to = dir.Sub("to");
        M9RecordMoveTests.Make(from, id, 4);
        var final = M9RecordMoveTests.Make(to, id, 4);
        File.WriteAllText(Path.Combine(final, RecordMover.Marker), "");     // 이름을 바꾼 뒤 표시를 지우기 전에 끊김
        Assert.True(Assert.Single(Assert.Single(RecordMover.Plan(MoveKind.Retry, from, to).Items).Segments).AlreadyThere);   // 내용 비교는 표시를 보지 않는다
        RecordMover.CleanupOwnMoving(to);
        Assert.False(File.Exists(Path.Combine(final, RecordMover.Marker)));
        Assert.True(Directory.Exists(final));                                // 기록 폴더는 그대로
    }

    [Fact]
    public void CrossVolumeMoveRemovesTheMarkerAfterTheFinalRename()
    {
        var id = Id(5, "eeeeee");
        var from = dir.Sub("from");
        var to = dir.Sub("to");
        M9RecordMoveTests.Make(from, id, 5);
        var seen = new List<bool>();
        var ops = new Probe { OnRename = (src, dst) => seen.Add(File.Exists(Path.Combine(src, RecordMover.Marker))) };
        var r = RecordMover.Execute(RecordMover.Plan(MoveKind.New, from, to), ops, _ => { }, Ct);
        Assert.Equal([id], r.Moved);
        Assert.Equal([true], seen);                                          // 이름을 바꿀 때 표시가 아직 있다 (우리 것으로 남음)
        Assert.False(File.Exists(Path.Combine(to, id, RecordMover.Marker)));  // 바꾼 뒤 지움
    }

    private sealed class Probe : DiskMoveOps
    {
        public Action<string, string>? OnRename;
        public override bool SameVolume(string a, string b) => false;
        public override void RenameDirectory(string from, string to) { OnRename?.Invoke(from, to); base.RenameDirectory(from, to); }
    }

    // ---------------- a7 ----------------

    [Fact]
    public void RecoveryValidatesTheStepReadFromTheJournal()
    {
        var from = dir.Sub("from");
        var to = dir.Sub("to");
        var f = new RecordFolders(to, [from], new MoveLog("new", from, to, [], new Dictionary<string, string>(), [], "bogus"));
        Assert.Equal((true, "step"), RecordMoveService.Recover(f, to));
        Assert.Equal((true, (string?)null), RecordMoveService.Recover(f with { Move = f.Move! with { Step = "moving" } }, to));
    }

    // ---------------- a8 ----------------

    [Fact]
    public void MovingAndMovedFoldersAreNeverSegments()
    {
        var id = Id(6, "ffffff");
        Assert.Equal(id, RecordCatalog.SegmentId(id));
        Assert.Equal(id, RecordCatalog.SegmentId(id + "~2"));
        Assert.Null(RecordCatalog.SegmentId(id + "~2.moving"));
        Assert.Null(RecordCatalog.SegmentId(id + ".moved"));
        Assert.Null(RecordCatalog.SegmentId(id + "~"));
        Assert.Null(RecordCatalog.SegmentId(id + "~x"));
        var root = dir.Sub("r");
        var good = M9RecordMoveTests.Make(root, id, 6);
        MakeAs(root, id, 1, id + "~2.moving", 5);                            // 불완전한 임시 폴더 (조각 1)
        Assert.Equal([good], RecordReader.FindSegments(id, [root]));
        Assert.Equal([good], Assert.Single(RecordCatalog.List([root], 10, null)).Segments);
    }

    // ---------------- a5 ----------------

    [Fact]
    public void RetryBackToTheDefaultFolderIsAllowedInsideTheUserFolder()
    {
        var userData = dir.Sub("user");
        var def = Directory.CreateDirectory(Path.Combine(userData, "records")).FullName;
        var old = dir.Sub("old");
        var known = new KnownLocations(def, [old], dir.Sub("fallback"), userData, def);
        Assert.Null(RecordFolderRules.Check(def, MoveKind.Default, known with { Current = old }, _ => null, 40));
        Assert.Null(RecordFolderRules.Check(def, MoveKind.Retry, known, _ => null, 40));   // 기본 위치로 되돌리다 끊긴 뒤
        // 기본 위치가 아닌 앱 데이터 안 폴더는 여전히 거부
        var inside = Directory.CreateDirectory(Path.Combine(userData, "other")).FullName;
        Assert.NotNull(RecordFolderRules.Check(inside, MoveKind.Retry, known with { Current = inside, Default = def }, _ => null, 40));
    }

    // ---------------- a11 ----------------

    [Fact]
    public async Task DiagnosisUsesTheSettingsOfTheMomentItWasAccepted()
    {
        var s = Scenario.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json")));
        var tb = new DemoTimeBase(Start, 1);
        tb.Freeze(TimeSpan.FromSeconds(10));
        var probes = new DemoDiagnosticProbes(new DemoWorld(s, tb));
        var settings = DemoDiagnosticProbes.Settings(s) with { Domains = ["before.example.com", "before.example.net"] };
        var host = new DiagnosisHost(new DiagnosisWorld(probes, settings, new NoActions()), new DiagnosisStore(dir.Sub("d")), () => null, () => Start, "test");
        Assert.Equal(DiagnosisRequest.Started, host.StartDiagnosis());
        host.SetSettings(settings with { Domains = ["after.example.com", "after.example.net"] });   // 받은 뒤, 작업자가 읽기 전
        await TestSupport.EventuallyWithin(() => host.Snapshot is { Operation: DiagnosisOperation.None, Last: not null }, "진단 끝", TestSupport.SessionLimit);
        Assert.Contains("dns:before.example.com", probes.Probed);
        Assert.DoesNotContain(probes.Probed, p => p.Contains("after.", StringComparison.Ordinal));
        await host.ShutdownAsync();
    }

    private sealed class NoActions : ISystemActions
    {
        public Task<SystemActionResult> FlushDnsAsync(CancellationToken ct) => Task.FromResult(new SystemActionResult(true, null));
        public SystemActionResult OpenSettings(SettingsPage page) => new(true, null);
        public SystemActionResult OpenFolder(string folder, IReadOnlyList<string> allowedRoots) => new(true, null);
    }

    // ---------------- a12 ----------------

    [Fact]
    public void DomainsThatDifferOnlyByTheTrailingDotAreTheSame()
    {
        var e = DiagnosisDefaults.Validate([], ["a.example.com", "a.example.com."]);
        Assert.True(e.ContainsKey("domain.1"));
        Assert.Equal(["a.example.com", "b.example.com"], DiagnosisDefaults.From(null, ["a.example.com.", " b.example.com "]).Settings.Domains);
    }

    // ---------------- a13 ----------------

    [Fact]
    public void ObservationRightAfterALongGapClosesTheOldSegmentFirst()
    {
        var n = new ResidentNotifier();
        long v = 0;
        var prefs = NoticePrefs.Default with { NotifyRecovered = true };
        IReadOnlyList<ResidentNotice> At(long ms, ObservationKind o) =>
            n.Feed(new ResidentInput(++v, "m1", true, ms, o, false, null, null, false, null), WindowPresence.Hidden, prefs);
        At(1000, ObservationKind.LinkDown);
        At(5000, ObservationKind.None);
        // 절전 40초: 그동안 장면 없음 → 복귀 첫 장면이 바로 관측
        var after = At(45_000, ObservationKind.LinkDown).Where(x => x.Balloon).Select(x => x.Kind).ToArray();
        Assert.Equal([NoticeKind.SegmentEnd, NoticeKind.SegmentStart], after);
    }

    // ---------------- G4 2라운드 ----------------

    private (RecordMoveService Service, DemoAppWorld World, Folders Settings, RecordWorkGate Gate) NewService()
    {
        var userData = dir.Sub("u2");
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "steady.json"));
        var world = new DemoAppWorld(new DemoWorld(Scenario.Parse(text), new DemoTimeBase(Start, 600)), userData, new DemoRunRules(), dir.Sub("fb2"));
        Directory.CreateDirectory(world.CurrentRecordFolder);
        var gate = new RecordWorkGate();
        var settings = new Folders();
        return (new RecordMoveService(world, gate, new MeasurementHost(world, userData, new HostOptions { WorkGate = gate }), settings, _ => null), world, settings, gate);
    }

    /// <summary>2라운드 a1: 목적지와 같은 위치는 원본이 되지 않고, 설정 위치가 실제 위치와 어긋나 있으면 옮기지 않는다 (자기 기록을 "이미 있음"으로 지우지 않게).</summary>
    [Fact]
    public async Task ARetryNeverTreatsTheDestinationAsASource()
    {
        var (service, world, settings, _) = NewService();
        var current = world.CurrentRecordFolder;
        var id = Id(7, "gggggg");
        M9RecordMoveTests.Make(current, id, 7);
        settings.Current = new RecordFolders(null, [current], null);              // 지금 위치가 옛 위치 목록에도 있음
        Assert.Empty(service.Route(MoveKind.Retry, null).Sources);
        // 저장 실패로 설정만 B를 가리키고 실제 위치는 A인 상태
        settings.Current = new RecordFolders(dir.Sub("b-not-saved"), [current], null);
        var report = await service.RunAsync(MoveKind.Retry, null, null, Ct);
        Assert.Equal(MoveState.Invalid, report.State);
        Assert.True(Directory.Exists(Path.Combine(current, id)), "원본이 그대로");
    }

    /// <summary>2라운드 a2: 남의 .moving 때문에 옮기지 않는 앞 원본도 정본 정보를 내놓아, 뒤 원본의 같은 조각이 대신 옮겨지지 않는다.</summary>
    [Fact]
    public void AFrontSourceExcludedByAForeignMovingFolderStillShadowsLaterCopies()
    {
        var id = Id(8, "hhhhhh");
        var a = dir.Sub("fa");
        var b = dir.Sub("fb-src");
        var dest = dir.Sub("fdest");
        M9RecordMoveTests.Make(a, id, 8, 0, 30);
        Directory.CreateDirectory(Path.Combine(dest, id + RecordMover.MovingSuffix));   // 우리 표시 없는 .moving (남의 것)
        var planA = RecordMover.Plan(MoveKind.Retry, a, dest);
        Assert.Equal([id], planA.ForeignMoving);
        Assert.Empty(planA.Items);
        // 다른 내용의 뒤 사본: 충돌
        MakeAs(b, id, 0, id + "~5", 20);
        Assert.Equal([id], RecordMover.Plan(MoveKind.Retry, b, dest, planA.Claims).Conflicts);
        // 같은 내용의 뒤 사본: 사본이지만 앞 원본이 옮겨지지 않았으므로 그대로 둔다
        // (같은 이름 id면 그것도 남의 .moving에 걸려 빠진다: 다른 이름의 같은 조각으로)
        var b2 = dir.Sub("fb2-src");
        var made = M9RecordMoveTests.Make(dir.Sub("fb2-tmp"), id, 8, 0, 30);
        Directory.Move(made, Path.Combine(b2, id + "~5"));
        var planB2 = RecordMover.Plan(MoveKind.Retry, b2, dest, planA.Claims);
        Assert.True(Assert.Single(Assert.Single(planB2.Items).Segments).Duplicate);
        Assert.Equal("shadowed", Run(planB2, _ => false).Failed[id]);
        Assert.True(Directory.Exists(Path.Combine(b2, id + "~5")));
        Assert.False(Directory.Exists(Path.Combine(dest, id + "~5")));
    }

    /// <summary>2라운드 a3·b5: 같은 측정의 조각이 두 옛 위치에 나뉘어 있어도 측정 하나로 센다 (모두 옮기면 다 옮김).</summary>
    [Fact]
    public async Task ARecordSplitAcrossTwoOldLocationsCountsOnce()
    {
        var (service, world, settings, _) = NewService();
        var id = Id(9, "iiiiii");
        var a = dir.Sub("sa");
        var b = dir.Sub("sb");
        M9RecordMoveTests.Make(a, id, 9, 0);
        M9RecordMoveTests.Make(b, id, 9, 1);
        settings.Current = new RecordFolders(null, [a, b], null);
        var report = await service.RunAsync(MoveKind.Retry, null, null, Ct);
        Assert.Equal(MoveState.Done, report.State);
        Assert.Equal((1, 1), (report.Moved, report.Total));
        Assert.Equal(2, RecordReader.FindSegments(id, [world.CurrentRecordFolder]).Count);
    }

    /// <summary>3라운드 a1: 지금 위치가 다른 경로 표기(끝의 <c>\.</c>, 구분자)로 옛 위치 목록에 있어도 원본이 되지 않고 기록은 그대로다.</summary>
    [Fact]
    public async Task ARetryWithAnotherSpellingOfTheCurrentFolderKeepsTheRecords()
    {
        var (service, world, settings, _) = NewService();
        var current = world.CurrentRecordFolder;
        var id = Id(10, "jjjjjj");
        M9RecordMoveTests.Make(current, id, 10);
        foreach (var alias in new[] { current + Path.DirectorySeparatorChar + ".", current.Replace('\\', '/'), current.ToUpperInvariant() + Path.DirectorySeparatorChar })
        {
            Assert.True(RecordFolderRules.Same(alias, current), alias);
            settings.Current = new RecordFolders(null, [alias], null);
            Assert.Empty(service.Route(MoveKind.Retry, null).Sources);
            var report = await service.RunAsync(MoveKind.Retry, null, null, Ct);
            Assert.NotEqual(MoveState.Partial, report.State);
            Assert.True(Directory.Exists(Path.Combine(current, id)), "자기 기록을 지우지 않는다: " + alias);
        }
        // 실행 경계의 마지막 방어선
        var plan = RecordMover.Plan(MoveKind.Retry, current + Path.DirectorySeparatorChar + ".", current);
        Assert.Equal("same", RecordMover.Execute(plan, new DiskMoveOps(), _ => { }, Ct).Failed[id]);
        Assert.True(Directory.Exists(Path.Combine(current, id)));
    }

    /// <summary>3라운드 a2·b2: 나뉜 측정의 한쪽 조각만 옮기고 다른 쪽이 실패하면 완료로 세지 않는다 (일부 옮김, 0/1).</summary>
    [Fact]
    public async Task ASplitRecordIsCountedOnlyWhenEveryPartMoved()
    {
        var (service, _, settings, _) = NewService();
        var id = Id(11, "kkkkkk");
        var a = dir.Sub("pa");
        var b = dir.Sub("pb");
        M9RecordMoveTests.Make(a, id, 11, 0);
        var bad = M9RecordMoveTests.Make(b, id, 11, 1);
        settings.Current = new RecordFolders(null, [a, b], null);
        service.Ops = new FailCopyFrom(bad);
        var report = await service.RunAsync(MoveKind.Retry, null, null, Ct);
        Assert.Equal(MoveState.Partial, report.State);                                // 한쪽은 옮겼으니 "하나도 못 옮김"은 아니다
        Assert.Equal((0, 1), (report.Moved, report.Total));
        Assert.True(report.Failed.ContainsKey(id));
    }

    private sealed class FailCopyFrom(string folder) : DiskMoveOps
    {
        public override bool SameVolume(string a, string b) => false;
        public override void CopyFile(string from, string to)
        {
            if (from.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) throw new IOException("시험: 복사 실패");
            base.CopyFile(from, to);
        }
    }

    /// <summary>2라운드 b2: 백그라운드 옮기기는 예약하기 전에 Running을 등록하고, 이미 멈춘 요청은 처음에 돌아온다.</summary>
    [Fact]
    public async Task BackgroundMoveIsRegisteredBeforeItIsScheduled()
    {
        var (service, _, settings, gate) = NewService();
        settings.Current = new RecordFolders(null, [dir.Sub("empty-old")], null);
        var ticket = gate.TryEnter()!;                                             // 작업자가 독점을 기다리게
        var work = service.RunInBackground(MoveKind.Retry, null, null, Ct);
        Assert.False(service.Running.IsCompleted);                                  // 부른 즉시 등록됨
        ticket.Dispose();
        await work.WaitAsync(TestSupport.SessionLimit, Ct);
        await service.Running.WaitAsync(TestSupport.SessionLimit, Ct);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        Assert.Equal(MoveState.Stopped, (await service.RunInBackground(MoveKind.Retry, null, null, stopped.Token)).State);
    }
}
