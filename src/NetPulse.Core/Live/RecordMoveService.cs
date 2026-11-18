using NetPulse.Core.Records;

namespace NetPulse.Core.Live;

/// <summary>옮기기 일지 (설정 파일 records.move와 같은 내용, Core 쪽 표현).</summary>
public sealed record MoveLog(string Kind, string From, string To, IReadOnlyList<string> Ids, IReadOnlyDictionary<string, string> Hashes, IReadOnlyList<string> Done);

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

    /// <summary>종류별 원본들과 목적지.</summary>
    public (IReadOnlyList<string> Sources, string Destination) Route(MoveKind kind, string? picked)
    {
        var f = settings.Read();
        return kind switch
        {
            MoveKind.New => ([world.CurrentRecordFolder], picked ?? throw new ArgumentNullException(nameof(picked))),
            MoveKind.Default => ([world.CurrentRecordFolder], world.DefaultRecordFolder),
            _ => (f.Move is { } m ? [m.From, .. f.Extra.Where(e => !RecordFolderRules.Same(e, m.From))] : f.Extra, world.CurrentRecordFolder),
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
        foreach (var s in sources)
        {
            var plan = RecordMover.Plan(kind, s, dest);
            count += plan.Count; bytes += plan.Bytes; longest = Math.Max(longest, plan.LongestRelative);
            conflicts.AddRange(plan.Conflicts);
        }
        if (kind == MoveKind.Default) Directory.CreateDirectory(dest);
        var reason = RecordFolderRules.Check(dest, kind, Known(), resolve, longest);
        if (reason is null && conflicts.Count > 0) reason = "새 위치에 같은 이름의 다른 기록이 있습니다.";
        return (count, bytes, reason, conflicts);
    }

    /// <summary>옮긴다 (백그라운드에서 부른다). 진행: (옮긴 수, 전체).</summary>
    public async Task<MoveReport> RunAsync(MoveKind kind, string? picked, IProgress<(int Done, int Total)>? progress, CancellationToken stop)
    {
        if (!settings.Writable) return Report(MoveState.NotPersisted, "설정 파일을 저장할 수 없는 실행이라 옮길 수 없습니다.");
        if (host.Snapshot.State is not (HostState.Idle or HostState.NoProfile)) return Report(MoveState.Busy, "측정 중에는 바꿀 수 없습니다.");
        if (!await gate.EnterExclusiveAsync(GateLimit).ConfigureAwait(false)) return Report(MoveState.Busy, "지금은 옮길 수 없습니다 (진행 중인 작업).");
        try
        {
            if (host.Snapshot.State is not (HostState.Idle or HostState.NoProfile)) return Report(MoveState.Busy, "측정 중에는 바꿀 수 없습니다.");
            var (sources, dest) = Route(kind, picked);
            if (kind == MoveKind.Default) Directory.CreateDirectory(dest);
            // 잠근 뒤 다시 판정 [2-3]: 이 결과가 유일한 근거
            var plans = sources.Select(s => RecordMover.Plan(kind, s, dest)).ToArray();
            int longest = plans.Length == 0 ? 0 : plans.Max(p => p.LongestRelative);
            var reason = RecordFolderRules.Check(dest, kind, Known(), resolve, longest);
            if (reason is not null) return Report(MoveState.Invalid, reason);
            var conflicts = plans.SelectMany(p => p.Conflicts).ToArray();
            if (conflicts.Length > 0) return new(MoveState.Conflict, 0, 0, new Dictionary<string, string>(), [], conflicts, "새 위치에 같은 이름의 다른 기록이 있습니다.");

            int total = plans.Sum(p => p.Count), moved = 0;
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
                // 위치·일지를 먼저 [1-6]
                var extra = Clean([plan.From, .. f.Extra], dest);
                if (!Persist(new RecordFolders(Keep(dest), extra, log))) return Report(MoveState.NotPersisted, "설정을 저장하지 못해 아무것도 옮기지 않았습니다.");
                var done = new List<string>();
                var outcome = RecordMover.Execute(plan, Ops, id =>
                {
                    done.Add(id);
                    moved++;
                    settings.Save(settings.Read() with { Move = log with { Done = [.. done] } });
                    progress?.Report((moved, total));
                    AfterRecordForTest?.Invoke(moved);
                }, stop);
                foreach (var (id, why) in outcome.Failed) failed[id] = why;
                leftovers.AddRange(outcome.Leftovers);
                stopped = outcome.Stopped;
                // 정리: 원본에 기록이 남지 않았으면 옛 위치에서 뺀다
                var now = settings.Read();
                var keepExtra = HasRecords(plan.From) ? now.Extra : now.Extra.Where(e => !RecordFolderRules.Same(e, plan.From)).ToArray();
                Persist(new RecordFolders(now.Folder, keepExtra, null));
            }
            var state = stopped ? MoveState.Stopped : failed.Count == 0 && moved == total ? MoveState.Done : moved == 0 ? MoveState.Failed : MoveState.Partial;
            return new(state, moved, total, failed, leftovers, [], null);
        }
        finally { gate.ExitExclusive(); }
    }

    /// <summary>다음 실행 복구 (B3 흐름 9): 일지를 검증하고 맞으면 우리 .moving을 지운다. 맞지 않으면 이유 (자동 동작 없음).</summary>
    public static (bool Interrupted, string? Problem) Recover(RecordFolders f, string currentFolder)
    {
        if (f.Move is not { } m) return (false, null);
        var problem = RecordMover.ValidateJournal(m.Kind, "moving", m.From, m.To, m.Ids, m.Done, currentFolder, f.Extra);
        if (problem is not null) return (true, problem);
        RecordMover.CleanupOwnMoving(m.To);
        return (true, null);
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
            return Directory.Exists(folder) && Directory.EnumerateDirectories(folder).Any(d =>
            {
                var n = Path.GetFileName(d);
                int t = n.IndexOf('~');
                return RecordCatalog.IsIdLike(t > 0 ? n[..t] : n);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static MoveReport Report(MoveState s, string reason) => new(s, 0, 0, new Dictionary<string, string>(), [], [], reason);
}
