using System.Globalization;
using System.Text.Json.Nodes;
using NetPulse.Core.Statistics;

namespace NetPulse.Core.Records;

public enum RecordStatus { Ok, Damaged, Unreadable, Unsupported }

/// <summary>읽으며 센 손상 (M3 설계 6.7). 손상 줄은 위치와 상관없이 건너뛰고 종류별로 센다.</summary>
public sealed record DamageCounts(int Malformed, int WrongId, int UnknownTarget, int BadCombination, int UnknownValue, int Duplicate, int OutOfOrder, int TruncatedTail, int DamagedEvents)
{
    public int Total => Malformed + WrongId + UnknownTarget + BadCombination + UnknownValue + Duplicate + OutOfOrder + TruncatedTail;
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
    string? MissingNotice);

/// <summary>
/// 기록 읽기 (M3 설계 6.7). 통계는 언제나 원본 회차로 다시 계산한다. 요약은 종료 사유와 관측 수만 쓴다.
/// 조각(segment)은 측정 Id로 여러 위치에서 모아 순서대로 합친다.
/// </summary>
public static class RecordReader
{
    /// <summary>폴더 하나(조각 하나 이상이 같은 측정 Id로 다른 위치에 있을 수 있다)를 읽는다.</summary>
    public static MeasurementRecord Read(IReadOnlyList<string> segmentFolders)
    {
        var empty = new DamageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var headers = new List<(RecordHeader Header, string Folder)>();
        foreach (var folder in segmentFolders)
        {
            var path = Path.Combine(folder, "measurement.json");
            if (!File.Exists(path)) continue;
            try
            {
                var (h, unsupported) = RecordSchema.ParseHeader(ReadShared(path));
                if (unsupported) return Fail(RecordStatus.Unsupported, "지원하지 않는 기록 형식입니다.", empty);
                headers.Add((h!, folder));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException or NullReferenceException or ArgumentException)
            {
                // 이 조각의 머리를 읽지 못함 (다른 조각이 있으면 그걸로 읽는다)
            }
        }
        if (headers.Count == 0) return Fail(RecordStatus.Unreadable, "measurement.json이 없거나 읽을 수 없습니다.", empty);
        var ids = headers.Select(h => h.Header.Id).Distinct().ToArray();
        if (ids.Length > 1) return Fail(RecordStatus.Unreadable, "서로 다른 측정의 조각이 섞여 있습니다.", empty);
        var ordered = headers.OrderBy(h => h.Header.Segment).ToArray();
        var header = ordered[0].Header;
        var missing = Enumerable.Range(0, ordered[^1].Header.Segment + 1).Except(ordered.Select(h => h.Header.Segment)).ToArray();
        var targetIds = new HashSet<string>(header.Targets.Select(t => t.Id));

        int malformed = 0, wrongId = 0, unknownTarget = 0, badCombo = 0, unknownValue = 0, duplicate = 0, outOfOrder = 0, truncated = 0, badEvents = 0;
        var rounds = new List<RoundData>();
        var seen = new HashSet<long>();
        long lastRound = -1;
        var events = new List<JsonObject>();
        RecordSummary? summary = null;

        foreach (var (h, folder) in ordered)
        {
            foreach (var file in RoundFiles(folder))
            {
                var text = ReadShared(file);
                var lines = text.Split('\n');
                bool endsClean = text.Length == 0 || text.EndsWith('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.Length == 0) continue;
                    bool isTail = i == lines.Length - 1 && !endsClean;
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
            var ev = Path.Combine(folder, "events.jsonl");
            if (File.Exists(ev))
                foreach (var line in ReadShared(ev).Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    try
                    {
                        var o = JsonNode.Parse(line)?.AsObject();
                        if (o is null || (string?)o["id"] != header.Id || o["type"] is null) { badEvents++; continue; }
                        events.Add(o);
                    }
                    catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { badEvents++; }
                }
            var sum = Path.Combine(folder, "summary.json");
            if (File.Exists(sum)) summary = ParseSummary(ReadShared(sum), header.Id) ?? summary;
        }

        var gaps = new List<GapSpan>();
        foreach (var e in events.Where(e => (string?)e["type"] == "gap"))
        {
            try
            {
                gaps.Add(new GapSpan((string?)e["reason"] == "stall" ? GapReason.Stall : GapReason.Suspend, (long)e["fromMs"]!, (long)e["toMs"]!,
                    DateTimeOffset.Parse((string)e["fromWall"]!, CultureInfo.InvariantCulture), DateTimeOffset.Parse((string)e["toWall"]!, CultureInfo.InvariantCulture),
                    (long?)e["lastRoundBefore"], (long?)e["firstRoundAfter"]));
            }
            catch (Exception ex) when (ex is NullReferenceException or FormatException or InvalidOperationException) { badEvents++; }
        }

        var damage = new DamageCounts(malformed, wrongId, unknownTarget, badCombo, unknownValue, duplicate, outOfOrder, truncated, badEvents);
        var stats = StatsCalculator.Compute(rounds, header.Targets.Select(t => t.Id).ToArray(), gaps);
        var end = summary?.EndReason ?? EndReason.Interrupted;
        string? notice = null;
        int valid = rounds.Count;
        if (summary is not null && summary.Observed != valid)
            notice = $"기록 일부 누락: 관측 {summary.Observed}회차 중 원본 {valid}회차";
        if (missing.Length > 0) notice = (notice is null ? "" : notice + " ") + $"찾지 못한 조각 {missing.Length}개";
        var status = damage.Total > 0 || missing.Length > 0 || damage.DamagedEvents > 0 ? RecordStatus.Damaged : RecordStatus.Ok;
        return new MeasurementRecord(status, null, header, rounds, gaps, events, summary, end, damage, missing, stats, notice);
    }

    /// <summary>쓰는 중인 파일도 읽는다 (측정 중 보기, 마감 실패로 처리 작업이 아직 쥔 파일).</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static IEnumerable<string> RoundFiles(string folder)
    {
        var first = Path.Combine(folder, "rounds.jsonl");
        if (File.Exists(first)) yield return first;
        for (int part = 2; ; part++)
        {
            var next = Path.Combine(folder, $"rounds-{part}.jsonl");
            if (!File.Exists(next)) yield break;
            yield return next;
        }
    }

    private static RecordSummary? ParseSummary(string json, string id)
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
                (long?)o["lastCommittedRound"], (bool)o["sealFailed"]!, (string?)o["error"]);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException or NullReferenceException) { return null; }
    }

    private static MeasurementRecord Fail(RecordStatus status, string problem, DamageCounts d) =>
        new(status, problem, null, [], [], [], null, EndReason.Interrupted, d, [], null, null);

    /// <summary>측정 Id로 여러 위치에서 조각 폴더를 찾는다 (M3 설계 6.6).</summary>
    public static IReadOnlyList<string> FindSegments(string measurementId, IEnumerable<string> locations) =>
        locations.Where(Directory.Exists)
            .SelectMany(l => Directory.EnumerateDirectories(l, measurementId + "*"))
            .Where(d => Path.GetFileName(d) == measurementId || Path.GetFileName(d).StartsWith(measurementId + "~", StringComparison.Ordinal))
            .ToArray();
}
