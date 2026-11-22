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

    /// <summary>
    /// Wi‑Fi 신호 표시 (M12 설계 C). 측정 중에는 실시간 사본(마지막 표본까지), 끝나면 기록의 신호 근거에서.
    /// 신호가 관측되지 않은 측정은 <see cref="SignalTrace.None"/>.
    /// </summary>
    public SignalTrace Signal { get; init; } = SignalTrace.None;
}

/// <summary>호스트 설정 (시간은 기본값이 설계 값).</summary>
public sealed record HostOptions
{
    /// <summary>실시간 전달 읽기 간격 (설계 4.4: 250 ms)</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>중지·종료 때 메모 초안 저장을 기다리는 한도 (넘으면 초안은 남기고 진행).</summary>
    public TimeSpan DraftFlushLimit { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>잠정 분석 간격 (설계 4.4: 5초)</summary>
    public TimeSpan AnalysisInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>시험·캡처: 측정 시작 직전에 멈춘다 (시작 중 화면)</summary>
    public bool HoldStart { get; init; }
    /// <summary>기록 작업 출입문 (M9 설계 B3): 옮기는 동안 측정 시작·끝난 측정의 메모 쓰기를 막는다.</summary>
    public RecordWorkGate? WorkGate { get; init; }
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
    /// <summary>
    /// (측정, 표시)별 메모 세대 (drafts 잠금). 기록 화면이 그 표시를 저장하기 시작하면 올라간다: 그 전에 잡은 초안 쓰기는
    /// 직렬 작업에서 실행될 때 건너뛴다 (G4 M7 1라운드 a1).
    /// </summary>
    private readonly Dictionary<(string Session, string Mark), long> memoEpochs = [];
    private HostState state;
    private ProfileSet? profiles;
    private MeasurementSession? session;
    private CancellationTokenSource? loopStop;
    private Task loop = Task.CompletedTask;
    private HostSnapshot snapshot;
    private long version;
    private FinishedMeasurement? finished;
    private string? startError;
    /// <summary>종료가 시작되면 새 시작을 받지 않는다 (M9 설계 A1 종료 합류).</summary>
    private bool startsBlocked;
    /// <summary>진행 중인 시작이 끝나면(성공·실패) 풀린다. 시작 중이 아니면 끝난 작업.</summary>
    private TaskCompletionSource startDone = Completed();

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    // 측정 중 모델 (loop 작업만 바꾼다)
    private AppendOnlyList<RoundData> rounds = new();
    private readonly List<JsonObject> events = [];
    private readonly Dictionary<string, EventWriteState> eventStates = [];
    private readonly Dictionary<string, string> eventMarks = [];   // 이벤트 ID → 표시 ID (증상·메모)
    private IReadOnlyList<GapSpan> gaps = [];
    private LinkLive links = LinkLive.None;
    private SignalLive signalLive = SignalLive.None;
    private SignalTrace signalTrace = SignalTrace.None;
    private (FinishedMeasurement For, SignalTrace Trace)? finishedSignal;
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

    /// <summary>
    /// 장면을 게시할 때마다 그 장면 (M9 설계 A1 [2-5]). 알림처럼 전환을 하나도 놓치면 안 되는 쪽이 쓴다:
    /// <see cref="Changed"/>는 처리할 때 최신 장면을 읽어 중간 장면이 합쳐질 수 있다. 여러 스레드에서 올 수 있어
    /// 받는 쪽은 <see cref="HostSnapshot.Version"/>으로 순서를 맞춘다.
    /// </summary>
    public event Action<HostSnapshot>? Published;

    /// <summary>새 시작을 막는다 (종료·세션 종료 진입). 이미 막혀 있으면 그대로.</summary>
    public void BlockStarts() { lock (gate) startsBlocked = true; }

    /// <summary>세션 종료가 취소되면 다시 시작을 받는다.</summary>
    public void UnblockStarts() { lock (gate) startsBlocked = false; }

    public bool StartsBlocked { get { lock (gate) return startsBlocked; } }
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
            if (state is not (HostState.Idle) || startsBlocked) return false;
        }
        // 작업 문의 표를 받고 시작한다 (G4 M9 1라운드 a1): 옮기기가 독점을 얻으면 이 표가 끝나기(측정 중 상태 게시)를 기다린 뒤
        // 호스트 상태를 다시 보고 거절한다. 표 없이 시작하면 독점 확인과 세션 생성 사이에 끼어들 수 있었다.
        var ticket = options.WorkGate?.TryEnter();
        if (options.WorkGate is not null && ticket is null)
        {
            lock (gate)
            {
                if (state is not (HostState.Idle) || startsBlocked) return false;
                startError = "기록을 옮기는 중에는 측정을 시작할 수 없습니다.";
            }
            Publish();
            return false;
        }
        using var held = ticket;
        lock (gate)
        {
            if (state is not (HostState.Idle) || startsBlocked) return false;
            profile = Current(profiles) ?? throw new InvalidOperationException("측정 구성이 없습니다.");
            state = HostState.Starting;
            startError = null;
            finished = null;
            if (!options.HoldStart) startDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Publish();
        if (options.HoldStart) return true;   // 시험·캡처: 시작 중 화면에 멈춤
        var done = startDone;
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
                signalLive = SignalLive.None; signalTrace = SignalTrace.None;
                loopStop = new CancellationTokenSource();
                loop = Task.Run(() => LoopAsync(s, loopStop.Token));
            }
            _ = s.Completion.ContinueWith(t => OnCompletedAsync(s, t.Result), TaskScheduler.Default).Unwrap();
            Publish();
            return true;
        }
        catch (Exception ex)
        {
            // 어떤 시작 실패든 시작 중에 남지 않는다 (G4 M5 1라운드 A3). 예외 글에는 경로가 들어갈 수 있고 예외 이름은 사용자 글에 넣지 않는다: 사유만 (화면이 "측정을 시작하지 못했습니다: "를 앞에 붙인다)
            string message = ex is SessionStartException ? ex.Message : ex is IOException or UnauthorizedAccessException ? "기록 파일을 쓰지 못했습니다." : "예상하지 못한 오류가 났습니다.";
            lock (gate) { state = HostState.Idle; startError = message; }
            Publish();
            return false;
        }
        finally { done.TrySetResult(); }
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
        await FlushDraftsAsync(options.DraftFlushLimit).ConfigureAwait(false);
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

    /// <summary>노트 쓰기 직렬 작업 (기록 화면의 메모 고치기도 같은 인스턴스를 쓴다, M7 구현 중 결정 4).</summary>
    public NotesWriter Notes => notes;

    /// <summary>
    /// 기록 화면이 그 표시의 메모를 쓰기 시작한다 (쓰기를 넣기 직전, UI 밖): 이 호스트가 아는 그 측정의 노트 폴더와 같을 때만
    /// 측정 화면에 남은 옛 초안을 버리고 세대를 올린다. 이미 넣은 옛 초안 쓰기도 실행 때 건너뛴다 (M7 구현 중 결정 4, G4 M7 1라운드 a1·a2).
    /// </summary>
    public void MemoSavingElsewhere(string folder, string sessionId, string mark)
    {
        if (!SameFolder(NotesFolder(sessionId), folder)) return;   // 같은 ID의 다른 위치 복사본
        lock (drafts)
        {
            drafts.Remove((sessionId, mark));
            memoEpochs[(sessionId, mark)] = memoEpochs.GetValueOrDefault((sessionId, mark)) + 1;
        }
        Publish();
    }

    /// <summary>기록 화면에서 그 표시의 메모를 저장했다: 방금 끝난 측정(같은 폴더)이면 결과 카드의 메모를 다시 읽는다.</summary>
    public void MemoSavedElsewhere(string folder, string sessionId, string mark)
    {
        if (!SameFolder(NotesFolder(sessionId), folder)) return;
        RefreshFinishedMarks(sessionId);
        Publish();
    }

    private static bool SameFolder(string? a, string? b)
    {
        if (a is null || b is null) return false;
        static string N(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        try { return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private long MemoEpoch(string sessionId, string mark) { lock (drafts) return memoEpochs.GetValueOrDefault((sessionId, mark)); }

    /// <summary>
    /// 메모 저장 (설계 4.5 [2-5]): 측정 중이면 세션 입력 통로, 끝났으면 노트. 결과가 저장이면 초안을 지운다.
    /// 같은 함수를 반복 표시·저장 버튼·중지·창 닫기가 모두 쓴다.
    /// </summary>
    public Task<EventWriteState> SaveMemoAsync(string sessionId, string mark, string? memo) => SaveMemoAsync(sessionId, mark, memo, null);

    /// <param name="epoch">초안을 잡을 때의 세대 (없으면 지금). 그 사이 기록 화면이 저장을 시작했으면 쓰지 않는다 (더 새 글이 있다)</param>
    private async Task<EventWriteState> SaveMemoAsync(string sessionId, string mark, string? memo, long? epoch)
    {
        MeasurementSession? s;
        long atMs;
        long mine;
        lock (gate)
        {
            lock (drafts)
            {
                mine = epoch ?? memoEpochs.GetValueOrDefault((sessionId, mark));
                if (mine != memoEpochs.GetValueOrDefault((sessionId, mark))) return EventWriteState.Saved;   // 이미 더 새 글: 할 일 없음
                drafts[(sessionId, mark)] = memo ?? "";
            }
            s = session is not null && session.Id == sessionId ? session : null;
            atMs = (snapshot.Marks.FirstOrDefault(m => m.Mark == mark) ?? finished?.Marks.FirstOrDefault(m => m.Mark == mark))?.AtMs ?? 0;
        }
        EventWriteState result;
        var live = s?.EditSymptomMemo(mark, memo);
        if (live is not null) result = await live.ConfigureAwait(false);
        else
        {
            // 끝난 측정의 노트는 기록 폴더에 쓴다: 옮기는 동안은 쓰지 않고 초안으로 남긴다 (M9 설계 B3 출입문)
            using var ticket = options.WorkGate?.TryEnter();
            var folder = options.WorkGate is not null && ticket is null ? null : NotesFolder(sessionId);
            result = options.WorkGate is not null && ticket is null ? EventWriteState.Pending
                : folder is null ? EventWriteState.Lost
                : await notes.WriteAsync(folder, sessionId, mark, atMs, world.Now, memo, () => MemoEpoch(sessionId, mark) == mine).ConfigureAwait(false);
        }
        // 저장한 그 글일 때만 초안을 지운다: 기다리는 동안 새로 쓴 글은 남긴다 (G4 M5 1라운드 A5)
        if (result == EventWriteState.Saved)
            lock (drafts)
                if (drafts.TryGetValue((sessionId, mark), out var current) && current == (memo ?? "")) drafts.Remove((sessionId, mark));
        if (live is null && result == EventWriteState.Saved) RefreshFinishedMarks(sessionId);
        Publish();
        return result;
    }

    /// <summary>모든 메모 초안을 넣는다 (중지·창 닫기).</summary>
    public async Task FlushDraftsAsync(TimeSpan? limit = null)
    {
        (string Session, string Mark, string Text, long Epoch)[] list;
        // 초안과 세대를 같은 잠금에서 잡는다: 그 뒤 기록 화면이 저장을 시작하면 이 쓰기는 건너뛴다 (G4 M7 1라운드 a1)
        lock (drafts) list = drafts.Select(d => (d.Key.Session, d.Key.Mark, d.Value, memoEpochs.GetValueOrDefault(d.Key))).ToArray();
        // 모두 한 번에 넣고(측정 중이면 세션 통로, 끝났으면 노트 직렬 작업) 기한까지만 기다린다:
        // 저장이 막혀도 중지·종료는 진행하고, 못 끝낸 초안은 남는다 (G4 M5 1라운드 A6)
        var all = Task.WhenAll(list.Select(d => SaveMemoAsync(d.Session, d.Mark, d.Text, d.Epoch)));
        if (limit is { } l) await Task.WhenAny(all, Task.Delay(l)).ConfigureAwait(false);
        else await all.ConfigureAwait(false);
    }

    /// <summary>요약이 있는 조각 폴더 (없으면 마지막 조각).</summary>
    /// <summary>
    /// 그 측정의 노트 폴더. 먼저 정본 규칙(조각 번호마다 앞 위치)으로 조각을 고른 뒤 기록 화면과 같은 규칙(<see cref="RecordMemos.NotesFolder"/>)을 쓴다.
    /// 뒤 위치의 같은 조각 사본에 요약이 있어도 그쪽에 쓰지 않는다 (G4 M9 1라운드 a4: 읽기가 버리는 사본에 메모가 저장될 수 있었다).
    /// </summary>
    private string? NotesFolder(string sessionId)
    {
        var found = RecordReader.FindSegments(sessionId, world.RecordPaths);
        if (found.Count == 0) return null;
        return RecordMemos.NotesFolder(RecordCatalog.EntryFor(sessionId, found, null).Segments);
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

    /// <summary>
    /// 앱 종료: 새 시작을 막고, 진행 중인 시작·중지에 합류해 측정을 끝까지 멈추고 저장한 뒤, 노트 쓰기를 최대 3초 기다린다
    /// (M9 설계 A1 종료 합류 [1-3]: <see cref="StopAsync"/>는 시작 중·멈추는 중에 바로 돌아오므로 종료에서는 이 경로만 쓴다).
    /// UI 문맥을 쓰지 않는다 (세션 종료가 UI 스레드를 막고 기다린다, A6).
    /// </summary>
    public async Task ShutdownAsync()
    {
        Task starting;
        lock (gate)
        {
            startsBlocked = true;
            starting = startDone.Task;
        }
        await starting.ConfigureAwait(false);   // 시작 중이면 성공·실패가 정해질 때까지
        await FlushDraftsAsync(options.DraftFlushLimit).ConfigureAwait(false);
        HostState now;
        lock (gate) now = state;
        if (now is HostState.Measuring || (now is HostState.Starting && options.HoldStart)) await StopAsync().ConfigureAwait(false);
        else if (now is HostState.Stopping) await WaitIdleAsync().ConfigureAwait(false);   // 진행 중인 중지에 합류
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
                // 이벤트 ID와 게시된 쓰기 상태까지 되살린다 (저장 못 한 표시가 저장된 것으로 보이지 않게, 뒤의 상태 변경이 맞는 표시를 찾게)
                for (int i = 0; i < inputs.Events.Count; i++) AddEvent(inputs.Events[i], inputs.EventStates[i].EventId, inputs.EventStates[i].State);
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
            // 신호 사본은 게시마다 새 목록이다: 어댑터 항목(닫힌 묶음 사본은 바뀔 때만 새로 만듦)이 같으면 표시도 그대로 둔다
            if (!SameSignal(signalLive, delta.Signal))
            {
                signalTrace = SignalTrace.FromLive(delta.Signal);
                changed = true;
            }
            signalLive = delta.Signal;
            status = delta.Status;
        }
        if (changed) Publish();
    }

    private static bool SameSignal(SignalLive a, SignalLive b) =>
        ReferenceEquals(a, b) || (a.Configured == b.Configured && a.IntervalMs == b.IntervalMs && a.Adapters.SequenceEqual(b.Adapters));

    /// <summary>gate 안: 끝난 측정의 신호 표시 (기록의 신호 근거에서, 끝난 측정마다 한 번).</summary>
    private SignalTrace FinishedSignal(FinishedMeasurement f, MeasurementRecord record, RecordHeader header)
    {
        if (finishedSignal is { } c && ReferenceEquals(c.For.Record, record)) return c.Trace;
        var trace = SignalTrace.FromEvidence(SignalEvidenceBuilder.FromEvents(record.Events, header.IntervalMs), header.IntervalMs);
        finishedSignal = (f, trace);
        return trace;
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
        Published?.Invoke(next);
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
                finished.Analysis, null, PathMapModel.From(fh.Targets, fRounds, kind, finished.Analysis, null, ended: true), kind, null, finished, startError)
            { Signal = FinishedSignal(finished, fr, fh) };
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
            stats, dv.Cards, analysis, analysisAt, map, connection, status, null, startError) { Signal = signalTrace };
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

    /// <param name="stillWanted">직렬 작업에서 실행될 때 다시 묻는다: 거짓이면 쓰지 않고 저장으로 끝낸다 (더 새 글이 앞이나 뒤에 들어감, G4 M7 1라운드 a1)</param>
    public Task<EventWriteState> WriteAsync(string folder, string measurementId, string mark, long markAtMs, DateTimeOffset editedWall, string? memo, Func<bool>? stillWanted = null)
    {
        var done = new TaskCompletionSource<EventWriteState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            tail = tail.ContinueWith(async _ =>
            {
                for (int attempt = 0; ; attempt++)
                {
                    if (stillWanted?.Invoke() == false) { done.TrySetResult(EventWriteState.Saved); return; }
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
