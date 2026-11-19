using NetPulse.Core.Records;

namespace NetPulse.Core.Live;

/// <summary>옮기기 일지 (설정 파일 records.move와 같은 내용, Core 쪽 표현).</summary>
/// <param name="Step">설정 파일에서 읽은 단계 그대로 (복구가 검증한다, G4 M9 1라운드 a7)</param>
public sealed record MoveLog(string Kind, string From, string To, IReadOnlyList<string> Ids, IReadOnlyDictionary<string, string> Hashes, IReadOnlyList<string> Done,
    string Step = "moving");

/// <summary>기록 위치 설정 (설정 파일 records 묶음).</summary>
public sealed record RecordFolders(string? Folder, IReadOnlyList<string> Extra, MoveLog? Move);

/// <summary>설정 파일을 읽고 쓰는 길 (앱이 SettingsStore로 잇는다). 저장은 쓰기 뒤 다시 읽어 확인한 결과.</summary>
public interface IRecordFolderSettings
{
    bool Writable { get; }
    RecordFolders Read();
    bool Save(RecordFolders next);
}

/// <summary>옮기기 결과 상태 (설정 카드가 그린다).</summary>
public enum MoveState { Done, Partial, Stopped, Failed, Busy, Conflict, Invalid, NotPersisted }

public sealed record MoveReport(MoveState State, int Moved, int Total, IReadOnlyDictionary<string, string> Failed, IReadOnlyList<string> Leftovers,
    IReadOnlyList<string> Conflicts, string? Reason);

/// <summary>
/// 기록 저장 위치 바꾸기와 옮기기의 순서 (M9 설계 B3 흐름 3~8):
/// 출입문 독점 → 잠근 뒤 다시 판정(해시) → <b>위치·일지를 먼저 저장·확인</b> → 기록마다 옮기기(일지 갱신) → 정리 → 독점 놓기.
/// 원본·목적지가 둘 다 알려진 위치가 된 뒤에만 파일을 건드리므로 어느 순간 끊겨도 기록을 찾을 수 있다.
/// </summary>
public sealed class RecordMoveService(IAppWorld world, RecordWorkGate gate, MeasurementHost host, IRecordFolderSettings settings, Func<string, string?> resolve)
{
    public static readonly TimeSpan GateLimit = TimeSpan.FromSeconds(10);

    /// <summary>시험 주입: 파일 작업 (기본은 디스크).</summary>
    public IMoveOps Ops { get; set; } = new DiskMoveOps();

    /// <summary>시험 주입: 기록 하나를 옮긴 뒤 (프로세스 강제 종료 등).</summary>
    public Action<int>? AfterRecordForTest { get; set; }

    public KnownLocations Known()
    {
        var f = settings.Read();
        var userData = Path.GetDirectoryName(world.DefaultRecordFolder) ?? world.DefaultRecordFolder;
        return new KnownLocations(world.CurrentRecordFolder, f.Extra, world.FallbackRecordFolder, userData, world.DefaultRecordFolder);
    }

    /// <summary>종류별 원본들과 목적지. 목적지와 같은 위치는 원본에서 뺀다 (자기 자신으로 옮기면 "이미 있음"으로 보고 지운다).</summary>
    public (IReadOnlyList<string> Sources, string Destination) Route(MoveKind kind, string? picked)
    {
        var (sources, dest) = RouteRaw(kind, picked);
        return ([.. sources.Where(s => !SameReal(s, dest))], dest);
    }

    /// <summary>실제 경로까지 풀어 같은 폴더인가 (표기·짧은 이름·정션이 달라도).</summary>
    private bool SameReal(string a, string b) => RecordFolderRules.Same(a, b) || RecordFolderRules.Same(resolve(a) ?? a, resolve(b) ?? b);

    private (IReadOnlyList<string> Sources, string Destination) RouteRaw(MoveKind kind, string? picked)
    {
        var f = settings.Read();
        return kind switch
        {
            MoveKind.New => ([world.CurrentRecordFolder], picked ?? throw new ArgumentNullException(nameof(picked))),
            MoveKind.Default => ([world.CurrentRecordFolder], world.DefaultRecordFolder),
            // 다시 옮기기: 옛 위치들을 읽기 순서(설정의 extraFolders 순서) 그대로. 앞 원본의 정본을 뒤 사본이 대신하지 않게 (G4 M9 1라운드 a3)
            _ => (f.Move is { } m && !f.Extra.Any(e => RecordFolderRules.Same(e, m.From)) ? [m.From, .. f.Extra] : f.Extra, world.CurrentRecordFolder),
        };
    }

    /// <summary>잠그기 전 안내용 판정 (확인 대화상자의 수·크기). 이유가 있으면 Reason.</summary>
    public (int Count, long Bytes, string? Reason, IReadOnlyList<string> Conflicts) Preview(MoveKind kind, string? picked)
    {
        if (!settings.Writable) return (0, 0, "설정 파일을 저장할 수 없는 실행이라 옮길 수 없습니다.", []);
        var (sources, dest) = Route(kind, picked);
        int count = 0, longest = 0;
        long bytes = 0;
        var conflicts = new List<string>();
        var all = Plans(kind, sources, dest);
        foreach (var plan in all)
        {
            bytes += plan.Bytes; longest = Math.Max(longest, plan.LongestRelative);
            conflicts.AddRange(plan.Conflicts);
        }
        count = all.SelectMany(p => p.Items).Select(i => i.Id).Distinct(StringComparer.Ordinal).Count();   // 진행 표시와 같은 측정 단위
        if (kind == MoveKind.Default) Directory.CreateDirectory(dest);
        var reason = RecordFolderRules.Check(dest, kind, Known(), resolve, longest);
        if (reason is null && conflicts.Count > 0) reason = "새 위치에 같은 이름의 다른 기록이 있습니다.";
        return (count, bytes, reason, conflicts);
    }

    private Task running = Task.CompletedTask;

    /// <summary>
    /// 진행 중인 옮기기가 끝나는(되돌림·정리·독점 놓기까지) 작업. 없으면 끝난 작업. UI 문맥을 쓰지 않아 종료 절차가 어느 스레드에서든 기다릴 수 있다
    /// (G4 M9 1라운드 b1: 종료가 이동 정리 완료에 합류하지 않았다).
    /// </summary>
    public Task Running => Volatile.Read(ref running);

    /// <summary>옮긴다 (부른 스레드에서 이어 간다, 시험용). 진행: (옮긴 수, 전체).</summary>
    public async Task<MoveReport> RunAsync(MoveKind kind, string? picked, IProgress<(int Done, int Total)>? progress, CancellationToken stop)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref running, done.Task);
        try { return await RunCoreAsync(kind, picked, progress, stop).ConfigureAwait(false); }
        finally { done.TrySetResult(); }
    }

    /// <summary>
    /// 백그라운드에서 옮긴다. <see cref="Running"/>을 <b>예약하기 전에 이 스레드에서</b> 등록한다 (G4 M9 2라운드 b2: 작업자가 돌기 전에 종료가 오면
    /// 지난 완료 작업을 기다렸다). 이미 멈춤을 받았으면 작업자는 처음에 바로 돌아온다.
    /// </summary>
    public Task<MoveReport> RunInBackground(MoveKind kind, string? picked, IProgress<(int Done, int Total)>? progress, CancellationToken stop)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref running, done.Task);
        var work = Task.Run(() => RunCoreAsync(kind, picked, progress, stop));
        work.ContinueWith(_ => done.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work;
    }

    private async Task<MoveReport> RunCoreAsync(MoveKind kind, string? picked, IProgress<(int Done, int Total)>? progress, CancellationToken stop)
    {
        if (stop.IsCancellationRequested) return Report(MoveState.Stopped, null);
        if (!settings.Writable) return Report(MoveState.NotPersisted, "설정 파일을 저장할 수 없는 실행이라 옮길 수 없습니다.");
        if (host.Snapshot.State is not (HostState.Idle or HostState.NoProfile)) return Report(MoveState.Busy, "측정 중에는 바꿀 수 없습니다.");
        if (!await gate.EnterExclusiveAsync(GateLimit).ConfigureAwait(false)) return Report(MoveState.Busy, "지금은 옮길 수 없습니다 (진행 중인 작업).");
        try
        {
            if (host.Snapshot.State is not (HostState.Idle or HostState.NoProfile)) return Report(MoveState.Busy, "측정 중에는 바꿀 수 없습니다.");
            var (sources, dest) = Route(kind, picked);
            if (kind == MoveKind.Default) Directory.CreateDirectory(dest);
            // 설정의 기록 위치가 실제 위치(세계)와 어긋나 있으면 옮기지 않는다: 원본·목적지 판정의 근거가 무너진다 (G4 M9 2라운드 a1)
            if (!RecordFolderRules.Same(world.CurrentRecordFolder, settings.Read().Folder ?? world.DefaultRecordFolder))
                return Report(MoveState.Invalid, "설정의 기록 위치가 지금 위치와 다릅니다. 앱을 다시 시작하세요.");
            if (stop.IsCancellationRequested) return Report(MoveState.Stopped, null);
            // 잠근 뒤 다시 판정 [2-3]: 이 결과가 유일한 근거
            var plans = Plans(kind, sources, dest);
            // 실행 경계에서도: 원본과 목적지가 같은 폴더면 아무것도 하지 않는다 (자기 기록을 "이미 옮겨짐"으로 지우지 않게)
            if (plans.Any(p => SameReal(p.From, p.To))) return Report(MoveState.Invalid, "옮길 원본이 목적지와 같은 폴더입니다.");
            int longest = plans.Length == 0 ? 0 : plans.Max(p => p.LongestRelative);
            var reason = RecordFolderRules.Check(dest, kind, Known(), resolve, longest);
            if (reason is not null) return Report(MoveState.Invalid, reason);
            var conflicts = plans.SelectMany(p => p.Conflicts).ToArray();
            if (conflicts.Length > 0) return new(MoveState.Conflict, 0, 0, new Dictionary<string, string>(), [], conflicts, "새 위치에 같은 이름의 다른 기록이 있습니다.");

            // 측정 단위로 센다 (G4 M9 2라운드 a3·b5): 같은 측정의 조각이 여러 원본에 나뉘어 있어도 하나
            int total = plans.SelectMany(p => p.Items).Select(i => i.Id).Distinct(StringComparer.Ordinal).Count(), moved = 0;
            // 측정이 다 옮겨졌는가는 그 측정이 든 모든 원본의 결과를 합쳐 정한다 (3라운드 a2·b2): 남은 원본 수가 0이 될 때 센다
            var pending = plans.SelectMany(p => p.Items).GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var movedIds = new HashSet<string>(StringComparer.Ordinal);
            var failed = new Dictionary<string, string>(StringComparer.Ordinal);
            var leftovers = new List<string>();
            bool stopped = false;
            if (plans.Length == 0 || plans.All(p => p.Count == 0))
            {
                // 0개여도 위치는 확정한다 [2-2]
                var f0 = settings.Read();
                var extra0 = Clean([.. sources.Where(s => HasRecords(s)), .. f0.Extra], dest);
                if (!Persist(new RecordFolders(Keep(dest), extra0, null))) return Report(MoveState.NotPersisted, "설정을 저장하지 못해 바꾸지 않았습니다.");
                return new(MoveState.Done, 0, 0, failed, leftovers, [], null);
            }
            foreach (var plan in plans)
            {
                if (stopped) break;
                var f = settings.Read();
                var log = new MoveLog(kind switch { MoveKind.New => "new", MoveKind.Retry => "retry", _ => "default" }, plan.From, dest,
                    plan.Items.Select(i => i.Id).ToArray(), plan.Items.ToDictionary(i => i.Id, i => i.Hash), []);
                // 위치·일지를 먼저 [1-6]. 이미 옛 위치면 그 자리(읽기 순서)를 지킨다, 새 원본(지금 위치였던 곳)은 맨 앞
                var extra = Clean(f.Extra.Any(e => RecordFolderRules.Same(e, plan.From)) ? f.Extra : [plan.From, .. f.Extra], dest);
                if (!Persist(new RecordFolders(Keep(dest), extra, log))) return Report(MoveState.NotPersisted, "설정을 저장하지 못해 아무것도 옮기지 않았습니다.");
                var done = new List<string>();
                var outcome = RecordMover.Execute(plan, Ops, id =>
                {
                    done.Add(id);
                    movedIds.Add(id);
                    if (--pending[id] == 0 && !failed.ContainsKey(id)) moved++;
                    settings.Save(settings.Read() with { Move = log with { Done = [.. done] } });
                    progress?.Report((moved, total));
                    AfterRecordForTest?.Invoke(moved);
                }, stop, movedIds.Contains);
                foreach (var (id, why) in outcome.Failed) failed[id] = why;
                leftovers.AddRange(outcome.Leftovers);
                stopped = outcome.Stopped;
                // 정리: 원본에 기록이 남지 않았으면 옛 위치에서 뺀다
                var now = settings.Read();
                var keepExtra = HasRecords(plan.From) ? now.Extra : now.Extra.Where(e => !RecordFolderRules.Same(e, plan.From)).ToArray();
                Persist(new RecordFolders(now.Folder, keepExtra, null));
            }
            var state = stopped ? MoveState.Stopped : failed.Count == 0 && moved == total ? MoveState.Done : movedIds.Count == 0 ? MoveState.Failed : MoveState.Partial;   // 조각 하나라도 옮겼으면 "하나도 못 옮김"이 아니다
            return new(state, moved, total, failed, leftovers, [], null);
        }
        finally { gate.ExitExclusive(); }
    }

    /// <summary>다음 실행 복구 (B3 흐름 9): 일지를 검증하고 맞으면 우리 .moving을 지운다. 맞지 않으면 이유 (자동 동작 없음).</summary>
    public static (bool Interrupted, string? Problem) Recover(RecordFolders f, string currentFolder)
    {
        if (f.Move is not { } m) return (false, null);
        var problem = RecordMover.ValidateJournal(m.Kind, m.Step, m.From, m.To, m.Ids, m.Done, currentFolder, f.Extra);
        if (problem is not null) return (true, problem);
        RecordMover.CleanupOwnMoving(m.To);
        return (true, null);
    }

    /// <summary>원본들의 계획을 읽기 순서대로: 앞 원본의 정본 조각을 뒤 원본 계획에 넘긴다 (G4 M9 1라운드 a3).</summary>
    private static MovePlan[] Plans(MoveKind kind, IReadOnlyList<string> sources, string dest)
    {
        var claims = new Dictionary<(string Id, int Segment), string>();
        var plans = new List<MovePlan>();
        foreach (var s in sources)
        {
            var plan = RecordMover.Plan(kind, s, dest, claims);
            plans.Add(plan);
            foreach (var (k, v) in plan.Claims ?? new Dictionary<(string, int), string>()) claims.TryAdd(k, v);
        }
        return [.. plans];
    }

    private bool Persist(RecordFolders next)
    {
        if (!settings.Save(next)) return false;
        world.SetRecordFolders(next.Folder, next.Extra);
        return true;
    }

    /// <summary>기본 위치면 null (설정 folder).</summary>
    private string? Keep(string dest) => RecordFolderRules.Same(dest, world.DefaultRecordFolder) ? null : dest;

    private static IReadOnlyList<string> Clean(IEnumerable<string> extra, string dest) =>
        extra.Where(e => !RecordFolderRules.Same(e, dest)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool HasRecords(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateDirectories(folder).Any(d => RecordCatalog.SegmentId(Path.GetFileName(d)) is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static MoveReport Report(MoveState s, string? reason) => new(s, 0, 0, new Dictionary<string, string>(), [], [], reason);
}
