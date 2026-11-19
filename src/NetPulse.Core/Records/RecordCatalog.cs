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
                if (SegmentId(Path.GetFileName(dir)) is not { } id) continue;   // 기록 폴더가 아닌 것은 건너뛴다
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

    /// <summary>
    /// 조각 폴더 이름이면 측정 Id: 정확히 <c>id</c> 또는 <c>id~숫자</c>. <c>id~2.moving</c>·<c>id.moved</c> 같은 옮기기 임시·잔여 폴더는 아니다
    /// (G4 M9 1라운드 a8: 불완전한 임시 폴더가 정본으로 뽑힐 수 있었다). 목록·조각 찾기·옮기기가 모두 이 규칙을 쓴다.
    /// </summary>
    /// <summary>그 측정 Id의 조각 폴더 이름인가: 정확히 <c>id</c> 또는 <c>id~숫자</c> (Id 꼴 검사 없이, 이미 아는 Id로 찾을 때).</summary>
    public static bool IsSegmentOf(string folderName, string measurementId)
    {
        if (folderName == measurementId) return true;
        return folderName.Length > measurementId.Length + 1 && folderName.StartsWith(measurementId + "~", StringComparison.Ordinal)
            && folderName[(measurementId.Length + 1)..].All(char.IsAsciiDigit);
    }

    public static string? SegmentId(string folderName)
    {
        int tilde = folderName.IndexOf('~');
        var id = tilde > 0 ? folderName[..tilde] : folderName;
        if (!LooksLikeId(id)) return null;
        if (tilde > 0 && (tilde == folderName.Length - 1 || !folderName[(tilde + 1)..].All(char.IsAsciiDigit))) return null;
        return id;
    }

    private static RecordEntry Entry(string id, List<string> folders, string? runningId)
    {
        RecordHeader? first = null;
        var segments = new List<int>();
        bool unreadableHeader = false, damagedSummary = false;
        RecordSummary? summary = null;
        // 정본 규칙 (M9 설계 B3 [1-7]): 같은 조각 번호가 여러 위치에 있으면 앞 위치(폴더 목록 순서)의 것만 쓴다.
        // 뒤 사본은 항목의 조각에서도 빼서 읽기·요약·메모 쓸 조각이 모두 같은 정본을 본다 (RecordReader도 조각 번호마다 앞 것)
        var kept = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                var path = Path.Combine(folder, "measurement.json");
                if (!File.Exists(path)) { unreadableHeader = true; kept.Add(folder); continue; }
                var (h, unsupported) = RecordSchema.ParseHeader(RecordReader.ReadShared(path));
                if (unsupported || h is null || h.Id != id) { unreadableHeader = true; kept.Add(folder); continue; }
                if (segments.Contains(h.Segment)) continue;   // 뒤 위치의 같은 조각
                kept.Add(folder);
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
                if (!kept.Contains(folder)) kept.Add(folder);
            }
        }
        folders = kept;

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
