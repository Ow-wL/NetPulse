using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NetPulse.Core.Records;
using NetPulse.Core.Statistics;
using NetPulse.Core.Targets;
using NetPulse.Core.Timing;

namespace NetPulse.Core.Measurement;

/// <summary>세션을 만들 때 바꿔 끼울 것들 (M3 설계 2절, 9절).</summary>
public sealed record SessionOptions
{
    public required Profile Profile { get; init; }
    public required IClock Clock { get; init; }
    /// <summary>대상의 측정 방식에 맞는 공급자.</summary>
    public required Func<Target, IProbe> ProbeFor { get; init; }
    public required IRecordRoot Primary { get; init; }
    public required IRecordRoot Fallback { get; init; }
    public IRouteObserver? Routes { get; init; }
    /// <summary>링크 직접 관측 (M4 설계 A2). 경로 관측이 쓰는 인터페이스를 감시한다.</summary>
    public NetPulse.Core.Evidence.ILinkObserver? Links { get; init; }
    /// <summary>링크 로그 (M4 설계 A3). 측정 끝에 한 번 읽는다.</summary>
    public NetPulse.Core.Evidence.ILinkLogSource? LinkLog { get; init; }
    /// <summary>측정 끝 뒤 로그가 쌓이기를 기다리는 시간 (기본 3초).</summary>
    public TimeSpan LinkLogSettle { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>로그 조회 제한 (기본 5초).</summary>
    public TimeSpan LinkLogTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>직접 관측 표본 큐 크기 (기본 1024).</summary>
    public int LinkQueueCapacity { get; init; } = MeasurementSession.LinkQueueCapacityDefault;
    /// <summary>Wi‑Fi 신호 읽기 (M12 설계 C). 링크 직접 관측이 감시하는 무선 어댑터를 회차마다 읽는다 (Links가 있어야 쓴다).</summary>
    public NetPulse.Core.Evidence.ISignalSource? Signal { get; init; }
    public IPowerEvents? Power { get; init; }
    public string AppVersion { get; init; } = "0.0.0";
    /// <summary>회차 큐 크기 (기본 3600 = 1시간).</summary>
    public int QueueCapacity { get; init; } = 3600;
    /// <summary>쓰지 못해 메모리에 둔 회차 상한 (넘으면 오래된 것부터 폐기).</summary>
    public int PendingLimit { get; init; } = 300;
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SealTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RetryTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>시험용: 측정 Id를 정한다.</summary>
    public Func<DateTimeOffset, string>? NewId { get; init; }
}

/// <summary>세션을 시작하지 못함 (프로필 오류, 저장 위치 없음).</summary>
public sealed class SessionStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>세션 상태 (화면·트레이용, M3 설계 9절 5).</summary>
public sealed record SessionStatus(
    string Id, bool Running, long LastRound, long Observed, long Committed, long Pending, long Dropped, long Unprocessed,
    bool StorageFailing, string LocationKind, bool InGap);

/// <summary>세션 결과.</summary>
/// <param name="Summary">쓴 요약 (마감 실패면 null)</param>
/// <param name="SealFailed">처리 작업이 제때 멈추지 않아 요약을 쓰지 않음 ("기록 마감 실패")</param>
/// <param name="SummaryWriteFailed">요약 쓰기 실패</param>
/// <param name="Unsaved">끝까지 쓰지 못한 회차 수 (화면이 알린다)</param>
public sealed record SessionResult(
    string Id, EndReason EndReason, RecordSummary? Summary, bool SealFailed, bool SummaryWriteFailed, long Unsaved,
    MeasurementStats Stats, string LocationKind, string? Error);

/// <summary>
/// 측정 한 회분의 수명 (M3 설계 9절).
/// 엔진이 회차를 제한 큐에 넣고, 처리 작업 하나가 순서대로 꺼내 공백 판정 → 경로 이벤트 → 통계(관측) → 기록(확정)을 한다.
/// 회차 상태: 관측 → 확정 / 대기(쓰기 실패, 재시도) / 폐기(대기 상한 초과). 큐에 넣지 못한 회차는 미처리.
/// </summary>
public sealed partial class MeasurementSession : IAsyncDisposable
{
    private readonly SessionOptions options;
    private readonly IClock clock;
    private readonly Profile profile;
    private readonly IReadOnlyList<Target> targets;
    private readonly MeasurementEngine engine;
    private readonly Channel<RoundSnapshot> queue;
    private readonly GapTracker gaps;
    private readonly CancellationTokenSource workerStop = new();
    private readonly TaskCompletionSource<SessionResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object sync = new();
    private readonly List<RoundData> observed = [];
    private readonly List<(long From, long To)> unprocessedRanges = [];
    private readonly LinkedList<(long Round, string Line)> pending = new();
    private readonly List<PendingEvent> pendingEvents = [];
    private readonly Dictionary<string, RouteSample> lastRoutes = [];
    private readonly IDisposable? powerSubscription;
    private RecordHeader header;
    private IRecordRoot root;
    private IRecordFolder folder;
    private LineAppender rounds;
    private LineAppender events;
    private Task worker = Task.CompletedTask;
    private int ending;
    private bool sealedOff;
    private long committed, dropped, unprocessed, lastRound = -1, eventsDropped;
    private long? lastCommittedRound;
    private bool storageFailing;
    private (long From, long To)? dropRange;

    private MeasurementSession(SessionOptions options, string id, IRecordRoot root, IRecordFolder folder, RecordHeader header)
    {
        this.options = options;
        clock = options.Clock;
        profile = options.Profile;
        targets = profile.ActiveTargets;
        Id = id;
        this.root = root;
        this.folder = folder;
        this.header = header;
        rounds = new LineAppender(folder, "rounds");
        events = new LineAppender(folder, "events");
        gaps = new GapTracker(Profile.Interval);
        queue = Channel.CreateBounded<RoundSnapshot>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
        });
        var probes = targets.ToDictionary(t => t.Id, options.ProbeFor);
        long limit = profile.Duration.Ticks / Profile.Interval.Ticks;
        engine = new MeasurementEngine(clock, new DispatchProbe(probes), targets.Select(t => t.ToProbeTarget()).ToArray(), Profile.Interval, limit,
            options.Links is { } links ? new RoutesWithLinks(options.Routes, links, () => directCapability == NetPulse.Core.Evidence.LinkCapability.Supported, ObserveSignal) : options.Routes, Enqueue);
        CreateLinkQueue(options.LinkQueueCapacity);
        linkCutoffMs = header.PlannedMs;   // 계획 끝 뒤의 링크 표본은 처음부터 받지 않는다 (M4 G4 4라운드 2)
        powerSubscription = options.Power?.Subscribe(() => gaps.OnSuspend(clock.Elapsed, clock.Now), () => gaps.OnResume(clock.Elapsed, clock.Now));
    }

    public string Id { get; }

    /// <summary>시험 전용: 처리 작업이 회차를 꺼낸 직후(분류 잠금 전) 부른다.</summary>
    internal Action<long>? TestHookAfterDequeue { get; set; }

    /// <summary>시험 전용: 마감됐는지.</summary>
    internal bool Sealed { get { lock (sync) return sealedOff; } }

    /// <summary>화면용 상태 구독은 엔진에서 직접 한다 (수명은 세션이 소유).</summary>
    public MeasurementEngine Engine => engine;

    /// <summary>세션이 끝나면 완료된다 (계획 완료, 중지, 오류 모두).</summary>
    public Task<SessionResult> Completion => completion.Task;

    /// <summary>
    /// 시작한다 (M3 설계 9절 1~2): 프로필 검증 → 저장 위치 확보 → measurement.json → 엔진 시작.
    /// 기본 위치에 쓸 수 없으면 대체 위치로, 둘 다 안 되면 시작하지 않는다.
    /// </summary>
    public static MeasurementSession Start(SessionOptions options)
    {
        var issues = ProfileValidator.Validate(options.Profile);
        if (ProfileValidator.HasErrors(issues))
            throw new SessionStartException("측정 구성 오류: " + string.Join(" ", issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
        var startedAt = options.Clock.Now;
        var id = (options.NewId ?? RecordLocation.NewId)(startedAt);
        Exception? last = null;
        foreach (var root in new[] { options.Primary, options.Fallback })
        {
            var header = new RecordHeader(id, 0, options.AppVersion, startedAt, (int)startedAt.Offset.TotalMinutes, options.Profile.ActiveTargets,
                (int)Profile.Interval.TotalMilliseconds, (int)options.Profile.Timeout.TotalMilliseconds, (long)options.Profile.Duration.TotalMilliseconds, root.Kind,
                options.Profile.Expected);
            try
            {
                var folder = root.CreateFolder(id) ?? throw new IOException("같은 이름의 기록 폴더가 이미 있습니다.");
                folder.WriteAllText("measurement.json", RecordSchema.HeaderJson(header));
                var session = new MeasurementSession(options, id, root, folder, header);
                session.Begin();
                return session;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                last = ex;
            }
        }
        throw new SessionStartException("기록을 저장할 위치가 없습니다.", last);
    }

    private void Begin()
    {
        StartLinks();
        worker = Task.Run(WorkAsync);
        engine.Start();
        _ = engine.Finished.ContinueWith(_ => EndAsync(EndReason.Completed, null), TaskScheduler.Default);
    }

    /// <summary>사용자 중지.</summary>
    public Task<SessionResult> StopAsync() => EndAsync(EndReason.Stopped, null);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        workerStop.Dispose();
    }

    public SessionStatus Status { get { lock (sync) return StatusUnlocked(); } }

    /// <summary>관측 기준 통계 (지금까지).</summary>
    public MeasurementStats ComputeStats()
    {
        RoundData[] copy;
        GapSpan[] spans;
        lock (sync) { copy = observed.ToArray(); spans = gapSpans.ToArray(); }
        return StatsCalculator.Compute(copy, targets.Select(t => t.Id).ToArray(), spans);
    }

    /// <summary>관측한 회차 (시험·대조용 복사본).</summary>
    public IReadOnlyList<RoundData> ObservedRounds { get { lock (sync) return observed.ToArray(); } }

    private readonly List<GapSpan> gapSpans = [];

    /// <summary>지금까지 닫힌 공백.</summary>
    public IReadOnlyList<GapSpan> Gaps { get { lock (sync) return gapSpans.ToArray(); } }

    // ---------------- 회차 출력 (엔진의 측정 작업에서) ----------------

    private void Enqueue(RoundSnapshot snapshot)
    {
        if (queue.Writer.TryWrite(snapshot)) return;
        // 처리가 큐 크기만큼 밀렸다. 조용히 버리지 않고 미처리로 센다 (M3 설계 6.1)
        lock (sync)
        {
            unprocessed++;
            AddRange(unprocessedRanges, snapshot.Tick.Index);
        }
    }

    private static void AddRange(List<(long From, long To)> ranges, long round)
    {
        if (ranges.Count > 0 && ranges[^1].To == round - 1) ranges[^1] = (ranges[^1].From, round);
        else ranges.Add((round, round));
    }

    /// <summary>범위를 정렬해 이어진 것끼리 합친다 (마감 때 진행 중이던 회차가 뒤늦게 더해질 수 있다).</summary>
    private static List<(long From, long To)> Merge(IEnumerable<(long From, long To)> ranges)
    {
        var merged = new List<(long From, long To)>();
        foreach (var r in ranges.OrderBy(r => r.From))
        {
            if (merged.Count > 0 && r.From <= merged[^1].To + 1) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
            else merged.Add(r);
        }
        return merged;
    }

    // ---------------- 처리 작업 ----------------

    private async Task WorkAsync()
    {
        var token = workerStop.Token;
        var never = new TaskCompletionSource<bool>().Task;
        try
        {
            // 회차와 사용자 이벤트(증상·메모, M5 설계 3.1)를 같은 작업이 쓴다: 디스크 쓰기는 이 작업 한 곳에서만 일어난다
            Task<bool>? roundWait = null, userWait = null;
            while (true)
            {
                roundWait ??= queue.Reader.WaitToReadAsync(token).AsTask();
                userWait ??= userEvents.Reader.WaitToReadAsync(token).AsTask();
                await Task.WhenAny(roundWait, userWait).ConfigureAwait(false);
                if (userWait.IsCompleted)
                {
                    bool more = await userWait.ConfigureAwait(false);
                    userWait = more ? null : never;   // 닫히면 더 기다리지 않는다
                    lock (sync) if (sealedOff) return;
                    DrainUserEvents();
                }
                if (!roundWait.IsCompleted) continue;
                bool rounds = await roundWait.ConfigureAwait(false);
                roundWait = null;
                if (!rounds)
                {
                    lock (sync) if (sealedOff) return;
                    DrainUserEvents();   // 큐가 닫혔다: 들어온 사용자 이벤트를 마저 쓰고 끝낸다
                    return;
                }
                while (true)
                {
                    RoundSnapshot? item;
                    lock (sync)
                    {
                        if (sealedOff) return;   // 마감 뒤에는 꺼내지 않는다
                        if (!queue.Reader.TryRead(out item)) break;
                    }
                    DrainLinks();
                    DrainSignal();
                    DrainUserEvents();
                    Process(item);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _ = EndAsync(EndReason.Error, ex.GetType().Name);
        }
    }

    /// <summary>
    /// 꺼낸 회차 하나. 쓰기 전에 먼저 관측(또는 마감됐으면 미처리)으로 분류한다:
    /// 꺼낸 회차는 이벤트 쓰기가 막혀도 정확히 한 번 어느 한쪽에 들어간다.
    /// </summary>
    private void Process(RoundSnapshot s)
    {
        TestHookAfterDequeue?.Invoke(s.Tick.Index);   // 시험 전용: 꺼낸 뒤·분류 잠금 전에 끼어든다 (M3 이월 3)
        long startMs = (long)s.Tick.Actual.TotalMilliseconds;
        long endMs = (long)(s.Completed == default ? s.Tick.Actual : s.Completed).TotalMilliseconds;
        var results = s.Results.Select(x => new TargetResult(x.Target.Id, x.Result.Outcome,
            x.Result.Outcome == ProbeOutcome.Success ? x.Result.RttMs : null,
            x.Result.Outcome == ProbeOutcome.Error ? (string.IsNullOrEmpty(x.Result.Status) ? "error" : x.Result.Status) : null)).ToArray();
        GapVerdict verdict;
        long? clockDelta;
        long? clockPrevMs;
        RoundData data;

        // 회차 수용 여부와 공백·시각 상태 변경을 같은 마감 경계 안에서 정한다 (디스크 쓰기만 밖에서)
        lock (sync)
        {
            if (sealedOff)
            {
                // 마감 뒤에 분류되는 회차는 통계·요약에 넣지 않고 미처리로 센다. 공백 상태도 건드리지 않는다
                unprocessed++;
                AddRange(unprocessedRanges, s.Tick.Index);
                return;
            }
            verdict = gaps.OnRound(s.Tick.Index, s.Tick.SkippedBefore, startMs, endMs, s.At);
            clockDelta = gaps.CheckClock(s.Tick.Actual, s.At);
            clockPrevMs = gaps.PreviousSampleMs;
            data = new RoundData(s.Tick.Index, (long)s.Tick.Scheduled.TotalMilliseconds, startMs, s.At, verdict.Skipped, verdict.Gap, results,
                s.Completed == default ? null : endMs);
            observed.Add(data);
            gapSpans.AddRange(verdict.Closed);
            lastRound = data.Round;
            pending.AddLast((data.Round, RecordSchema.RoundLine(Id, data)));
        }

        // 이 회차를 처리하며 만든 이벤트는 회차와 한 항목으로 게시한다 (M5 설계 3.2)
        var related = new List<string>();
        foreach (var g in verdict.Closed) WriteGapLine(g, related);
        if (s.Routes is not null) WriteRouteChanges(s.Tick.Index, startMs, s.At, s.Routes, related);
        if (clockDelta is long delta)
            WriteEvent(RecordSchema.EventLine(Id, "clock", s.Tick.Index, startMs, s.At, new JsonObject { ["deltaMs"] = delta, ["prevAtMs"] = clockPrevMs }), related);
        FlushPending(s.Tick.Actual, s.At, fromWorker: true);
        PublishRound(data, related);
    }

    /// <summary>
    /// 대기 회차를 순서대로 쓴다. 실패하면 거기서 멈춘다 (파일 안 순서를 지킨다).
    /// 처리 작업에서 부를 때는 마감되면 바로 멈춘다 (진행 중이던 쓰기 하나만 끝난다).
    /// </summary>
    private bool FlushPending(TimeSpan elapsed, DateTimeOffset wall, bool fromWorker, Stopwatch? until = null, TimeSpan limit = default)
    {
        RetryEvents();
        while (true)
        {
            (long Round, string Line) head;
            if (until is not null && until.Elapsed >= limit) return false;   // 종료 재시도 제한 (줄마다 확인)
            lock (sync)
            {
                if (fromWorker && sealedOff) return false;
                if (pending.Count == 0) break;
                head = pending.First!.Value;
            }
            if (!rounds.TryAppend(head.Line, out var failure))
            {
                OnWriteFailure(failure!, elapsed, wall);
                // 옮겼으면 새 위치에서 한 번 더
                if (!ReferenceEquals(failure, relocatedFor) || !rounds.TryAppend(head.Line, out _)) { TrimPending(elapsed, wall); return false; }
            }
            bool recovered;
            lock (sync)
            {
                pending.RemoveFirst();
                committed++;
                lastCommittedRound = head.Round;
                recovered = storageFailing && pending.Count == 0;
                if (recovered) storageFailing = false;
            }
            if (recovered) WriteEvent(RecordSchema.EventLine(Id, "storage", null, (long)elapsed.TotalMilliseconds, wall, new JsonObject { ["kind"] = "recovered" }));
        }
        return true;
    }

    private Exception? relocatedFor;

    private void OnWriteFailure(Exception failure, TimeSpan elapsed, DateTimeOffset wall)
    {
        bool first;
        long waiting;
        lock (sync) { first = !storageFailing; storageFailing = true; waiting = pending.Count; }
        if (first)
            WriteEvent(RecordSchema.EventLine(Id, "storage", null, (long)elapsed.TotalMilliseconds, wall,
                new JsonObject { ["kind"] = "writeFailed", ["reason"] = LineAppender.Reason(failure), ["pending"] = waiting }));
        if (RecordLocation.ShouldRelocate(failure, root, options.Fallback) && TryRelocate(failure, elapsed, wall)) relocatedFor = failure;
    }

    /// <summary>
    /// 대체 위치로 옮긴다 (M3 설계 6.6, 2-2): 같은 측정 Id로 새 조각 폴더, measurement.json 복제(segment+1),
    /// relocated 이벤트는 새 조각에 반드시, 이전 위치에는 시도만.
    /// </summary>
    private bool TryRelocate(Exception failure, TimeSpan elapsed, DateTimeOffset wall)
    {
        var target = options.Fallback;
        var next = header with { Segment = header.Segment + 1, LocationKind = target.Kind };
        IRecordFolder? created = null;
        try
        {
            created = target.CreateFolder(Id);
            for (int n = 2; created is null && n < 10; n++) created = target.CreateFolder($"{Id}~{n}");
            if (created is null) return false;
            created.WriteAllText("measurement.json", RecordSchema.HeaderJson(next));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }

        var fields = new JsonObject { ["kind"] = "relocated", ["from"] = root.Kind, ["to"] = target.Kind, ["reason"] = LineAppender.Reason(failure) };
        var line = RecordSchema.EventLine(Id, "storage", null, (long)elapsed.TotalMilliseconds, wall, fields);
        events.TryAppend(line, out _);   // 이전 위치에는 시도만
        rounds.Dispose();
        events.Dispose();
        lock (sync)
        {
            root = target;
            folder = created;
            header = next;
        }
        rounds = new LineAppender(created, "rounds");
        events = new LineAppender(created, "events");
        // 새 조각에는 반드시: 대기열 맨 앞으로
        var relocatedId = RegisterEvent(line, null);
        lock (sync)
        {
            int at = pendingEvents.FindIndex(p => p.EventId == relocatedId);
            if (at > 0) { var p = pendingEvents[at]; pendingEvents.RemoveAt(at); pendingEvents.Insert(0, p); }
        }
        RetryEvents();
        PublishEvent(relocatedId);
        return true;
    }

    /// <summary>대기가 상한을 넘으면 오래된 것부터 폐기하고 범위를 이벤트로 남긴다.</summary>
    private void TrimPending(TimeSpan elapsed, DateTimeOffset wall)
    {
        (long From, long To)? range = null;
        lock (sync)
        {
            while (pending.Count > options.PendingLimit)
            {
                var r = pending.First!.Value.Round;
                pending.RemoveFirst();
                dropped++;
                range = range is { } x ? (x.From, r) : (r, r);
            }
            if (range is { } d) dropRange = dropRange is { } prev ? (prev.From, d.To) : d;
        }
        if (range is { } dr)
            WriteEvent(RecordSchema.EventLine(Id, "storage", null, (long)elapsed.TotalMilliseconds, wall,
                new JsonObject { ["kind"] = "dropped", ["fromRound"] = dr.From, ["toRound"] = dr.To }));
    }

    private void WriteRouteChanges(long round, long atMs, DateTimeOffset wall, IReadOnlyList<RouteSample> samples, List<string> related)
    {
        foreach (var sample in samples)
        {
            lastRoutes.TryGetValue(sample.TargetId, out var prev);
            // 유형은 30회차마다만 조회하므로, 이번에 없으면 앞의 값을 이어 쓴다 (M3 설계 6.4).
            // 같은 인터페이스일 때만: 번호가 바뀌었는데 유형이 없으면 미확인(null)으로 남긴다
            var effective = sample.Kind is null && prev is not null && sample.Ok && prev.Ok && sample.InterfaceIndex == prev.InterfaceIndex
                ? sample with { Kind = prev.Kind } : sample;
            bool changed = prev is null || prev.Ok != effective.Ok || prev.InterfaceIndex != effective.InterfaceIndex
                || prev.Kind != effective.Kind || prev.Error != effective.Error;
            lastRoutes[sample.TargetId] = effective;
            if (!changed) continue;
            var fields = new JsonObject
            {
                ["target"] = effective.TargetId, ["interfaceIndex"] = effective.InterfaceIndex, ["kind"] = effective.Kind,
                ["status"] = effective.Ok ? "ok" : "failed",
            };
            if (effective.Error is not null) fields["code"] = effective.Error;
            WriteEvent(RecordSchema.EventLine(Id, "route", round, atMs, wall, fields), related);
        }
    }

    private void WriteGapEvent(GapSpan g)
    {
        lock (sync) gapSpans.Add(g);
        WriteGapLine(g, null);
    }

    private void WriteGapLine(GapSpan g, List<string>? related)
    {
        var fields = new JsonObject
        {
            ["reason"] = g.Reason == GapReason.Stall ? "stall" : "suspend",
            ["fromMs"] = g.FromMs, ["toMs"] = g.ToMs, ["fromWall"] = RecordSchema.Iso(g.FromWall), ["toWall"] = RecordSchema.Iso(g.ToWall),
            ["lastRoundBefore"] = g.LastRoundBefore, ["firstRoundAfter"] = g.FirstRoundAfter,
        };
        WriteEvent(RecordSchema.EventLine(Id, "gap", g.FirstRoundAfter, g.ToMs, g.ToWall, fields), related);
    }

    /// <summary>
    /// 이벤트 한 줄. 못 쓰면 메모리에 두고 다음에 다시 쓴다 (상한을 넘으면 오래된 것부터 버린다).
    /// <paramref name="related"/>가 있으면 회차와 함께 게시하도록 ID만 모으고, 없으면 바로 게시한다 (M5 설계 3.2).
    /// <paramref name="ticket"/>은 첫 쓰기 시도의 결과로 끝난다 (증상·메모).
    /// </summary>
    private void WriteEvent(string line, List<string>? related = null, TaskCompletionSource<EventWriteState>? ticket = null)
    {
        var id = RegisterEvent(line, ticket);
        RetryEvents();
        if (related is not null) related.Add(id);
        else PublishEvent(id);
    }

    private void RetryEvents()
    {
        while (true)
        {
            PendingEvent head;
            lock (sync)
            {
                if (pendingEvents.Count == 0) return;
                head = pendingEvents[0];
            }
            if (!events.TryAppend(head.Line, out _)) return;
            lock (sync)
            {
                // 쓰는 사이 상한으로 버려졌으면 이미 유실로 셌다: 같은 줄일 때만 지운다
                if (pendingEvents.Count > 0 && ReferenceEquals(pendingEvents[0], head)) pendingEvents.RemoveAt(0);
                MarkSaved(head.EventId);
            }
        }
    }

    // ---------------- 끝 ----------------

    /// <summary>
    /// 끝낸다 (M3 설계 6.1, 9절 4). 한 번만 실제로 돌고, 이후 호출은 같은 결과를 기다린다.
    /// 엔진 정지 → 큐 닫기 → 처리 작업이 남은 회차를 다 처리할 때까지(10초) → 넘으면 마감 → 대기 회차 재시도(3초) → summary.json.
    /// </summary>
    private async Task<SessionResult> EndAsync(EndReason reason, string? error)
    {
        if (Interlocked.Exchange(ref ending, 1) != 0) return await completion.Task.ConfigureAwait(false);
        CloseUserInput();   // 이후 증상·메모는 받지 않는다. 이미 들어온 것은 요약 전에 쓴다 (M5 설계 3.1)
        // 측정 끝 경계는 정리(엔진 정지·배출·마감)를 기다리기 전에 고정한다 (M4 G4 2라운드 4)
        var stopElapsed = clock.Elapsed;
        var stopWall = clock.Now;
        // 링크 표본 마감 = 최종 측정 끝과 같은 경계: min(중지 시각, 계획 끝) (M4 G4 4라운드 2)
        Volatile.Write(ref linkCutoffMs, Math.Min((long)stopElapsed.TotalMilliseconds, header.PlannedMs));
        try
        {
            await engine.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            powerSubscription?.Dispose();
            // 알림 해제 (해제 뒤 도착한 콜백은 버린다, M4 설계 A5 종료 순서)
            linkClosed = true;
            try { options.Links?.Dispose(); } catch (Exception) { }
            CloseSignal();
            queue.Writer.TryComplete();
            bool sealFailed = false;
            if (!await Within(worker, options.DrainTimeout).ConfigureAwait(false))
            {
                // 마감: 큐에 남은 회차는 미처리로 확정하고 처리 작업에 중단을 요청한다
                lock (sync)
                {
                    sealedOff = true;
                    while (queue.Reader.TryRead(out var left))
                    {
                        unprocessed++;
                        AddRange(unprocessedRanges, left.Tick.Index);
                    }
                }
                workerStop.Cancel();
                if (!await Within(worker, options.SealTimeout).ConfigureAwait(false)) sealFailed = true;
            }

            var endElapsed = stopElapsed;
            var endWall = stopWall;
            long endMs = Math.Min((long)endElapsed.TotalMilliseconds, header.PlannedMs);
            // 계획 시간 뒤에 끝났으면(계획 뒤 복귀) 끝 경계를 계획 시간으로 맞추고 벽시계도 같은 순간으로 되돌린다
            if ((long)endElapsed.TotalMilliseconds > endMs)
            {
                endWall -= endElapsed - TimeSpan.FromMilliseconds(endMs);
                endElapsed = TimeSpan.FromMilliseconds(endMs);
            }
            MeasurementStats stats;
            if (sealFailed)
            {
                // 처리 작업이 아직 돈다: 파일을 건드리지 않는다. 기록은 읽을 때 원본으로 다시 계산된다
                lock (sync) stats = StatsCalculator.Compute(observed.ToArray(), targets.Select(t => t.Id).ToArray(), gapSpans.ToArray());
                LoseUserEvents();
                long unsavedNow;
                lock (sync) unsavedNow = pending.Count;
                var failed = new SessionResult(Id, reason, null, true, false, unsavedNow, stats, root.Kind, error ?? "기록 마감 실패");
                completion.TrySetResult(failed);
                return failed;
            }

            // 끝 쓰기(공백·미처리 이벤트, 대기 회차 재시도, 요약)는 한 작업에서 하고 전체를 제한한다.
            // 쓰기가 멈춰 돌아오지 않으면 요약 없이 "기록 마감 실패"로 돌려준다 (늦게 끝나도 요약을 쓰지 않는다).
            var finishing = Task.Run(() => FinishWrites(reason, error, endMs, endElapsed, endWall));
            var finishLimit = options.RetryTimeout + options.SealTimeout
                + (options.LinkLog is null ? TimeSpan.Zero : options.LinkLogSettle + options.LinkLogTimeout);
            if (!await Within(finishing, finishLimit).ConfigureAwait(false))
            {
                // 포기와 요약 게시는 같은 잠금에서 하나만 이긴다. 게시(이름 바꾸기)가 이미 시작됐으면 그 결과를 기다린다.
                bool gaveUp;
                long left;
                lock (sync)
                {
                    gaveUp = !publishing;
                    if (gaveUp) abandoned = true;
                    stats = StatsCalculator.Compute(observed.ToArray(), targets.Select(t => t.Id).ToArray(), gapSpans.ToArray());
                    left = pending.Count;
                }
                if (gaveUp)
                {
                    LoseUserEvents();
                    var stuck = new SessionResult(Id, reason, null, true, true, left, stats, root.Kind, error ?? "기록 마감 실패");
                    completion.TrySetResult(stuck);
                    return stuck;
                }
            }
            var (summary, summaryFailed, finalError) = await finishing.ConfigureAwait(false);
            lock (sync) stats = StatsCalculator.Compute(observed.ToArray(), targets.Select(t => t.Id).ToArray(), gapSpans.ToArray());
            error = finalError;
            rounds.Dispose();
            events.Dispose();
            var result = new SessionResult(Id, reason, summary, false, summaryFailed, summary.Pending, stats, root.Kind, error);
            completion.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            LoseUserEvents();
            var stats = ComputeStats();
            var result = new SessionResult(Id, EndReason.Error, null, false, true, 0, stats, root.Kind, ex.GetType().Name);
            completion.TrySetResult(result);
            return result;
        }
    }

    private bool abandoned;    // 종료 쪽이 끝 쓰기를 포기함 (sync 안)
    private bool publishing;   // 요약 게시를 시작함 (sync 안)

    /// <summary>끝 쓰기. 대기 회차 재시도는 RetryTimeout 안에서 줄마다 기한을 확인한다 (단조 시계).</summary>
    private (RecordSummary Summary, bool Failed, string? Error) FinishWrites(EndReason reason, string? error, long endMs, TimeSpan endElapsed, DateTimeOffset endWall)
    {
        try
        {
            DrainUserEvents();   // 처리 작업이 마저 쓰지 못한(마감된) 사용자 이벤트
            foreach (var g in gaps.Close(endMs, endWall)) WriteGapEvent(g);
            FinishSignal(endMs, endWall);
            FinishLinks(endElapsed, endWall);
            List<(long From, long To)> ranges;
            lock (sync) ranges = Merge(unprocessedRanges);
            foreach (var (from, to) in ranges)
                WriteEvent(RecordSchema.EventLine(Id, "queue", from, (long)endElapsed.TotalMilliseconds, endWall,
                    new JsonObject { ["kind"] = "unprocessed", ["fromRound"] = from, ["toRound"] = to }));
            var watch = Stopwatch.StartNew();
            while (!FlushPending(endElapsed, endWall, fromWorker: false, watch, options.RetryTimeout) && watch.Elapsed < options.RetryTimeout)
                Thread.Sleep(100);
            RetryEvents();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error ??= ex.GetType().Name;   // 예상 밖 오류가 나도 요약은 쓴다 (오류는 결과에 남긴다)
        }

        RecordSummary summary;
        lock (sync)
            summary = new RecordSummary(Id, reason, endWall, observed.Count, committed, pending.Count, dropped, unprocessed, lastCommittedRound, false, error,
                eventsDropped + pendingEvents.Count,   // 끝까지 못 쓴 이벤트도 유실
                endMs);                                // 측정 끝 경과 ms (관측해야 했던 회차의 분모, G4 1라운드 1)
        lock (sync) foreach (var p in pendingEvents) MarkLost(p.EventId);
        try
        {
            // 게시 직전에 포기 여부를 확인하고, 아니면 게시 중으로 표시한다 (그 뒤로는 종료 쪽이 포기하지 않는다)
            bool published = folder.WriteAllText("summary.json", RecordSchema.SummaryJson(summary), () =>
            {
                lock (sync)
                {
                    if (abandoned) return false;
                    publishing = true;
                    return true;
                }
            });
            if (!published)
            {
                // 세션이 이미 "기록 마감 실패"로 끝났다: 요약은 게시하지 않고 파일만 닫는다
                rounds.Dispose();
                events.Dispose();
            }
            return (summary, !published, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (summary, true, error); }
    }

    private static async Task<bool> Within(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;

    /// <summary>대상 Id로 측정 공급자를 고른다 (대상마다 측정 방식이 다를 수 있다).</summary>
    private sealed class DispatchProbe(IReadOnlyDictionary<string, IProbe> byTarget) : IProbe
    {
        public Task<ProbeResult> ProbeAsync(ProbeTarget target, long round, CancellationToken cancellationToken) =>
            byTarget[target.Id].ProbeAsync(target, round, cancellationToken);
    }
}
