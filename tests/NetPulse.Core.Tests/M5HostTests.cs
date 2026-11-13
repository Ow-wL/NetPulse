using NetPulse.Core.Analysis;
using NetPulse.Core.Demo;
using NetPulse.Core.Live;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Targets;
using static NetPulse.Core.Tests.TestSupport;

namespace NetPulse.Core.Tests;

/// <summary>M5 설계 4.4·4.5: 측정 수명, 화면 값, 메모 초안·노트, 최근 측정 (화면 없이).</summary>
public sealed class M5HostTests : IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static Scenario Load(string name, string? extra = null)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json"));
        if (extra is not null) text = text.Replace("\"version\": 1,", "\"version\": 1, " + extra + ",");
        return Scenario.Parse(text);
    }

    private (MeasurementHost Host, DemoAppWorld World) NewHost(Scenario scenario, DemoRunRules? rules = null, string? userData = null, HostOptions? options = null)
    {
        var data = userData ?? dir.Sub("u");
        var world = new DemoAppWorld(new DemoWorld(scenario, new DemoTimeBase(Start, 600)), data, rules ?? new DemoRunRules(), dir.Sub("fb"));
        var host = new MeasurementHost(world, data, options ?? new HostOptions { PollInterval = TimeSpan.FromMilliseconds(20), AnalysisInterval = TimeSpan.FromMilliseconds(50) });
        return (host, world);
    }

    private static async Task<HostSnapshot> WaitFor(MeasurementHost host, Func<HostSnapshot, bool> until, string what)
    {
        await EventuallyWithin(() => until(host.Snapshot), what, TimeSpan.FromSeconds(30));
        return host.Snapshot;
    }

    [Fact]
    public async Task FirstRunNeedsAProfileWhichIsKeptForTheNextLaunch()
    {
        var data = dir.Sub("first");
        var (host, _) = NewHost(Load("outage-long"), userData: data);
        Assert.Equal((HostState.NoProfile, ProfileLoadStatus.Missing), (host.Snapshot.State, host.LoadStatus));
        var detection = await host.DetectAsync(TestContext.Current.CancellationToken);
        host.SaveProfiles(new ProfileSet([detection.Proposal!], detection.Proposal!.Id));
        Assert.Equal(HostState.Idle, host.Snapshot.State);
        var (again, _) = NewHost(Load("outage-long"), userData: data);
        Assert.Equal((HostState.Idle, ProfileLoadStatus.Loaded, "공유기"), (again.Snapshot.State, again.LoadStatus, again.CurrentProfile!.Targets[0].Name));
    }

    [Fact]
    public async Task OngoingOutageMapMatchesAReanalysisOfTheSameRange()
    {
        // 설계 7.2 5단계와 같은 비교를 화면 없이: 맵이 그린 후보 = 그 분석 사본의 범위로 기록을 다시 분석한 후보
        var (host, world) = NewHost(Load("outage-long"), new DemoRunRules(HoldRound: 120));
        var detection = await host.DetectAsync(TestContext.Current.CancellationToken);
        host.SaveProfiles(new ProfileSet([detection.Proposal!], detection.Proposal!.Id));
        Assert.True(await host.StartAsync());
        var mark = await WaitFor(host, s => s.Rounds.Count >= 100, "100회차").ContinueWith(_ => host.MarkSymptom("화상회의 중 끊김"), TestContext.Current.CancellationToken);
        Assert.NotNull(mark);
        var snap = await WaitFor(host, s => s.Rounds.Count >= 120 && s.AnalysisAt is { Rounds: >= 120 } && s.Marks.Count == 1, "끊김 중 잠정 분석");
        Assert.Equal(MapCandidate.LocalToInternet, snap.Map.Candidate);
        Assert.Equal(EventWriteState.Saved, snap.Marks[0].State);
        var ext = Assert.Single(snap.Bands, b => b.Kind == BandKind.ExternalNone);
        Assert.True(ext.OpenEnd);
        Assert.Equal(90L, ext.FirstRound);

        // 같은 범위 재분석
        var record = RecordReader.Read(RecordReader.FindSegments(snap.SessionId!, world.RecordPaths));
        var stamp = snap.AnalysisAt!;
        var re = MeasurementAnalysis.Analyze(record.Header!, record.Rounds.Take(stamp.Rounds).ToArray(), record.Gaps,
            record.Events.Take(stamp.Events).ToArray(), null, true, stamp.ElapsedMs, null);
        Assert.Equal(PathMapModel.PickCandidate(re, ended: false).Candidate, snap.Map.Candidate);

        await host.StopAsync();
        var done = host.Snapshot;
        Assert.Equal(HostState.Idle, done.State);
        Assert.NotNull(done.Finished);
        Assert.Equal(EndReason.Stopped, done.Finished!.Result.EndReason);
        Assert.Equal("화상회의 중 끊김", Assert.Single(done.Finished.Marks).Memo);
    }

    [Fact]
    public async Task MemoAfterTheEndGoesToNotesAndDraftsAreFlushedOnStop()
    {
        var (host, world) = NewHost(Load("steady"), new DemoRunRules(HoldRound: 30));
        var detection = await host.DetectAsync(TestContext.Current.CancellationToken);
        host.SaveProfiles(new ProfileSet([detection.Proposal!], detection.Proposal!.Id));
        await host.StartAsync();
        await WaitFor(host, s => s.Rounds.Count >= 10, "10회차");
        var a = host.MarkSymptom()!;
        var b = host.MarkSymptom()!;
        var id = host.Snapshot.SessionId!;
        host.SetDraft(id, a.Mark, "중지 전에 쓰던 메모");             // 저장하지 않은 초안
        await host.StopAsync();                                       // 중지가 초안을 먼저 넣는다
        Assert.Empty(host.UnsavedDrafts());
        Assert.Equal(EventWriteState.Saved, await host.SaveMemoAsync(id, b.Mark, "끝난 뒤 메모"));   // 노트로
        var record = RecordReader.Read(RecordReader.FindSegments(id, world.RecordPaths));
        var marks = SymptomMarks.FromEvents(record.Events, record.NoteLines).Marks;
        Assert.Equal(["중지 전에 쓰던 메모", "끝난 뒤 메모"], marks.Select(m => m.Memo));
        Assert.Single(record.NoteLines);
        Assert.Equal("끝난 뒤 메모", host.Snapshot.Finished!.Marks.Single(m => m.Mark == b.Mark).Memo);
    }

    [Fact]
    public async Task StartFailureIsReportedAndTheHostStaysIdle()
    {
        var (host, _) = NewHost(Load("steady", "\"faults\": { \"storageStartFail\": true }"));
        var detection = await host.DetectAsync(TestContext.Current.CancellationToken);
        host.SaveProfiles(new ProfileSet([detection.Proposal!], detection.Proposal!.Id));
        Assert.False(await host.StartAsync());
        Assert.Equal(HostState.Idle, host.Snapshot.State);
        Assert.Equal("기록을 저장할 위치가 없습니다.", host.Snapshot.StartError);
    }

    [Fact]
    public async Task ProfilesCannotChangeWhileMeasuringAndHoldStartStopsAtStarting()
    {
        var (host, _) = NewHost(Load("steady"), new DemoRunRules(HoldRound: 20));
        var detection = await host.DetectAsync(TestContext.Current.CancellationToken);
        var set = new ProfileSet([detection.Proposal!], detection.Proposal!.Id);
        host.SaveProfiles(set);
        await host.StartAsync();
        Assert.Throws<InvalidOperationException>(() => host.SaveProfiles(set));
        await host.StopAsync();

        var (held, _) = NewHost(Load("steady"), options: new HostOptions { HoldStart = true });
        held.SaveProfiles(set);
        Assert.True(await held.StartAsync());
        Assert.Equal(HostState.Starting, held.Snapshot.State);
    }

    [Fact]
    public async Task RecentRecordsAreNewestFirstWithGradesFromTheRecord()
    {
        var data = dir.Sub("recent");
        var (host, world) = NewHost(Load("outage-long"), userData: data);
        var detection = await host.DetectAsync(TestContext.Current.CancellationToken);
        host.SaveProfiles(new ProfileSet([detection.Proposal!], detection.Proposal!.Id));
        for (int i = 0; i < 2; i++)
        {
            var finished = new TaskCompletionSource();
            void OnFinished(FinishedMeasurement _) => finished.TrySetResult();
            host.Finished += OnFinished;
            await host.StartAsync();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);   // 계획 끝(180회차)까지
            host.Finished -= OnFinished;
        }
        var recent = new RecentRecords(() => world.RecordPaths);
        await recent.RefreshAsync(null);
        Assert.Equal(2, recent.Items.Count);
        Assert.True(recent.Items[0].Entry.StartedAt > recent.Items[1].Entry.StartedAt);   // 데모 벽시계가 이어진다
        var first = recent.Items[0];
        var analysis = MeasurementAnalysis.Analyze(RecordReader.Read(first.Entry.Segments));
        Assert.Equal((LiveSummary.Badge(analysis.Grade.Grade), (int?)analysis.Episodes.Count), (first.Badge, first.Outages));
        Assert.Equal(1, first.Outages);
        Assert.Equal("wired", first.ConnectionKind);

        var held = new RecentRecords(() => world.RecordPaths, holdAnalysis: true);
        await held.RefreshAsync(null);
        Assert.All(held.Items, i => Assert.True(i.Analyzing));         // 불러오는 중 장면 재현
    }
}
