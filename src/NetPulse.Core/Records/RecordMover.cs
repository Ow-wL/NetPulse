using System.Security.Cryptography;
using System.Text;

namespace NetPulse.Core.Records;

/// <summary>파일 작업 (시험이 고장을 넣는다).</summary>
public interface IMoveOps
{
    bool SameVolume(string a, string b);
    void MoveDirectory(string from, string to);
    void CreateDirectory(string path);
    void CopyFile(string from, string to);
    void DeleteDirectory(string path);
    void RenameDirectory(string from, string to);
}

/// <summary>실제 디스크.</summary>
public class DiskMoveOps : IMoveOps
{
    public virtual bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    public virtual void MoveDirectory(string from, string to) => Directory.Move(from, to);
    public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public virtual void CopyFile(string from, string to) => File.Copy(from, to, overwrite: false);
    public virtual void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
    public virtual void RenameDirectory(string from, string to) => Directory.Move(from, to);
}

/// <summary>옮길 조각 폴더 하나 (<c>id</c> 또는 <c>id~n</c>).</summary>
/// <param name="Segment">머리의 조각 번호 (읽지 못하면 null: 폴더 이름으로만 비교)</param>
/// <param name="Duplicate">더 앞 원본의 같은 조각과 내용이 같은 사본: 옮기지 않고, 앞 원본의 그 측정이 옮겨졌을 때만 지운다</param>
public sealed record MoveSegment(string Name, string Source, string Hash, long Bytes, bool AlreadyThere, int? Segment = null, bool Duplicate = false);

/// <summary>옮길 측정 하나.</summary>
public sealed record MoveItem(string Id, IReadOnlyList<MoveSegment> Segments)
{
    public long Bytes => Segments.Sum(s => s.Bytes);
    public string Hash => RecordMover.Combine(Segments.Select(s => s.Name + "=" + s.Hash));
}

/// <summary>옮기기 계획 (잠근 뒤 다시 만든 것이 유일한 근거, [2-3]).</summary>
/// <param name="Claims">이 원본이 정본으로 내놓는 조각 (측정 Id, 조각 번호) → 해시. 뒤 원본의 계획에 넘긴다</param>
public sealed record MovePlan(MoveKind Kind, string From, string To, IReadOnlyList<MoveItem> Items, IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> ForeignMoving, int LongestRelative, IReadOnlyDictionary<(string Id, int Segment), string>? Claims = null)
{
    public long Bytes => Items.Sum(i => i.Bytes);
    public int Count => Items.Count;
}

/// <summary>옮기기 결과.</summary>
/// <param name="Failed">측정 Id → 이유 (copy, verify, move, read)</param>
/// <param name="Leftovers">옮겼지만 옛 위치에 정리하지 못한 파일이 남은 측정 Id</param>
public sealed record MoveOutcome(IReadOnlyList<string> Moved, IReadOnlyDictionary<string, string> Failed, IReadOnlyList<string> Leftovers, bool Stopped)
{
    public bool AllMoved(MovePlan plan) => !Stopped && Failed.Count == 0 && Moved.Count == plan.Count;
}

/// <summary>
/// 기록 옮기기 (M9 설계 B3 [1-6, 1-7, 2-2, 2-3, 2-8]).
/// <list type="bullet">
/// <item><see cref="Plan"/>: 원본의 측정 폴더(측정 Id 꼴 이름과 <c>id~n</c>)마다 내용 해시(파일 목록·크기·SHA-256)를 내고, 목적지의 같은 이름과 비교해
/// 옮김 / 이미 옮겨짐(같은 내용) / 충돌(다른 내용)으로 나눈다. 우리 표시가 없는 <c>.moving</c>과 겹치는 측정은 뺀다.</item>
/// <item><see cref="Execute"/>: 같은 볼륨은 폴더 이름 바꾸기, 다른 볼륨은 <c>id.moving</c>(표시 파일 포함)에 복사 → 계획의 해시와 대조 → 이름 바꾸기 → 원본 삭제.
/// 삭제가 실패하면 원본을 <c>.moved</c>로, 그것도 실패하면 그대로 둔다(정본 규칙상 목적지가 이긴다).</item>
/// <item>위치(설정)는 부르는 쪽이 <b>옮기기 전에</b> 확정한다 (원본·목적지 둘 다 알려진 위치가 된 뒤에만 파일을 건드린다).</item>
/// </list>
/// </summary>
public static class RecordMover
{
    public const string MovingSuffix = ".moving", MovedSuffix = ".moved", Marker = ".netpulse-moving";

    /// <summary>
    /// 원본 하나의 계획. 같은 측정은 <b>정본 규칙과 같은 기준</b>(머리의 조각 번호)으로 비교한다 (G4 M9 1라운드 a2·a3):
    /// <list type="bullet">
    /// <item>목적지에 같은 조각 번호가 있으면(폴더 이름이 달라도) 내용이 같으면 이미 옮겨짐, 다르면 충돌.</item>
    /// <item>목적지에 같은 이름의 폴더가 있는데 조각 번호가 다르거나 읽지 못하면 내용이 같을 때만 이미 옮겨짐, 아니면 충돌(그 이름으로 놓을 수 없다).</item>
    /// <item><paramref name="earlier"/>(더 앞 원본들의 정본 조각)과 같은 조각 번호면: 내용이 같으면 사본(<see cref="MoveSegment.Duplicate"/>), 다르면 충돌.
    /// 뒤 원본의 다른 내용이 앞 원본을 대신해 정본이 되지 않게 한다.</item>
    /// </list>
    /// </summary>
    public static MovePlan Plan(MoveKind kind, string from, string to, IReadOnlyDictionary<(string Id, int Segment), string>? earlier = null)
    {
        var items = new List<MoveItem>();
        var conflicts = new List<string>();
        var foreign = new List<string>();
        var claims = new Dictionary<(string, int), string>();
        int longest = 0;
        var groups = Groups(from);
        var destGroups = Groups(to);
        foreach (var (id, dirs) in groups)
        {
            if (dirs.Any(d => Directory.Exists(Path.Combine(to, Path.GetFileName(d) + MovingSuffix)) && !IsOurs(Path.Combine(to, Path.GetFileName(d) + MovingSuffix))))
            {
                foreign.Add(id);
                // 옮기지 않아도 정본 정보는 내놓는다 (G4 M9 2라운드 a2): 뒤 원본의 같은 조각은 이것과 비교해 충돌·사본이 되고,
                // 이 측정은 옮겨지지 않았으므로 사본도 지우지 않는다 (뒤 사본이 정본을 대신하지 않는다)
                foreach (var dir in dirs)
                    if (SegmentNumber(dir) is int s && !(earlier?.ContainsKey((id, s)) ?? false)) claims.TryAdd((id, s), FolderHash(dir).Hash);
                continue;
            }
            // 목적지의 같은 측정: 조각 번호 → 폴더 (앞 것)
            var destBySegment = new Dictionary<int, string>();
            foreach (var d in destGroups.GetValueOrDefault(id) ?? [])
                if (SegmentNumber(d) is int n) destBySegment.TryAdd(n, d);
            var segments = new List<MoveSegment>();
            var mine = new Dictionary<(string, int), string>();
            bool conflict = false;
            foreach (var dir in dirs.Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var (hash, bytes, rel) = FolderHash(dir);
                longest = Math.Max(longest, name.Length + MovingSuffix.Length + 1 + rel);
                int? seg = SegmentNumber(dir);
                bool already = false, duplicate = false;
                if (seg is int s0 && earlier is not null && earlier.TryGetValue((id, s0), out var earlierHash))
                {
                    if (earlierHash == hash) duplicate = true;
                    else conflict = true;
                }
                else if (seg is int s1 && destBySegment.TryGetValue(s1, out var sameSegment))
                {
                    if (FolderHash(sameSegment).Hash == hash) already = true;
                    else conflict = true;
                }
                else if (Directory.Exists(Path.Combine(to, name)))
                {
                    if (FolderHash(Path.Combine(to, name)).Hash == hash) already = true;
                    else conflict = true;
                }
                if (seg is int s2 && !duplicate) mine.TryAdd((id, s2), hash);
                segments.Add(new MoveSegment(name, dir, hash, bytes, already, seg, duplicate));
            }
            if (conflict) { conflicts.Add(id); continue; }
            items.Add(new MoveItem(id, segments));
            foreach (var (k, v) in mine) claims.TryAdd(k, v);
        }
        return new MovePlan(kind, from, to, items, conflicts, foreign, longest, claims);
    }

    /// <summary>조각 폴더들을 측정 Id로 묶는다 (<see cref="RecordCatalog.SegmentId"/> 규칙).</summary>
    private static SortedDictionary<string, List<string>> Groups(string folder)
    {
        var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return groups;
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            if (RecordCatalog.SegmentId(Path.GetFileName(dir)) is not { } id) continue;
            if (!groups.TryGetValue(id, out var list)) groups[id] = list = [];
            list.Add(dir);
        }
        return groups;
    }

    /// <summary>조각 폴더 머리의 조각 번호 (읽지 못하면 null).</summary>
    public static int? SegmentNumber(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "measurement.json");
            if (!File.Exists(path)) return null;
            var (h, unsupported) = RecordSchema.ParseHeader(RecordReader.ReadShared(path));
            return unsupported || h is null ? null : h.Segment;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or NullReferenceException or OverflowException) { return null; }
    }

    /// <summary>
    /// 계획대로 옮긴다. 기록 하나가 끝날 때마다 <paramref name="recordDone"/>(일지 저장). 기록 사이·파일 사이에 <paramref name="stop"/>을 본다.
    /// </summary>
    /// <param name="earlierMoved">앞 원본의 그 측정이 옮겨졌는가 (사본 조각을 지워도 되는지). 없으면 사본이 있는 측정은 남긴다</param>
    public static MoveOutcome Execute(MovePlan plan, IMoveOps ops, Action<string> recordDone, CancellationToken stop, Func<string, bool>? earlierMoved = null)
    {
        var moved = new List<string>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        var leftovers = new List<string>();
        // 마지막 방어선: 원본과 목적지가 같은 폴더면 아무것도 지우지 않는다
        if (RecordFolderRules.Same(plan.From, plan.To))
        {
            foreach (var item in plan.Items) failed[item.Id] = "same";
            return new(moved, failed, leftovers, false);
        }
        bool same = ops.SameVolume(plan.From, plan.To);
        foreach (var item in plan.Items)
        {
            if (stop.IsCancellationRequested) return new(moved, failed, leftovers, true);
            bool left = false;
            string? reason = null;
            // 앞 원본의 정본이 옮겨지지 않았으면 사본이 있는 측정은 그대로 둔다 (뒤 사본이 정본을 대신하지 않게)
            if (item.Segments.Any(s => s.Duplicate) && earlierMoved?.Invoke(item.Id) != true) { failed[item.Id] = "shadowed"; continue; }
            foreach (var seg in item.Segments)
            {
                var target = Path.Combine(plan.To, seg.Name);
                try
                {
                    if (seg.AlreadyThere || seg.Duplicate) { left |= !DeleteSource(ops, seg.Source); continue; }
                    if (same)
                    {
                        ops.MoveDirectory(seg.Source, target);
                        if (FolderHash(target).Hash != seg.Hash) { reason = "verify"; break; }   // 이름 바꾸기 중 내용이 바뀜 (다른 프로그램)
                        continue;
                    }
                    var temp = target + MovingSuffix;
                    if (Directory.Exists(temp)) ops.DeleteDirectory(temp);   // 계획이 우리 것만 남겼다
                    ops.CreateDirectory(temp);
                    File.WriteAllText(Path.Combine(temp, Marker), "");
                    if (!CopyTree(ops, seg.Source, temp, stop))
                    {
                        TryDelete(ops, temp);
                        return new(moved, failed, leftovers, true);
                    }
                    if (FolderHash(temp).Hash != seg.Hash) { TryDelete(ops, temp); reason = "verify"; break; }
                    // 최종 이름으로 확정한 뒤 표시를 지운다 (G4 M9 1라운드 a6): 그 사이에 끊겨도 남의 .moving처럼 남지 않는다.
                    // 표시가 최종 폴더에 남으면 다음 실행 정리가 지운다 (내용 해시·읽기는 표시를 보지 않는다)
                    ops.RenameDirectory(temp, target);
                    try { File.Delete(Path.Combine(target, Marker)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    left |= !DeleteSource(ops, seg.Source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDelete(ops, target + MovingSuffix);
                    reason = same ? "move" : "copy";
                    break;
                }
            }
            if (reason is not null) { failed[item.Id] = reason; continue; }
            moved.Add(item.Id);
            if (left) leftovers.Add(item.Id);
            recordDone(item.Id);
        }
        return new(moved, failed, leftovers, false);
    }

    private static bool CopyTree(IMoveOps ops, string from, string to, CancellationToken stop)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            ops.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (stop.IsCancellationRequested) return false;
            ops.CopyFile(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
        return true;
    }

    /// <summary>원본 삭제 규칙: 지우고, 못 지우면 <c>.moved</c>로, 그것도 못 하면 그대로 (false = 옛 파일이 남음).</summary>
    private static bool DeleteSource(IMoveOps ops, string source)
    {
        try { ops.DeleteDirectory(source); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { if (Directory.Exists(source)) ops.RenameDirectory(source, source + MovedSuffix); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static void TryDelete(IMoveOps ops, string path)
    {
        try { if (Directory.Exists(path)) ops.DeleteDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsOurs(string movingFolder) => File.Exists(Path.Combine(movingFolder, Marker));

    /// <summary>
    /// 목적지의 우리 <c>.moving</c> 폴더를 지운다 (다음 실행 복구, B3 흐름 9). 남의 것은 건드리지 않는다.
    /// 최종 이름으로 바꾼 뒤 표시를 지우기 전에 끊긴 조각 폴더의 표시 파일도 지운다.
    /// </summary>
    public static int CleanupOwnMoving(string to)
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(to)) return 0;
            foreach (var d in Directory.EnumerateDirectories(to, "*" + MovingSuffix))
            {
                if (!IsOurs(d)) continue;
                try { Directory.Delete(d, recursive: true); n++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var d in Directory.EnumerateDirectories(to))
            {
                if (RecordCatalog.SegmentId(Path.GetFileName(d)) is null || !IsOurs(d)) continue;
                try { File.Delete(Path.Combine(d, Marker)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return n;
    }

    /// <summary>
    /// 일지 검증 (B3 흐름 9 [2-8]): 한 덩어리로. 맞으면 null, 아니면 이유 (자동 삭제·이동을 하지 않는다).
    /// </summary>
    public static string? ValidateJournal(string kind, string step, string from, string to, IReadOnlyList<string> ids, IReadOnlyList<string> done,
        string? folder, IReadOnlyList<string> extraFolders)
    {
        if (kind is not ("new" or "retry" or "default")) return "kind";
        if (step != "moving") return "step";
        if (!Path.IsPathFullyQualified(from) || !Path.IsPathFullyQualified(to)) return "path";
        if (folder is null || !RecordFolderRules.Same(to, folder)) return "to";
        if (!extraFolders.Any(e => RecordFolderRules.Same(e, from))) return "from";
        if (!Directory.Exists(to)) return "missing";
        if (ids.Any(i => !RecordCatalog.IsIdLike(i)) || done.Any(d => !ids.Contains(d))) return "ids";
        return null;
    }

    /// <summary>폴더 내용 해시: 상대 경로 순으로 (경로|크기|SHA-256). 우리 표시 파일은 뺀다.</summary>
    public static (string Hash, long Bytes, int LongestRelative) FolderHash(string folder)
    {
        var lines = new List<string>();
        long bytes = 0;
        int longest = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(folder, file);
            if (rel == Marker) continue;
            var info = new FileInfo(file);
            using var stream = File.OpenRead(file);
            lines.Add(rel + "|" + info.Length + "|" + Convert.ToHexString(SHA256.HashData(stream)));
            bytes += info.Length;
            longest = Math.Max(longest, rel.Length);
        }
        return (Combine(lines), bytes, longest);
    }

    public static string Combine(IEnumerable<string> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
}
