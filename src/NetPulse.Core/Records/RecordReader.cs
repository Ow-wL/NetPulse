using System.Globalization;
using System.Text.Json.Nodes;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Records;

public enum RecordStatus { Ok, Damaged, Unreadable, Unsupported }

/// <summary>
/// 읽으며 센 손상 (M3 설계 6.7). 손상 줄은 위치와 상관없이 건너뛰고 종류별로 센다.
/// UnreadableFiles: 있는데 열지 못한 파일(권한·잠금). DamagedEvents는 회차 손상 합계(Total)에 넣지 않는다 (이벤트 손상은 분리).
/// </summary>
public sealed record DamageCounts(int Malformed, int WrongId, int UnknownTarget, int BadCombination, int UnknownValue, int Duplicate, int OutOfOrder, int TruncatedTail,
    int DamagedEvents, int UnreadableFiles = 0)
{
    public int Total => Malformed + WrongId + UnknownTarget + BadCombination + UnknownValue + Duplicate + OutOfOrder + TruncatedTail + UnreadableFiles;
}

/// <summary>읽은 측정 한 회분.</summary>
public sealed record MeasurementRecord(
    RecordStatus Status,
    string? Problem,
    RecordHeader? Header,
    IReadOnlyList<RoundData> Rounds,
    IReadOnlyList<GapSpan> Gaps,
    IReadOnlyList<JsonObject> Events,
    RecordSummary? Summary,
    EndReason EndReason,
    DamageCounts Damage,
    IReadOnlyList<int> MissingSegments,
    MeasurementStats? Stats,
    string? MissingNotice,
    IReadOnlyList<JsonObject>? Notes = null,
    int DamagedNotes = 0)
{
    /// <summary>끝난 뒤 덧붙인 노트 (M5 설계 3.1, <see cref="RecordNotes"/>). 없으면 빈 목록.</summary>
    public IReadOnlyList<JsonObject> NoteLines => Notes ?? [];
}

/// <summary>
/// 기록 읽기 (M3 설계 6.7). 통계는 언제나 원본 회차로 다시 계산한다. 요약은 종료 사유와 관측 수만 쓴다.
/// 조각(segment)은 측정 Id로 여러 위치에서 모아 순서대로 합친다. 위치·조각·파일 하나의 접근 실패는 그것만 빠진 것으로 다룬다.
/// </summary>
public static class RecordReader
{
    private static bool IsAccessFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static bool IsDataFailure(Exception ex) =>
        ex is System.Text.Json.JsonException or FormatException or InvalidOperationException or NullReferenceException or ArgumentException or OverflowException or KeyNotFoundException;

    /// <summary>조각 폴더들(같은 측정 Id, 여러 위치일 수 있음)을 읽는다.</summary>
    public static MeasurementRecord Read(IReadOnlyList<string> segmentFolders)
    {
        var empty = new DamageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var headers = new List<(RecordHeader Header, string Folder)>();
        bool unsupported = false;
        int badHeaders = 0;   // 머리가 없거나 읽지 못한 조각 폴더 (번호를 몰라도 불완전 복구로 표시한다)
        foreach (var folder in segmentFolders)
        {
            var path = Path.Combine(folder, "measurement.json");
            try
            {
                if (!File.Exists(path)) { badHeaders++; continue; }
                var (h, notSupported) = RecordSchema.ParseHeader(ReadShared(path));
                if (notSupported) { unsupported = true; continue; }
                headers.Add((h!, folder));
            }
            catch (Exception ex) when (IsAccessFailure(ex) || IsDataFailure(ex))
            {
                badHeaders++;   // 다른 조각으로 읽는다
            }
        }
        if (headers.Count == 0)
            return unsupported ? Fail(RecordStatus.Unsupported, "지원하지 않는 기록 형식입니다.", empty)
                : Fail(RecordStatus.Unreadable, "measurement.json이 없거나 읽을 수 없습니다.", empty);
        if (headers.Select(h => h.Header.Id).Distinct().Count() > 1) return Fail(RecordStatus.Unreadable, "서로 다른 측정의 조각이 섞여 있습니다.", empty);
        // 같은 조각 번호가 둘이면 앞의 것만 쓴다
        var ordered = headers.GroupBy(h => h.Header.Segment).Select(g => g.First()).OrderBy(h => h.Header.Segment).ToArray();
        var header = ordered[0].Header;
        var missing = Enumerable.Range(0, ordered[^1].Header.Segment + 1).Except(ordered.Select(h => h.Header.Segment)).ToArray();
        var targetIds = new HashSet<string>(header.Targets.Select(t => t.Id));

        int malformed = 0, wrongId = 0, unknownTarget = 0, badCombo = 0, unknownValue = 0, duplicate = 0, outOfOrder = 0, truncated = 0, badEvents = 0, unreadable = 0;
        var rounds = new List<RoundData>();
        var seen = new HashSet<long>();
        long lastRound = -1;
        var events = new List<JsonObject>();
        var notes = new List<JsonObject>();
        int badNotes = 0;
        RecordSummary? summary = null;

        foreach (var (_, folder) in ordered)
        {
            foreach (var file in PartFiles(folder, "rounds"))
            {
                // 줄 단위로 읽는다: 12시간 기록(수 MB)을 한 문자열로 읽고 나누면 큰 객체 힙 할당이 2세대 수집을 일으켜
                // 화면 프레임이 예산을 넘었다 (M7 UI 시험 1차 bench). 여는 데 실패하면 그 파일은 통째로 못 읽음.
                StreamReader reader;
                try { reader = OpenShared(file); }
                catch (Exception ex) when (IsAccessFailure(ex)) { unreadable++; continue; }
                using var fileReader = reader;
                using var lines = Lines(reader).GetEnumerator();
                while (true)
                {
                    string raw;
                    bool isTail;
                    try
                    {
                        if (!lines.MoveNext()) break;
                        (raw, isTail) = lines.Current;
                    }
                    catch (Exception ex) when (IsAccessFailure(ex)) { unreadable++; break; }   // 읽는 도중 실패: 앞부분만 쓴다
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    var (r, problem) = RecordSchema.ParseRound(line, header.Id, targetIds);
                    if (r is null)
                    {
                        if (isTail && problem == RecordSchema.LineProblem.Malformed) truncated++;
                        else switch (problem)
                        {
                            case RecordSchema.LineProblem.WrongId: wrongId++; break;
                            case RecordSchema.LineProblem.UnknownTarget: unknownTarget++; break;
                            case RecordSchema.LineProblem.BadCombination: badCombo++; break;
                            case RecordSchema.LineProblem.UnknownValue: unknownValue++; break;
                            default: malformed++; break;
                        }
                        continue;
                    }
                    if (!seen.Add(r.Round)) { duplicate++; continue; }   // 같은 회차는 첫 줄만
                    if (r.Round < lastRound) { outOfOrder++; continue; }
                    lastRound = r.Round;
                    rounds.Add(r);
                }
            }
            foreach (var file in PartFiles(folder, "events"))
            {
                string text;
                try { text = ReadShared(file); }
                catch (Exception ex) when (IsAccessFailure(ex)) { badEvents++; continue; }
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Trim().Length == 0) continue;
                    if (ParseEvent(line, header.Id) is { } o) events.Add(o);
                    else badEvents++;
                }
            }
            // 끝난 뒤 노트 (없어도 된다). 노트 손상은 기록 손상과 따로 센다 (기록 본문은 그대로이므로)
            var notePath = Path.Combine(folder, RecordNotes.FileName);
            try
            {
                if (File.Exists(notePath))
                    foreach (var raw in ReadShared(notePath).Split('\n'))
                    {
                        var line = raw.TrimEnd('\r');
                        if (line.Trim().Length == 0) continue;
                        if (ParseEvent(line, header.Id) is { } o) notes.Add(o);
                        else badNotes++;
                    }
            }
            catch (Exception ex) when (IsAccessFailure(ex)) { badNotes++; }
            var sum = Path.Combine(folder, "summary.json");
            try
            {
                if (File.Exists(sum)) summary = ParseSummary(ReadShared(sum), header.Id) ?? summary;
            }
            catch (Exception ex) when (IsAccessFailure(ex)) { }
        }

        var gaps = new List<GapSpan>();
        foreach (var e in events.Where(e => (string)e["type"]! == "gap"))
        {
            if (ParseGap(e) is { } g) gaps.Add(g);
            else badEvents++;
        }

        // 쓰는 중에 버린 이벤트는 이벤트 손상으로 센다 (G4 1라운드 8)
        if (summary?.EventsDropped is long droppedEvents && droppedEvents > 0) badEvents += (int)Math.Min(droppedEvents, int.MaxValue - badEvents);
        var damage = new DamageCounts(malformed, wrongId, unknownTarget, badCombo, unknownValue, duplicate, outOfOrder, truncated, badEvents, unreadable + badHeaders);
        var stats = StatsCalculator.Compute(rounds, header.Targets.Select(t => t.Id).ToArray(), gaps);
        var end = summary?.EndReason ?? EndReason.Interrupted;
        string? notice = null;
        int valid = rounds.Count;
        if (summary is not null && summary.Observed != valid)
            notice = $"기록 일부 누락: 관측 {summary.Observed}회차 중 원본 {valid}회차";
        if (missing.Length > 0) notice = (notice is null ? "" : notice + " ") + $"찾지 못한 조각 {missing.Length}개";
        if (badHeaders > 0) notice = (notice is null ? "" : notice + " ") + $"열지 못한 조각 {badHeaders}개";
        if (summary?.EventsDropped is long ed && ed > 0) notice = (notice is null ? "" : notice + " ") + $"이벤트 유실 {ed}개";
        var status = damage.Total > 0 || missing.Length > 0 || damage.DamagedEvents > 0 ? RecordStatus.Damaged : RecordStatus.Ok;
        return new MeasurementRecord(status, null, header, rounds, gaps, events, summary, end, damage, missing, stats, notice, notes, badNotes);
    }

    /// <summary>
    /// 이벤트 한 줄을 검증한다. 공통 필드(id, type 문자열, at 정수, wall 시각, round는 있으면 정수)가 맞아야 받는다.
    /// 종류별 필드는 쓰는 쪽(공백 → <see cref="ParseGap"/>, M4 이후)이 검증한다.
    /// </summary>
    private static JsonObject? ParseEvent(string line, string id)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            if (!Str(o, "id", out var eventId) || eventId != id) return null;
            if (!Str(o, "type", out var type) || type.Length == 0) return null;
            if (o["at"] is not JsonValue at || !at.TryGetValue<long>(out _)) return null;
            if (!Str(o, "wall", out var wall) || !DateTimeOffset.TryParse(wall, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
            if (o["round"] is JsonNode round && (round is not JsonValue rv || !rv.TryGetValue<long>(out _))) return null;
            return o;
        }
        catch (Exception ex) when (IsDataFailure(ex)) { return null; }
    }

    private static bool Str(JsonObject o, string name, out string value)
    {
        value = "";
        if (o[name] is not JsonValue v || !v.TryGetValue<string>(out var s)) return false;
        value = s;
        return true;
    }

    private static GapSpan? ParseGap(JsonObject e)
    {
        try
        {
            if (!Str(e, "reason", out var reason) || reason is not ("stall" or "suspend")) return null;
            if (!Str(e, "fromWall", out var fromWall) || !Str(e, "toWall", out var toWall)) return null;
            long from = e["fromMs"]!.GetValue<long>(), to = e["toMs"]!.GetValue<long>();
            if (to < from) return null;
            long? before = e["lastRoundBefore"] is JsonNode b ? b.GetValue<long>() : null;
            long? after = e["firstRoundAfter"] is JsonNode a ? a.GetValue<long>() : null;
            return new GapSpan(reason == "stall" ? GapReason.Stall : GapReason.Suspend, from, to,
                DateTimeOffset.Parse(fromWall, CultureInfo.InvariantCulture), DateTimeOffset.Parse(toWall, CultureInfo.InvariantCulture), before, after);
        }
        catch (Exception ex) when (IsDataFailure(ex)) { return null; }
    }

    /// <summary>쓰는 중인 파일도 읽는다 (측정 중 보기, 마감 실패로 처리 작업이 아직 쥔 파일).</summary>
    internal static string ReadShared(string path)
    {
        using var reader = OpenShared(path);
        return reader.ReadToEnd();
    }

    private static StreamReader OpenShared(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), System.Text.Encoding.UTF8);

    /// <summary>'\n'으로만 나눈 줄 (Split('\n')과 같은 결과). 마지막 줄이 줄바꿈 없이 끝났으면 Tail (쓰다 끊긴 줄).</summary>
    internal static IEnumerable<(string Line, bool Tail)> Lines(TextReader reader)
    {
        var line = new System.Text.StringBuilder();
        var buffer = new char[16 * 1024];
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != '\n') continue;
                line.Append(buffer, start, i - start);
                yield return (line.ToString(), false);
                line.Clear();
                start = i + 1;
            }
            line.Append(buffer, start, n - start);
        }
        if (line.Length > 0) yield return (line.ToString(), true);
    }

    /// <summary>조각 파일: base.jsonl, base-2.jsonl, … (되돌리기 실패 뒤 이어 쓴 파일, M3 설계 6.5).</summary>
    private static IEnumerable<string> PartFiles(string folder, string baseName)
    {
        var first = Path.Combine(folder, baseName + ".jsonl");
        if (File.Exists(first)) yield return first;
        for (int part = 2; ; part++)
        {
            var next = Path.Combine(folder, $"{baseName}-{part}.jsonl");
            if (!File.Exists(next)) yield break;
            yield return next;
        }
    }

    internal static RecordSummary? ParseSummary(string json, string id)
    {
        try
        {
            var o = JsonNode.Parse(json)!.AsObject();
            if ((string?)o["format"] != RecordSchema.Format || (string?)o["id"] != id) return null;
            var reason = (string)o["endReason"]! switch
            {
                "completed" => EndReason.Completed, "stopped" => EndReason.Stopped, "error" => EndReason.Error, "interrupted" => EndReason.Interrupted,
                _ => throw new FormatException(),
            };
            return new RecordSummary(id, reason, DateTimeOffset.Parse((string)o["endedAt"]!, CultureInfo.InvariantCulture),
                (long)o["observed"]!, (long)o["committed"]!, (long)o["pending"]!, (long)o["dropped"]!, (long)o["unprocessed"]!,
                (long?)o["lastCommittedRound"], (bool)o["sealFailed"]!, (string?)o["error"], (long?)o["eventsDropped"], (long?)o["endMs"]);
        }
        catch (Exception ex) when (IsDataFailure(ex)) { return null; }
    }

    private static MeasurementRecord Fail(RecordStatus status, string problem, DamageCounts d) =>
        new(status, problem, null, [], [], [], null, EndReason.Interrupted, d, [], null, null);

    /// <summary>측정 Id로 여러 위치에서 조각 폴더를 찾는다 (M3 설계 6.6). 접근할 수 없는 위치는 건너뛴다.</summary>
    public static IReadOnlyList<string> FindSegments(string measurementId, IEnumerable<string> locations)
    {
        var found = new List<string>();
        foreach (var location in locations)
        {
            try
            {
                if (!Directory.Exists(location)) continue;
                found.AddRange(Directory.EnumerateDirectories(location, measurementId + "*")
                    .Where(d => Path.GetFileName(d) == measurementId || Path.GetFileName(d).StartsWith(measurementId + "~", StringComparison.Ordinal)));
            }
            catch (Exception ex) when (IsAccessFailure(ex)) { }
        }
        return found;
    }
}
