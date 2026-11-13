using System.Diagnostics;
using System.Text.Json.Nodes;
using NetPulse.Core.Analysis;
using NetPulse.Core.Detection;
using NetPulse.Core.Evidence;
using NetPulse.Core.Measurement;
using NetPulse.Core.Presentation;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Live;

/// <summary>측정 수명 (M5 설계 4.4).</summary>
public enum HostState { NoProfile, Idle, Starting, Measuring, Stopping }

/// <summary>증상 표시 하나의 화면 값: 메모와 쓰기 상태.</summary>
public sealed record SymptomView(string Mark, long AtMs, DateTimeOffset Wall, string? Memo, EventWriteState State, bool Truncated);

/// <summary>끝난 측정: 결과와 기록을 다시 읽은 확정 분석.</summary>
public sealed record FinishedMeasurement(SessionResult Result, AnalysisResult? Analysis, MeasurementRecord? Record, IReadOnlyList<SymptomView> Marks);

/// <summary>잠정 분석이 쓴 사본의 시점 (G3이 같은 범위로 기록을 다시 분석해 비교, 설계 7.2).</summary>
public sealed record AnalysisStamp(long Seq, long ElapsedMs, int Rounds, int Events, double DurationMs);

/// <summary>
/// 화면에 넘기는 한 장면 (불변). 회차 목록은 복사하지 않고 앞에만 붙는 배열의 보기다.
/// </summary>
public sealed record HostSnapshot(
    long Version, HostState State, Profile? Profile, string? SessionId, RecordHeader? Header, long ElapsedMs, long PlannedMs,
    IReadOnlyList<RoundData> Rounds, IReadOnlyList<GapSpan> Gaps, IReadOnlyList<SymptomView> Marks, IReadOnlyList<LinkDownView> LinkDowns,
    IReadOnlyList<TimelineBand> Bands, MeasurementStats? Stats, IReadOnlyList<TargetCard> Cards, AnalysisResult? Analysis, AnalysisStamp? AnalysisAt,
    PathMapView Map, string? ConnectionKind, SessionStatus? Status, FinishedMeasurement? Finished, string? StartError)
{
    public bool Measuring => State is HostState.Measuring or HostState.Stopping;
    public RoundData? Latest => Rounds.Count == 0 ? null : Rounds[^1];
}

/// <summary>호스트 설정 (시간은 기본값이 설계 값).</summary>
public sealed record HostOptions
{
    /// <summary>실시간 전달 읽기 간격 (설계 4.4: 250 ms)</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>잠정 분석 간격 (설계 4.4: 5초)</summary>
    public TimeSpan AnalysisInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>시험·캡처: 측정 시작 직전에 멈춘다 (시작 중 화면)</summary>
    public bool HoldStart { get; init; }
    public string AppVersion { get; init; } = "0.0.0";
}

/// <summary>
/// 측정 수명과 화면 값 (M5 설계 4.4). 화면 기술(WinUI)과 무관하다: 이벤트는 백그라운드에서 오고, 화면이 UI 스레드로 옮긴다.
/// <list type="bullet">
/// <item>측정 중에는 백그라운드 작업이 실시간 전달을 읽어 장면을 만들고, 새 회차가 있으면 5초마다 잠정 분석한다 (이전 분석이 끝나지 않았으면 건너뜀).</item>
/// <item>끝나면 기록을 다시 읽어 확정 분석한다.</item>
/// <item>메모 초안은 (측정, 표시)별로 쓰기 결과가 저장이 될 때까지 보관한다 (설계 4.5 [2-5]).</item>
/// </list>
/// </summary>
public sealed class MeasurementHost : IAsyncDisposable
{
    private readonly IAppWorld world;
    private readonly string userData;
    private readonly HostOptions options;
    private readonly object gate = new();
    private readonly NotesWriter notes = new();
    /// <summary>메모 초안. 자기 잠금으로 지킨다 (화면이 그릴 때마다 읽으므로 장면 게시 잠금을 기다리지 않게). 잠금 순서: gate → drafts.</summary>
    private readonly Dictionary<(string Session, string Mark), string> drafts = [];
    private HostState state;
    private ProfileSet? profiles;
    private MeasurementSession? session;
    private CancellationTokenSource? loopStop;
    private Task loop = Task.CompletedTask;
    private HostSnapshot snapshot;
    private long version;
    private FinishedMeasurement? finished;
    private string? startError;

    // 측정 중 모델 (loop 작업만 바꾼다)
    private AppendOnlyList<RoundData> rounds = new();
    private readonly List<JsonObject> events = [];
    private readonly Dictionary<string, EventWriteState> eventStates = [];
    private readonly Dictionary<string, string> eventMarks = [];   // 이벤트 ID → 표시 ID (증상·메모)
    private IReadOnlyList<GapSpan> gaps = [];
    private LinkLive links = LinkLive.None;
    private SessionStatus? status;
    private AnalysisResult? analysis;
    private AnalysisStamp? analysisAt;
    private Task<(AnalysisResult, AnalysisStamp)>? analyzing;
    private long lastAnalyzedRounds = -1;
    private readonly Stopwatch sinceAnalysis = new();

    public MeasurementHost(IAppWorld world, string userData, HostOptions? options = null)
    {
        this.world = world;
        this.userData = userData;
        this.options = options ?? new HostOptions();
        var (set, loadStatus, moved) = ProfileStore.Load(userData, world.Now);
        LoadStatus = loadStatus;
        MovedTo = moved;
        profiles = set;
        state = set is null ? HostState.NoProfile : HostState.Idle;
        snapshot = Build();
    }

    public IAppWorld World => world;
    public ProfileLoadStatus LoadStatus { get; }
    /// <summary>읽지 못한 측정 구성 파일을 옮겨 둔 곳 (첫 실행 안내에서 알림).</summary>
    public string? MovedTo { get; }
    public ProfileSet? Profiles { get { lock (gate) return profiles; } }
    public Profile? CurrentProfile { get { lock (gate) return Current(profiles); } }
    public DetectionResult? LastDetection { get; private set; }

    /// <summary>장면이 바뀜 (백그라운드 스레드).</summary>
    public event Action? Changed;
    /// <summary>측정이 끝남 (최근 측정 목록을 다시 읽을 때).</summary>
    public event Action<FinishedMeasurement>? Finished;

    /// <summary>
    /// 마지막으로 게시한 장면. 장면은 바뀌지 않는 값이고 참조 교체는 원자적이라 잠금 없이 읽는다:
    /// 게시(<see cref="Build"/>)는 긴 측정에서 잠금 안에서 수십 ms가 걸려, 화면 스레드가 그동안 기다리며 프레임이 튀었다 (M5 bench).
    /// </summary>
    public HostSnapshot Snapshot => Volatile.Read(ref snapshot);
    public string? RunningId { get { lock (gate) return session is not null && state is HostState.Measuring or HostState.Stopping ? session.Id : null; } }

    private static Profile? Current(ProfileSet? set) =>
        set is null ? null : set.Profiles.FirstOrDefault(p => p.Id == set.LastUsedId) ?? set.Profiles.FirstOrDefault();

    // ---------------- 측정 구성 ----------------

    public async Task<DetectionResult> DetectAsync(CancellationToken cancellationToken)
    {
        var r = await world.DetectAsync(cancellationToken).ConfigureAwait(false);
        LastDetection = r;
        return r;
    }

    /// <summary>측정 구성 저장 (검증 오류면 예외). 측정 중에는 바꾸지 않는다.</summary>
    public void SaveProfiles(ProfileSet set)
    {
        lock (gate)
        {
            if (state is HostState.Measuring or HostState.Stopping or HostState.Starting) throw new InvalidOperationException("측정 중에는 바꿀 수 없습니다.");
        }
        ProfileStore.Save(userData, set);
        lock (gate)
        {
            profiles = set;
            if (state == HostState.NoProfile) state = HostState.Idle;
        }
        Publish();
    }

    // ---------------- 시작·중지 ----------------

    /// <summary>현재 프로필로 측정을 시작한다. 실패하면 이유를 장면에 넣고 false.</summary>
    public async Task<bool> StartAsync()
    {
        Profile profile;
        lock (gate)
        {
            if (state is not (HostState.Idle)) return false;
            profile = Current(profiles) ?? throw new InvalidOperationException("측정 구성이 없습니다.");
            state = HostState.Starting;
            startError = null;
            finished = null;
        }
        Publish();
        if (options.HoldStart) return true;   // 시험·캡처: 시작 중 화면에 멈춤
        try
        {
            var s = await Task.Run(() => world.StartSession(profile, options.AppVersion)).ConfigureAwait(false);
            lock (gate)
            {
                session = s;
                state = HostState.Measuring;
                rounds = new AppendOnlyList<RoundData>();
                events.Clear(); eventStates.Clear(); eventMarks.Clear();
                gaps = []; links = LinkLive.None; status = null; analysis = null; analysisAt = null; analyzing = null; lastAnalyzedRounds = -1;
                loopStop = new CancellationTokenSource();
                loop = Task.Run(() => LoopAsync(s, loopStop.Token));
            }
            _ = s.Completion.ContinueWith(t => OnCompletedAsync(s, t.Result), TaskScheduler.Default).Unwrap();
            Publish();
            return true;
        }
        catch (SessionStartException ex)
        {
            lock (gate) { state = HostState.Idle; startError = ex.InnerException is null ? ex.Message : ex.Message; }
            Publish();
            return false;
        }
    }

    /// <summary>중지 (확인을 묻지 않는다). 메모 초안이 있으면 먼저 넣는다.</summary>
    public async Task StopAsync()
    {
        MeasurementSession? s;
        lock (gate)
        {
            if (state == HostState.Starting && options.HoldStart) { state = HostState.Idle; Publish(); return; }
            if (state != HostState.Measuring) return;
            s = session;
            state = HostState.Stopping;
        }
        Publish();
        await FlushDraftsAsync().ConfigureAwait(false);
        if (s is not null) await s.StopAsync().ConfigureAwait(false);
        // 끝 처리(OnCompleted)가 장면을 확정한다
        await WaitIdleAsync().ConfigureAwait(false);
    }

    private TaskCompletionSource idleSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task WaitIdleAsync()
    {
        lock (gate) return state is HostState.Idle or HostState.NoProfile ? Task.CompletedTask : idleSignal.Task;
    }

    private async Task OnCompletedAsync(MeasurementSession s, SessionResult result)
    {
        loopStop?.Cancel();
        try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        // 마지막 전달을 마저 반영하고 기록을 다시 읽어 확정 분석
        try { Apply(s, s.Live.Read(cursor)); } catch (ArgumentException) { }
        MeasurementRecord? record = null;
        AnalysisResult? final = null;
        try
        {
            record = await Task.Run(() => RecordReader.Read(RecordReader.FindSegments(s.Id, world.RecordPaths))).ConfigureAwait(false);
            if (record.Header is not null) final = MeasurementAnalysis.Analyze(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var marks = record is not null ? MarksOf(record.Events, record.NoteLines, null) : CurrentMarks();
        var done = new FinishedMeasurement(result, final, record, marks);
        TaskCompletionSource signal;
        lock (gate)
        {
            finished = done;
            if (final is not null) analysis = final;
            state = HostState.Idle;
            signal = idleSignal;
            idleSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Publish();
        signal.TrySetResult();
        Finished?.Invoke(done);
    }

    // ---------------- 증상 표시·메모 ----------------

    public SymptomTicket? MarkSymptom(string? memo = null)
    {
        MeasurementSession? s;
        lock (gate) s = state == HostState.Measuring ? session : null;
        var ticket = s?.MarkSymptom(memo);
        return ticket;
    }

    /// <summary>메모 초안 (표시 ID별). 저장이 확정될 때까지 남는다.</summary>
    public void SetDraft(string sessionId, string mark, string? text)
    {
        lock (drafts)
        {
            if (string.IsNullOrWhiteSpace(text)) drafts.Remove((sessionId, mark));
            else drafts[(sessionId, mark)] = text;
        }
    }

    public string? Draft(string sessionId, string mark) { lock (drafts) return drafts.GetValueOrDefault((sessionId, mark)); }

    /// <summary>
    /// 메모 저장 (설계 4.5 [2-5]): 측정 중이면 세션 입력 통로, 끝났으면 노트. 결과가 저장이면 초안을 지운다.
    /// 같은 함수를 반복 표시·저장 버튼·중지·창 닫기가 모두 쓴다.
    /// </summary>
    public async Task<EventWriteState> SaveMemoAsync(string sessionId, string mark, string? memo)
    {
        MeasurementSession? s;
        long atMs;
        lock (gate)
        {
            lock (drafts) drafts[(sessionId, mark)] = memo ?? "";
            s = session is not null && session.Id == sessionId ? session : null;
            atMs = (snapshot.Marks.FirstOrDefault(m => m.Mark == mark) ?? finished?.Marks.FirstOrDefault(m => m.Mark == mark))?.AtMs ?? 0;
        }
        EventWriteState result;
        var live = s?.EditSymptomMemo(mark, memo);
        if (live is not null) result = await live.ConfigureAwait(false);
        else
        {
            var folder = NotesFolder(sessionId);
            result = folder is null ? EventWriteState.Lost
                : await notes.WriteAsync(folder, sessionId, mark, atMs, world.Now, memo).ConfigureAwait(false);
        }
        if (result == EventWriteState.Saved) lock (drafts) drafts.Remove((sessionId, mark));
        if (live is null && result == EventWriteState.Saved) RefreshFinishedMarks(sessionId);
        Publish();
        return result;
    }

    /// <summary>모든 메모 초안을 넣는다 (중지·창 닫기).</summary>
    public async Task FlushDraftsAsync()
    {
        (string Session, string Mark, string Text)[] list;
        lock (drafts) list = drafts.Select(d => (d.Key.Session, d.Key.Mark, d.Value)).ToArray();
        foreach (var d in list) await SaveMemoAsync(d.Session, d.Mark, d.Text).ConfigureAwait(false);
    }

    /// <summary>요약이 있는 조각 폴더 (없으면 마지막 조각).</summary>
    private string? NotesFolder(string sessionId)
    {
        var segments = RecordReader.FindSegments(sessionId, world.RecordPaths);
        return segments.FirstOrDefault(f => File.Exists(Path.Combine(f, "summary.json"))) ?? segments.LastOrDefault();
    }

    private void RefreshFinishedMarks(string sessionId)
    {
        FinishedMeasurement? f;
        lock (gate) f = finished;
        if (f is null || f.Result.Id != sessionId) return;
        try
        {
            var record = RecordReader.Read(RecordReader.FindSegments(sessionId, world.RecordPaths));
            var marks = MarksOf(record.Events, record.NoteLines, null);
            lock (gate) finished = f with { Record = record, Marks = marks };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------- 끝 ----------------

    /// <summary>창 닫기: 초안을 넣고, 측정을 멈추고 저장하고, 노트 쓰기를 최대 3초 기다린다.</summary>
    public async Task ShutdownAsync()
    {
        await FlushDraftsAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        await notes.DrainAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    /// <summary>창을 닫을 때 남은 초안 (표시 ID만, 메모 글은 남기지 않는다).</summary>
    public IReadOnlyList<string> UnsavedDrafts() { lock (drafts) return drafts.Keys.Select(k => k.Mark).ToArray(); }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        loopStop?.Dispose();
    }

    // ---------------- 실시간 반영 (loop 작업) ----------------

    private LiveCursor? cursor;

    private async Task LoopAsync(MeasurementSession s, CancellationToken token)
    {
        cursor = null;
        sinceAnalysis.Restart();
        while (!token.IsCancellationRequested)
        {
            try { Apply(s, s.Live.Read(cursor)); }
            catch (ArgumentException) { cursor = null; }
            MaybeAnalyze(s);
            try { await Task.Delay(options.PollInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Apply(MeasurementSession s, LiveDelta delta)
    {
        bool changed = false;
        if (delta.Resync)
        {
            // 처음이거나 전달 버퍼가 버린 범위: 사본으로 다시 만든다
            var inputs = s.Live.AnalysisInputs();
            var list = new AppendOnlyList<RoundData>();
            foreach (var r in inputs.Rounds) list.Add(r);
            lock (gate)
            {
                rounds = list;
                events.Clear(); eventStates.Clear(); eventMarks.Clear();
                foreach (var e in inputs.Events) AddEvent(e, null, EventWriteState.Saved);
            }
            cursor = inputs.At;
            delta = s.Live.Read(cursor);
            changed = true;
        }
        foreach (var item in delta.Items)
        {
            switch (item)
            {
                case RoundItem r:
                    rounds.Add(r.Round);
                    lock (gate) foreach (var e in r.Related) AddEvent(JsonNode.Parse(e.Json)!.AsObject(), e.EventId, e.State);
                    break;
                case EventItem e:
                    lock (gate) AddEvent(JsonNode.Parse(e.Event.Json)!.AsObject(), e.Event.EventId, e.Event.State);
                    break;
                case StateItem st:
                    lock (gate) eventStates[st.EventId] = st.State;
                    break;
            }
            changed = true;
        }
        cursor = delta.Next;
        lock (gate)
        {
            gaps = delta.Gaps;
            if (!Equals(links, delta.Links)) changed = true;
            links = delta.Links;
            status = delta.Status;
        }
        if (changed) Publish();
    }

    /// <summary>gate 안.</summary>
    private void AddEvent(JsonObject o, string? eventId, EventWriteState state)
    {
        events.Add(o);
        if (eventId is null) return;
        eventStates[eventId] = state;
        var type = (string?)o["type"];
        if (type is SymptomMarks.MarkType or SymptomMarks.MemoType && (string?)o["mark"] is { } mark) eventMarks[eventId] = mark;
    }

    private void MaybeAnalyze(MeasurementSession s)
    {
        if (analyzing is { IsCompleted: true } done)
        {
            var (result, stamp) = done.Result;
            lock (gate) { analysis = result; analysisAt = stamp; }
            analyzing = null;
            Publish();
        }
        if (analyzing is not null || sinceAnalysis.Elapsed < options.AnalysisInterval) return;
        int count = rounds.Count;
        if (count == lastAnalyzedRounds) return;
        lastAnalyzedRounds = count;
        sinceAnalysis.Restart();
        analyzing = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var inputs = s.Live.AnalysisInputs();
            var result = MeasurementAnalysis.Analyze(inputs.Header, inputs.Rounds, inputs.Gaps, inputs.Events, null, provisional: true, inputs.ElapsedMs, inputs.Links);
            return (result, new AnalysisStamp(inputs.At.Seq, inputs.ElapsedMs, inputs.Rounds.Count, inputs.Events.Count, watch.Elapsed.TotalMilliseconds));
        });
    }

    // ---------------- 장면 ----------------

    private void Publish()
    {
        HostSnapshot next;
        lock (gate)
        {
            next = Build();
            Volatile.Write(ref snapshot, next);
        }
        Changed?.Invoke();
    }

    /// <summary>gate 안.</summary>
    private IReadOnlyList<SymptomView> CurrentMarks() => MarksOf(events, null, eventStates);

    private IReadOnlyList<SymptomView> MarksOf(IReadOnlyList<JsonObject> evs, IReadOnlyList<JsonObject>? notesLines, Dictionary<string, EventWriteState>? states)
    {
        var (marks, _) = SymptomMarks.FromEvents(evs, notesLines);
        var stateOf = new Dictionary<string, EventWriteState>();
        if (states is not null)
            foreach (var (id, mark) in eventMarks)
                if (states.TryGetValue(id, out var st))
                    stateOf[mark] = stateOf.TryGetValue(mark, out var prev) && prev != EventWriteState.Saved ? prev : st;
        return marks.Select(m => new SymptomView(m.Mark, m.AtMs, m.Wall, m.Memo, stateOf.GetValueOrDefault(m.Mark, EventWriteState.Saved), m.Truncated)).ToArray();
    }

    /// <summary>gate 안: 지금 모델로 장면을 만든다.</summary>
    private HostSnapshot Build()
    {
        var header = session?.Header;
        bool measuring = state is HostState.Measuring or HostState.Stopping;
        if (!measuring && finished?.Record is { Header: { } fh } fr)
        {
            // 끝난 측정의 장면 (기록에서)
            var fRounds = fr.Rounds;
            var fBands = TimelineModel.Bands(fRounds, fh.Targets, fr.Gaps, fh.IntervalMs);
            var fLinks = (finished.Analysis?.Links ?? LinkEvidence.None).Downs.Select(d => new LinkDownView(d.Adapter, d.Source, d.FromMs, d.ToMs, false)).ToArray();
            var fStats = fr.Stats ?? StatsCalculator.Compute(fRounds, fh.Targets.Select(t => t.Id).ToArray(), fr.Gaps);
            var kind = PathMapModel.ConnectionKind(fr.Events);
            return new HostSnapshot(++version, state, Current(profiles), fh.Id, fh, fr.Summary?.EndMs ?? fRounds.LastOrDefault()?.AtMs ?? 0, fh.PlannedMs,
                fRounds, fr.Gaps, finished.Marks, fLinks, fBands, fStats, LiveSummary.Cards(fh.Targets, fStats, fRounds.LastOrDefault()),
                finished.Analysis, null, PathMapModel.From(fh.Targets, fRounds, kind, finished.Analysis, null, ended: true), kind, null, finished, startError);
        }
        if (!measuring || header is null)
        {
            var idleTargets = Current(profiles)?.ActiveTargets ?? [];
            var kind = LastDetection?.Kind switch { InterfaceKind.Wired => "wired", InterfaceKind.Wireless => "wireless", _ => null };
            return new HostSnapshot(++version, state, Current(profiles), null, null, 0, (long)(Current(profiles)?.Duration.TotalMilliseconds ?? 0),
                Array.Empty<RoundData>(), [], [], [], [], null, LiveSummary.Cards(idleTargets, new MeasurementStats(0, 0, 0, 0, []), null),
                null, null, PathMapModel.Idle(idleTargets, kind), kind, null, finished, startError);
        }
        var view = rounds.Snapshot();
        // 통계·구간은 회차와 공백으로만 정해진다. 링크·저장 상태만 바뀐 게시(0.25초마다)에서는 다시 쓴다:
        // 12시간 기록에서 게시마다 처음부터 다시 구해 큰 배열이 쌓이고 gen2 GC가 초당 한 번 넘게 돌아 프레임이 튀었다 (M5 bench)
        if (derived is not { } dv || !ReferenceEquals(dv.Header, header) || dv.Count != view.Count || !dv.Gaps.SequenceEqual(gaps))
        {
            var ids = header.Targets.Select(t => t.Id).ToArray();
            var st = StatsCalculator.Compute(view, ids, gaps);
            derived = dv = new Derived(header, view.Count, gaps.ToArray(), st, TimelineModel.Bands(view, header.Targets, gaps, header.IntervalMs),
                LiveSummary.Cards(header.Targets, st, view.Count == 0 ? null : view[^1]));
        }
        var stats = dv.Stats;
        var bands = dv.Bands;
        long elapsed = view.Count == 0 ? 0 : view[^1].EndMs ?? view[^1].AtMs;
        var linkEvents = events.Where(e => (string?)e["type"] == "link").Concat(links.ToSummaryEvents(header.Id, elapsed, header.StartedAt.AddMilliseconds(elapsed))).ToArray();
        var evidence = LinkEvidenceBuilder.FromEvents(linkEvents);
        var downAdapters = links.Adapters.Where(a => a.State == LinkState.Down).Select(a => a.Alias).ToHashSet();
        var downs = evidence.Downs.Select(d => new LinkDownView(d.Adapter, d.Source, d.FromMs, d.ToMs,
            d.Source == LinkSource.Direct && downAdapters.Contains(d.Adapter) && d == evidence.Downs.Last(x => x.Adapter == d.Adapter))).ToArray();
        var connection = PathMapModel.ConnectionKind(events);
        var tail = view.Count <= 10 ? (IReadOnlyList<RoundData>)view : Enumerable.Range(view.Count - 10, 10).Select(i => view[i]).ToArray();
        var map = PathMapModel.From(header.Targets, tail, connection, analysis, links, ended: false);
        return new HostSnapshot(++version, state, Current(profiles), session!.Id, header, elapsed, header.PlannedMs, view, gaps, CurrentMarks(), downs, bands,
            stats, dv.Cards, analysis, analysisAt, map, connection, status, null, startError);
    }

    /// <summary>회차·공백에서 나오는 무거운 값 (gate 안에서만).</summary>
    private sealed record Derived(RecordHeader Header, int Count, GapSpan[] Gaps, MeasurementStats Stats, IReadOnlyList<TimelineBand> Bands, IReadOnlyList<TargetCard> Cards);
    private Derived? derived;
}

/// <summary>
/// 끝난 측정의 노트 쓰기 (M5 설계 4.5): 앱에 하나인 직렬 작업. 실패하면 1초·3초 뒤 다시 시도하고, 그래도 실패면 유실.
/// </summary>
public sealed class NotesWriter
{
    private Task tail = Task.CompletedTask;
    private readonly object gate = new();

    /// <summary>재시도 간격 (시험이 줄인다).</summary>
    public TimeSpan[] RetryDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    public Task<EventWriteState> WriteAsync(string folder, string measurementId, string mark, long markAtMs, DateTimeOffset editedWall, string? memo)
    {
        var done = new TaskCompletionSource<EventWriteState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            tail = tail.ContinueWith(async _ =>
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        RecordNotes.AppendMemo(folder, measurementId, mark, markAtMs, editedWall, memo);
                        done.TrySetResult(EventWriteState.Saved);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt >= RetryDelays.Length) { done.TrySetResult(EventWriteState.Lost); return; }
                        await Task.Delay(RetryDelays[attempt]).ConfigureAwait(false);
                    }
                }
            }, TaskScheduler.Default).Unwrap();
        }
        return done.Task;
    }

    /// <summary>남은 쓰기를 최대 <paramref name="limit"/>만큼 기다린다.</summary>
    public async Task<bool> DrainAsync(TimeSpan limit)
    {
        Task t;
        lock (gate) t = tail;
        return await Task.WhenAny(t, Task.Delay(limit)).ConfigureAwait(false) == t;
    }
}
