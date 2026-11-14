namespace NetPulse.Core.Records;

/// <summary>다른 위치에서 연 기록의 조각 찾기 결과.</summary>
/// <param name="Segments">한 기록으로 묶은 조각 폴더 (조각 번호순)</param>
/// <param name="Notices">뺀 조각·중복·찾은 조각 수 안내 (경로는 넣지 않는다)</param>
/// <param name="Error">기록으로 읽을 수 없으면 사유 문장</param>
public sealed record ExternalFind(string? Id, IReadOnlyList<string> Segments, IReadOnlyList<string> Notices, string? Error);

/// <summary>
/// 다른 위치의 기록 열기 (M7 설계 3.7 [1-11], 구현 중 결정 5).
/// <list type="bullet">
/// <item>고른 폴더의 측정 ID로 ① 고른 폴더 ② 그 상위 폴더 ③ 알려진 저장 위치(기본·대체 순)에서 같은 ID 조각(<c>id</c>, <c>id~n</c>)을 찾는다.</item>
/// <item>불변 스냅숏(측정 ID, 시작 시각, UTC 차, 대상 전체, 주기, 제한시간, 계획 길이, 예상 연결)이 고른 조각과 같은 조각만 묶는다. 조각 번호·저장 위치 종류는 보지 않는다.</item>
/// <item>같은 조각 번호가 여러 곳에 있으면 위 순서에서 먼저 찾은 쪽.</item>
/// </list>
/// </summary>
public static class RecordLocator
{
    public static ExternalFind FindExternal(string selectedFolder, IReadOnlyList<string> knownRoots)
    {
        var selected = Path.GetFullPath(selectedFolder).TrimEnd(Path.DirectorySeparatorChar);
        var (header, error) = TryHeader(selected);
        if (header is null) return new ExternalFind(null, [], [], error ?? "NetPulse 기록 형식이 아닙니다.");

        var candidates = new List<string> { selected };
        var parent = Path.GetDirectoryName(selected);
        var searchRoots = new List<string>();
        if (parent is not null) searchRoots.Add(parent);
        searchRoots.AddRange(knownRoots);
        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    int tilde = name.IndexOf('~');
                    if ((tilde > 0 ? name[..tilde] : name) == header.Id) candidates.Add(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        var bySegment = new SortedDictionary<int, string>();
        int differing = 0, duplicates = 0;
        foreach (var folder in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (h, _) = TryHeader(folder);
            if (h is null || h.Id != header.Id) continue;
            if (!SameRecord(h, header)) { differing++; continue; }
            if (bySegment.ContainsKey(h.Segment)) { duplicates++; continue; }   // 먼저 찾은 쪽 (고른 폴더 → 상위 → 기본 → 대체)
            bySegment[h.Segment] = folder;
        }

        var notices = new List<string>();
        if (differing > 0) notices.Add($"머리가 다른 조각 {differing}개를 뺐습니다.");
        if (duplicates > 0) notices.Add($"같은 조각이 여러 곳에 있어 고른 위치에 가까운 쪽을 읽었습니다 ({duplicates}개).");
        notices.Add($"찾은 조각 {bySegment.Count}개만 읽었습니다 (끝 기록이 없으면 뒤쪽 조각이 더 있을 수 있습니다).");
        return new ExternalFind(header.Id, bySegment.Values.ToArray(), notices, null);
    }

    /// <summary>불변 스냅숏이 같은지 (조각 번호·저장 위치 종류·앱 버전은 보지 않는다).</summary>
    public static bool SameRecord(RecordHeader a, RecordHeader b) =>
        a.Id == b.Id && a.StartedAt == b.StartedAt && a.UtcOffsetMinutes == b.UtcOffsetMinutes
        && a.IntervalMs == b.IntervalMs && a.TimeoutMs == b.TimeoutMs && a.PlannedMs == b.PlannedMs && a.Expected == b.Expected
        && a.Targets.Count == b.Targets.Count && a.Targets.Zip(b.Targets).All(p => p.First == p.Second);

    private static (RecordHeader? Header, string? Error) TryHeader(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "measurement.json");
            if (!File.Exists(path)) return (null, "NetPulse 기록 형식이 아닙니다 (측정 머리 파일이 없음).");
            var (h, unsupported) = RecordSchema.ParseHeader(RecordReader.ReadShared(path));
            if (unsupported) return (null, "이 앱이 모르는 기록 형식 버전입니다.");
            return h is null ? (null, "NetPulse 기록 형식이 아닙니다.") : (h, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or NullReferenceException or OverflowException)
        {
            return (null, "기록을 읽지 못했습니다.");
        }
    }
}
