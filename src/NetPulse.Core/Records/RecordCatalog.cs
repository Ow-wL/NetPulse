using System.Globalization;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Records;

/// <summary>측정의 수명 (M5 설계 3.3). 요약이 없다는 사실만으로 비정상 종료라고 단정하지 않는다.</summary>
public enum RecordLifecycle
{
    /// <summary>지금 도는 측정 (부르는 쪽이 ID를 넘김)</summary>
    Running,
    /// <summary>요약이 형식 검사를 통과함</summary>
    Ended,
    /// <summary>요약 없음 또는 손상: 비정상 종료일 수도, 요약 쓰기 실패일 수도 있다 ("끝 기록 없음")</summary>
    NoSummary,
}

/// <summary>기록 무결성 (머리·요약·조각 수준. 줄 손상은 <see cref="RecordReader"/>로 읽어야 안다).</summary>
public enum RecordIntegrity { Ok, Damaged, MissingSegments, Unreadable }

/// <summary>기록 목록의 한 항목.</summary>
/// <param name="Segments">이 측정의 조각 폴더들 (위치 여러 곳일 수 있음)</param>
public sealed record RecordEntry(
    string Id, DateTimeOffset? StartedAt, long PlannedMs, IReadOnlyList<Target> Targets, ExpectedConnection? Expected,
    RecordLifecycle Lifecycle, EndReason? EndReason, DateTimeOffset? EndedAt, RecordIntegrity Integrity, IReadOnlyList<string> Segments);

/// <summary>
/// 기록 목록 (M5 설계 3.3). 각 측정 폴더의 <c>measurement.json</c>과 <c>summary.json</c>만 읽는다. 최신순.
/// 같은 측정 ID의 조각(기본·대체 위치, <c>id~n</c> 폴더)은 한 항목으로 묶는다.
/// </summary>
public static class RecordCatalog
{
    public static IReadOnlyList<RecordEntry> List(IEnumerable<string> roots, int max, string? runningId)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> dirs;
            try
            {
                if (!Directory.Exists(root)) continue;
                dirs = Directory.EnumerateDirectories(root).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                int tilde = name.IndexOf('~');
                var id = tilde > 0 ? name[..tilde] : name;
                if (!LooksLikeId(id)) continue;   // 기록 폴더가 아닌 것은 건너뛴다
                if (!groups.TryGetValue(id, out var list)) groups[id] = list = [];
                list.Add(dir);
            }
        }
        return groups.Select(g => Entry(g.Key, g.Value, runningId))
            .OrderByDescending(e => e.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, max))
            .ToArray();
    }

    /// <summary><c>yyyyMMddTHHmmssZ_xxxxxx</c> (RecordLocation.NewId) 또는 시험이 쓰는 같은 꼴.</summary>
    private static bool LooksLikeId(string id) => id.Length >= 16 && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>조각 폴더들로 항목 하나 (다른 위치에서 연 기록도 같은 규칙, M7 설계 3.7).</summary>
    public static RecordEntry EntryFor(string id, IReadOnlyList<string> folders, string? runningId) => Entry(id, folders.ToList(), runningId);

    /// <summary>폴더 이름이 측정 ID 꼴인지 (<c>id</c> 또는 <c>id~n</c>의 앞부분).</summary>
    public static bool IsIdLike(string id) => LooksLikeId(id);

    private static RecordEntry Entry(string id, List<string> folders, string? runningId)
    {
        RecordHeader? first = null;
        var segments = new List<int>();
        bool unreadableHeader = false, damagedSummary = false;
        RecordSummary? summary = null;
        foreach (var folder in folders)
        {
            try
            {
                var path = Path.Combine(folder, "measurement.json");
                if (!File.Exists(path)) { unreadableHeader = true; continue; }
                var (h, unsupported) = RecordSchema.ParseHeader(RecordReader.ReadShared(path));
                if (unsupported || h is null || h.Id != id) { unreadableHeader = true; continue; }
                segments.Add(h.Segment);
                if (first is null || h.Segment < first.Segment) first = h;
                var sum = Path.Combine(folder, "summary.json");
                if (File.Exists(sum))
                {
                    var parsed = RecordReader.ParseSummary(RecordReader.ReadShared(sum), id);
                    if (parsed is null) damagedSummary = true;
                    else summary = parsed;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or NullReferenceException or OverflowException)
            {
                unreadableHeader = true;
            }
        }

        var lifecycle = id == runningId ? RecordLifecycle.Running : summary is not null ? RecordLifecycle.Ended : RecordLifecycle.NoSummary;
        if (first is null)
            return new RecordEntry(id, StartFromId(id), 0, [], null, lifecycle, null, null, RecordIntegrity.Unreadable, folders);
        bool missing = segments.Count > 0 && Enumerable.Range(0, segments.Max() + 1).Except(segments).Any();
        var integrity = missing ? RecordIntegrity.MissingSegments : damagedSummary || unreadableHeader ? RecordIntegrity.Damaged : RecordIntegrity.Ok;
        return new RecordEntry(id, first.StartedAt, first.PlannedMs, first.Targets, first.Expected, lifecycle,
            summary?.EndReason, summary?.EndedAt, integrity, folders);
    }

    /// <summary>머리를 읽지 못하면 폴더 이름의 시각 (UTC)으로 정렬한다.</summary>
    private static DateTimeOffset? StartFromId(string id) =>
        id.Length >= 16 && DateTimeOffset.TryParseExact(id[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
