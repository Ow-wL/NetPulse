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
public sealed class MeasurementSession : IAsyncDisposable
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
    private readonly List<string> pendingEvents = [];
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
    private long committed, dropped, unprocessed, lastRound = -1;
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
            options.Routes, Enqueue);
        powerSubscription = options.Power?.Subscribe(() => gaps.OnSuspend(clock.Elapsed, clock.Now), () => gaps.OnResume(clock.Elapsed, clock.Now));
    }

    public string Id { get; }

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
            throw new SessionStartException("프로필 오류: " + string.Join(", ", issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code)));
        var startedAt = options.Clock.Now;
        var id = (options.NewId ?? RecordLocation.NewId)(startedAt);
        Exception? last = null;
        foreach (var root in new[] { options.Primary, options.Fallback })
        {
            var header = new RecordHeader(id, 0, options.AppVersion, startedAt, (int)startedAt.Offset.TotalMinutes, options.Profile.ActiveTargets,
                (int)Profile.Interval.TotalMilliseconds, (int)options.Profile.Timeout.TotalMilliseconds, (long)options.Profile.Duration.TotalMilliseconds, root.Kind);
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

    public SessionStatus Status
    {
        get
        {
            lock (sync)
                return new SessionStatus(Id, Volatile.Read(ref ending) == 0, lastRound, observed.Count, committed, pending.Count, dropped, unprocessed,
                    storageFailing, root.Kind, gaps.Suspended);
        }
    }

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

    // ---------------- 처리 작업 ----------------

    private async Task WorkAsync()
    {
        var token = workerStop.Token;
        try
        {
            while (await queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (true)
                {
                    RoundSnapshot? item;
                    lock (sync)
                    {
                        if (sealedOff) return;   // 마감 뒤에는 꺼내지 않는다
                        if (!queue.Reader.TryRead(out item)) break;
                    }
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

    private void Process(RoundSnapshot s)
    {
        lock (sync) if (sealedOff) return;
        long startMs = (long)s.Tick.Actual.TotalMilliseconds;
        long endMs = (long)(s.Completed == default ? s.Tick.Actual : s.Completed).TotalMilliseconds;
        var verdict = gaps.OnRound(s.Tick.Index, s.Tick.SkippedBefore, startMs, endMs, s.At);
        foreach (var g in verdict.Closed) WriteGapEvent(g);

        var results = s.Results.Select(x => new TargetResult(x.Target.Id, x.Result.Outcome,
            x.Result.Outcome == ProbeOutcome.Success ? x.Result.RttMs : null,
            x.Result.Outcome == ProbeOutcome.Error ? (string.IsNullOrEmpty(x.Result.Status) ? "error" : x.Result.Status) : null)).ToArray();
        var data = new RoundData(s.Tick.Index, (long)s.Tick.Scheduled.TotalMilliseconds, startMs, s.At, verdict.Skipped, verdict.Gap, results);

        if (s.Routes is not null) WriteRouteChanges(s.Tick.Index, startMs, s.At, s.Routes);
        if (gaps.CheckClock(s.Tick.Actual, s.At) is long delta)
            WriteEvent(RecordSchema.EventLine(Id, "clock", s.Tick.Index, startMs, s.At, new JsonObject { ["deltaMs"] = delta }));

        lock (sync)
        {
            if (sealedOff) return;   // 마감 뒤 결과는 통계·요약에 넣지 않는다
            observed.Add(data);
            lastRound = data.Round;
            pending.AddLast((data.Round, RecordSchema.RoundLine(Id, data)));
        }
        FlushPending(s.Tick.Actual, s.At, fromWorker: true);
    }

    /// <summary>
    /// 대기 회차를 순서대로 쓴다. 실패하면 거기서 멈춘다 (파일 안 순서를 지킨다).
    /// 처리 작업에서 부를 때는 마감되면 바로 멈춘다 (진행 중이던 쓰기 하나만 끝난다).
    /// </summary>
    private bool FlushPending(TimeSpan elapsed, DateTimeOffset wall, bool fromWorker)
    {
        RetryEvents();
        while (true)
        {
            (long Round, string Line) head;
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
        lock (sync) pendingEvents.Insert(0, line);   // 새 조각에는 반드시
        RetryEvents();
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

    private void WriteRouteChanges(long round, long atMs, DateTimeOffset wall, IReadOnlyList<RouteSample> samples)
    {
        foreach (var sample in samples)
        {
            lastRoutes.TryGetValue(sample.TargetId, out var prev);
            // 유형은 30회차마다만 조회하므로, 이번에 없으면 앞의 값을 이어 쓴다 (M3 설계 6.4)
            var effective = sample.Kind is null && prev is not null && sample.Ok && prev.Ok ? sample with { Kind = prev.Kind } : sample;
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
            WriteEvent(RecordSchema.EventLine(Id, "route", round, atMs, wall, fields));
        }
    }

    private void WriteGapEvent(GapSpan g)
    {
        lock (sync) gapSpans.Add(g);
        var fields = new JsonObject
        {
            ["reason"] = g.Reason == GapReason.Stall ? "stall" : "suspend",
            ["fromMs"] = g.FromMs, ["toMs"] = g.ToMs, ["fromWall"] = RecordSchema.Iso(g.FromWall), ["toWall"] = RecordSchema.Iso(g.ToWall),
            ["lastRoundBefore"] = g.LastRoundBefore, ["firstRoundAfter"] = g.FirstRoundAfter,
        };
        WriteEvent(RecordSchema.EventLine(Id, "gap", g.FirstRoundAfter, g.ToMs, g.ToWall, fields));
    }

    /// <summary>이벤트 한 줄. 못 쓰면 메모리에 두고 다음에 다시 쓴다 (상한을 넘으면 오래된 것부터 버린다).</summary>
    private void WriteEvent(string line)
    {
        lock (sync)
        {
            pendingEvents.Add(line);
            while (pendingEvents.Count > options.PendingLimit) pendingEvents.RemoveAt(0);
        }
        RetryEvents();
    }

    private void RetryEvents()
    {
        while (true)
        {
            string line;
            lock (sync)
            {
                if (pendingEvents.Count == 0) return;
                line = pendingEvents[0];
            }
            if (!events.TryAppend(line, out _)) return;
            lock (sync) pendingEvents.RemoveAt(0);
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
        try
        {
            await engine.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            powerSubscription?.Dispose();
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

            var endElapsed = clock.Elapsed;
            var endWall = clock.Now;
            long endMs = Math.Min((long)endElapsed.TotalMilliseconds, header.PlannedMs);
            MeasurementStats stats;
            if (sealFailed)
            {
                // 처리 작업이 아직 돈다: 파일을 건드리지 않는다. 기록은 읽을 때 원본으로 다시 계산된다
                lock (sync) stats = StatsCalculator.Compute(observed.ToArray(), targets.Select(t => t.Id).ToArray(), gapSpans.ToArray());
                long unsavedNow;
                lock (sync) unsavedNow = pending.Count;
                var failed = new SessionResult(Id, reason, null, true, false, unsavedNow, stats, root.Kind, error ?? "기록 마감 실패");
                completion.TrySetResult(failed);
                return failed;
            }

            foreach (var g in gaps.Close(endMs, endWall)) WriteGapEvent(g);
            List<(long From, long To)> ranges;
            lock (sync) ranges = [.. unprocessedRanges];
            foreach (var (from, to) in ranges)
                WriteEvent(RecordSchema.EventLine(Id, "queue", from, (long)endElapsed.TotalMilliseconds, endWall,
                    new JsonObject { ["kind"] = "unprocessed", ["fromRound"] = from, ["toRound"] = to }));

            // 대기 회차를 최대 3초 동안 다시 쓴다. 여기서 예상 밖 오류가 나도 요약은 쓴다 (오류는 결과에 남긴다)
            try
            {
                var retryUntil = DateTime.UtcNow + options.RetryTimeout;
                while (!FlushPending(endElapsed, endWall, fromWorker: false) && DateTime.UtcNow < retryUntil)
                    await Task.Delay(100).ConfigureAwait(false);
                RetryEvents();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error ??= ex.GetType().Name;
            }

            RecordSummary summary;
            lock (sync)
            {
                summary = new RecordSummary(Id, reason, endWall, observed.Count, committed, pending.Count, dropped, unprocessed,
                    lastCommittedRound, false, error);
                stats = StatsCalculator.Compute(observed.ToArray(), targets.Select(t => t.Id).ToArray(), gapSpans.ToArray());
            }
            bool summaryFailed = false;
            try { folder.WriteAllText("summary.json", RecordSchema.SummaryJson(summary)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { summaryFailed = true; }
            rounds.Dispose();
            events.Dispose();
            var result = new SessionResult(Id, reason, summary, false, summaryFailed, summary.Pending, stats, root.Kind, error);
            completion.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            var stats = ComputeStats();
            var result = new SessionResult(Id, EndReason.Error, null, false, true, 0, stats, root.Kind, ex.GetType().Name);
            completion.TrySetResult(result);
            return result;
        }
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
