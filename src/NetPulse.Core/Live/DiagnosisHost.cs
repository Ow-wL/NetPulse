using NetPulse.Core.Diagnostics;

namespace NetPulse.Core.Live;

/// <summary>작업 칸 (M6 설계 3.4): 빠른 진단과 DNS 비우기 흐름은 한 칸에서만 돈다.</summary>
public enum DiagnosisOperation { None, Diagnosing, Flushing }

/// <summary>DNS 비우기 흐름의 단계.</summary>
public enum FlushStage { Waiting, Flushing, Rechecking }

/// <summary>요청의 받아들임.</summary>
public enum DiagnosisRequest { Started, Queued, Busy, Invalid, Closed }

/// <summary>앞 결과: 이번 실행 안의 결과, 또는 진단 기록에서 읽은 것.</summary>
public sealed record PreviousDiagnosis(DateTimeOffset StartedAt, IReadOnlyDictionary<CheckId, CheckJudgment> Judgments, int Conclusion, bool FromRecord);

/// <summary>결과 하나와 그 진단을 시작한 순간의 측정 Id, 비우기 메시지, 저장 결과.</summary>
public sealed record DiagnosisRun(DiagnosisResult Result, string? MeasurementId, SystemActionResult? Flush, DiagnosisSave Save,
    IReadOnlyList<(CheckId Id, CheckJudgment Before, CheckJudgment After)>? Changes, PreviousDiagnosis? Previous);

/// <summary>사이트 점검 칸.</summary>
public sealed record SiteState(string? InputError, bool Running, SiteCheckResult? Result, DateTimeOffset? At)
{
    public static readonly SiteState Empty = new(null, false, null, null);
}

/// <summary>진단 장면 (화면이 읽는 바뀌지 않는 값).</summary>
public sealed record DiagnosisSnapshot(long Version, DiagnosisOperation Operation, FlushStage? Flush, IReadOnlyList<CheckResult> Partial,
    DiagnosisRun? Last, PreviousDiagnosis? Recent, int SkippedRecords, SiteState Site, DiagnosisSettings Settings);

/// <summary>
/// 진단 수명 (M6 설계 3.4, 구현 중 결정 "예약 직후 작업 상태"·"종료 억제 시점").
/// <list type="bullet">
/// <item>빠른 진단 시작: 칸이 비었을 때만. 진단 중·비우기 흐름 중이면 <see cref="DiagnosisRequest.Busy"/>.</item>
/// <item>DNS 비우기(확인 뒤): 비었으면 흐름 시작, 진단 중이면 그 순간 칸을 비우기 흐름(기다림)으로 점유하고 진단이 끝나기를 기다린다. 흐름 중이면 Busy.</item>
/// <item>측정 Id: 진단(흐름 안 다시 진단 포함)을 시작하는 순간 한 번 읽는다.</item>
/// <item>종료: 들어오는 즉시 억제 깃발 → 새 요청 거부, 예약 폐기, 취소, 3초 대기. 억제 뒤 끝난 결과는 게시·저장·알림 모두 하지 않는다.</item>
/// </list>
/// </summary>
public sealed class DiagnosisHost
{
    private readonly DiagnosisWorld world;
    private readonly DiagnosisStore store;
    private readonly Func<string?> runningMeasurementId;
    private readonly Func<DateTimeOffset> now;
    private readonly string appVersion;
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private volatile bool closed;
    private DiagnosisOperation operation;
    private FlushStage? flushStage;
    private List<CheckResult> partial = [];
    private DiagnosisRun? last;
    private PreviousDiagnosis? recent;
    private int skipped;
    private SiteState site = SiteState.Empty;
    private Task current = Task.CompletedTask, siteTask = Task.CompletedTask;
    private long version;
    private DiagnosisSnapshot snapshot;

    public DiagnosisHost(DiagnosisWorld world, DiagnosisStore store, Func<string?> runningMeasurementId, Func<DateTimeOffset> now, string appVersion)
    {
        this.world = world;
        this.store = store;
        this.runningMeasurementId = runningMeasurementId;
        this.now = now;
        this.appVersion = appVersion;
        snapshot = Build();
    }

    public DiagnosisSnapshot Snapshot => Volatile.Read(ref snapshot);
    public DiagnosisWorld World => world;
    public DiagnosisStore Store => store;

    /// <summary>장면이 바뀜 (아무 스레드). 종료 뒤에는 부르지 않는다.</summary>
    public event Action? Changed;

    /// <summary>진단 기록 폴더에서 가장 최근의 읽을 수 있는 기록을 읽는다 (앱 시작 때, 홈 카드·비교용).</summary>
    public void LoadRecent()
    {
        var (latest, skippedCount) = store.Latest();
        lock (gate)
        {
            if (last is null && latest is not null) recent = FromStored(latest);
            skipped = skippedCount;
        }
        Publish();
    }

    // ---------------- 요청 ----------------

    public DiagnosisRequest StartDiagnosis()
    {
        lock (gate)
        {
            if (closed) return DiagnosisRequest.Closed;
            if (operation != DiagnosisOperation.None) return DiagnosisRequest.Busy;
            operation = DiagnosisOperation.Diagnosing;
            partial = [];
            string? measurementId = runningMeasurementId();   // 시작 버튼을 받은 그 순간의 측정 (설계 3.4 [1-7])
            current = Task.Run(() => DiagnoseAsync(measurementId));
        }
        Publish();
        return DiagnosisRequest.Started;
    }

    /// <summary>DNS 캐시 비우기 흐름 (화면이 확인을 받은 뒤에만 부른다).</summary>
    public DiagnosisRequest RequestFlush()
    {
        DiagnosisRequest answer;
        lock (gate)
        {
            if (closed) return DiagnosisRequest.Closed;
            if (operation == DiagnosisOperation.Flushing) return DiagnosisRequest.Busy;
            var waitFor = current;
            answer = operation == DiagnosisOperation.Diagnosing ? DiagnosisRequest.Queued : DiagnosisRequest.Started;
            // 확인을 받는 순간 칸을 원자적으로 점유한다: 앞 진단이 끝나도 None이 게시되지 않는다
            operation = DiagnosisOperation.Flushing;
            flushStage = answer == DiagnosisRequest.Queued ? FlushStage.Waiting : FlushStage.Flushing;
            current = Task.Run(() => FlushFlowAsync(waitFor));
        }
        Publish();
        return answer;
    }

    /// <summary>사이트 점검. 입력 오류면 실행하지 않고 오류 코드를 장면에 남긴다.</summary>
    public DiagnosisRequest CheckSite(string input)
    {
        var (ascii, error) = SiteCheck.Validate(input);
        lock (gate)
        {
            if (closed) return DiagnosisRequest.Closed;
            if (site.Running) return DiagnosisRequest.Busy;
            if (ascii is null)
            {
                site = site with { InputError = error };
            }
            else
            {
                site = new SiteState(null, true, null, null);
                siteTask = Task.Run(() => SiteAsync(ascii));
            }
        }
        Publish();
        return ascii is null ? DiagnosisRequest.Invalid : DiagnosisRequest.Started;
    }

    // ---------------- 작업 ----------------

    private async Task DiagnoseAsync(string? measurementId)
    {
        var run = await RunOnceAsync(null, measurementId).ConfigureAwait(false);
        lock (gate)
        {
            if (closed) return;
            if (run is not null) last = run;
            // 그 사이 비우기 흐름이 칸을 점유했으면 그대로 둔다
            if (operation == DiagnosisOperation.Diagnosing) operation = DiagnosisOperation.None;
        }
        Publish();
    }

    private async Task FlushFlowAsync(Task waitFor)
    {
        try { await waitFor.ConfigureAwait(false); } catch (Exception) { /* 앞 진단의 결과는 그 작업이 다룬다 */ }
        if (BeforeFlushForTest is { } hook) await hook().ConfigureAwait(false);
        // 명령 시작 승인은 종료 진입과 같은 잠금 안에서 한 번만 (G4 2라운드 1): 종료가 먼저 잠금을 잡았으면 시작하지 않고,
        // 승인이 먼저면 그 명령은 "종료 전에 시작한 것"이다 (설계 3.4: 이미 시작한 것은 취소 신호만 받고 끝까지).
        // 실제 명령 쪽도 이미 취소된 토큰이면 프로세스를 띄우지 않는다
        lock (gate)
        {
            if (closed) return;
            flushStage = FlushStage.Flushing;
        }
        Publish();
        SystemActionResult flush;
        try { flush = await world.Actions.FlushDnsAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { flush = new SystemActionResult(false, ex.GetType().Name); }
        string? measurementId;
        lock (gate)
        {
            if (closed) return;
            flushStage = FlushStage.Rechecking; partial = []; measurementId = runningMeasurementId();   // 다시 진단을 시작하는 순간
        }
        Publish();
        var run = await RunOnceAsync(flush, measurementId).ConfigureAwait(false);
        lock (gate)
        {
            if (closed) return;
            if (run is not null) last = run;
            operation = DiagnosisOperation.None;
            flushStage = null;
        }
        Publish();
    }

    /// <summary>진단 한 번: 시작 순간 측정 Id, 진행 알림, 비교, 저장. 종료로 억제되면 null.</summary>
    private async Task<DiagnosisRun?> RunOnceAsync(SystemActionResult? flush, string? measurementId)
    {
        DiagnosisResult result;
        try
        {
            result = await QuickDiagnosis.RunAsync(world.Probes, world.Settings, now, stop.Token, new Reporter(this)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        if (closed) return null;   // 종료 뒤 끝난 결과(취소로 생긴 오류 포함)는 버린다
        PreviousDiagnosis? previous;
        lock (gate) previous = last is { } l ? FromResult(l.Result) : recent;
        var changes = previous is null ? null : Compare(previous, result);
        // 확정(이동)은 종료 진입과 같은 잠금 안에서, 이름 충돌로 다시 해도 매번 확인 (G4 2라운드 2)
        var save = store.Save(result, appVersion, measurementId, () => !closed, gate);
        if (closed) return null;
        return new DiagnosisRun(result, measurementId, flush, save, changes, previous);
    }

    private async Task SiteAsync(string domain)
    {
        SiteCheckResult? result = null;
        try { result = await SiteCheck.RunAsync(world.Probes, domain, world.Settings, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lock (gate)
        {
            if (closed) return;
            site = new SiteState(null, false, result, now());
        }
        Publish();
    }

    private sealed class Reporter(DiagnosisHost host) : IProgress<CheckResult>
    {
        public void Report(CheckResult value)
        {
            lock (host.gate)
            {
                if (host.closed) return;
                host.partial = [.. host.partial, value];
            }
            host.Publish();
        }
    }

    // ---------------- 종료 ----------------

    /// <summary>창 닫기: 억제 → 새 요청 거부 → 예약 폐기 → 취소 → 최대 <paramref name="limit"/> 기다림. 그 뒤 남은 작업은 버린다.</summary>
    public async Task ShutdownAsync(TimeSpan? limit = null)
    {
        Task a, b;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            a = current;
            b = siteTask;
            Changed = null;
        }
        stop.Cancel();
        var both = Task.WhenAll(a, b);
        await Task.WhenAny(both, Task.Delay(limit ?? TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        _ = both.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    // ---------------- 장면 ----------------

    /// <summary>
    /// 장면 게시. 종료 검사·장면 바꾸기·알림을 한 잠금 안에서 한다: 종료(<see cref="ShutdownAsync"/>)가 잠금을 잡은 뒤에는
    /// 어떤 게시도 일어나지 않는다 (미리 읽어 둔 알림 대리자 호출 포함, G4 1라운드 a1). 알림 받는 쪽은 큐에 넣기만 한다.
    /// </summary>
    private void Publish()
    {
        lock (gate)
        {
            if (closed) return;
            Volatile.Write(ref snapshot, Build());
            Changed?.Invoke();
        }
    }

    /// <summary>시험용: 앞 진단을 기다린 뒤, 명령 시작 승인(잠금) 직전에 실행 (종료 끼어들기를 고정한다).</summary>
    internal Func<Task>? BeforeFlushForTest { get; set; }

    /// <summary>gate 안 (생성자에서는 밖).</summary>
    private DiagnosisSnapshot Build() =>
        new(++version, operation, flushStage, partial.ToArray(), last, recent, skipped, site, world.Settings);

    private static PreviousDiagnosis FromResult(DiagnosisResult r) =>
        new(r.StartedAt, r.Checks.ToDictionary(c => c.Id, c => c.Judgment), r.Main.Number, false);

    private static PreviousDiagnosis FromStored(StoredDiagnosis s) =>
        new(s.StartedAt, s.Checks.ToDictionary(c => c.Id, c => c.Judgment), s.MainConclusion, true);

    /// <summary>판정이 바뀐 검사 (앞 결과가 기록에서 왔어도 같은 방식: 검사 판정만 비교).</summary>
    public static IReadOnlyList<(CheckId Id, CheckJudgment Before, CheckJudgment After)> Compare(PreviousDiagnosis before, DiagnosisResult after) =>
        after.Checks.Select(c => (c.Id, Before: before.Judgments.GetValueOrDefault(c.Id, CheckJudgment.Unmeasured), After: c.Judgment))
            .Where(x => x.Before != x.After).ToArray();
}
