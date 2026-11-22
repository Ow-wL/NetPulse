using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPulse.Core.Measurement;
using NetPulse.Core.Targets;

namespace NetPulse.Core.Records;

/// <summary>measurement.json (M3 설계 6.2). 조각마다 복제되고 segment만 다르다.</summary>
public sealed record RecordHeader(
    string Id, int Segment, string AppVersion, DateTimeOffset StartedAt, int UtcOffsetMinutes,
    IReadOnlyList<Target> Targets, int IntervalMs, int TimeoutMs, long PlannedMs, string LocationKind, ExpectedConnection? Expected = null);

public enum EndReason { Completed, Stopped, Error, Interrupted }

/// <summary>summary.json. 통계는 읽을 때 원본으로 다시 계산하므로 여기 값은 관측 기준 기록과 종료 사유용이다.</summary>
public sealed record RecordSummary(
    string Id, EndReason EndReason, DateTimeOffset EndedAt,
    long Observed, long Committed, long Pending, long Dropped, long Unprocessed,
    long? LastCommittedRound, bool SealFailed, string? Error, long? EventsDropped = null, long? EndMs = null);

/// <summary>
/// 기록 형식 버전 1의 직렬화 규칙 (M3 설계 6.2~6.3). 줄 단위 JSON. 모르는 필드는 무시하고, 알려진 필드의 모르는 값은 손상으로 본다.
/// 같은 버전 안에서 필드를 더할 때는 없을 때의 의미를 여기 정한다: skipped 없음 = 0, gap 없음 = false,
/// (M4) 머리 expected 없음 = 미기록(null, "상관없음"과 다름), 회차 end 없음 = 미기록(null, 읽는 쪽이 추정), 요약 eventsDropped 없음 = 미기록(null, 0이 아님).
/// </summary>
public static class RecordSchema
{
    public const string Format = "netpulse.measurement";
    public const int Version = 1;

    public static string OutcomeText(ProbeOutcome o) => o switch { ProbeOutcome.Success => "ok", ProbeOutcome.Lost => "lost", _ => "error" };

    public static string RoundLine(string id, RoundData r)
    {
        var results = new JsonArray();
        foreach (var x in r.Results)
        {
            var o = new JsonObject { ["t"] = x.TargetId, ["s"] = OutcomeText(x.Outcome) };
            if (x.RttMs is double ms) o["ms"] = Math.Round(ms, 3);
            if (x.Error is not null) o["e"] = x.Error;
            results.Add(o);
        }
        var line = new JsonObject
        {
            ["id"] = id, ["r"] = r.Round, ["due"] = r.DueMs, ["at"] = r.AtMs, ["wall"] = Iso(r.Wall), ["skipped"] = r.Skipped,
            ["results"] = results,
        };
        if (r.Gap) line["gap"] = true;
        if (r.EndMs is long end) line["end"] = end;
        return line.ToJsonString();
    }

    public static string EventLine(string id, string type, long? round, long atMs, DateTimeOffset wall, JsonObject? fields = null)
    {
        var o = new JsonObject { ["id"] = id, ["type"] = type, ["at"] = atMs, ["wall"] = Iso(wall) };
        if (round is long r) o["round"] = r;
        if (fields is not null) foreach (var (k, v) in fields.ToArray()) { fields.Remove(k); o[k] = v; }
        return o.ToJsonString();
    }

    public static string HeaderJson(RecordHeader h)
    {
        var targets = new JsonArray();
        foreach (var t in h.Targets)
            targets.Add(new JsonObject
            {
                ["id"] = t.Id, ["name"] = t.Name, ["address"] = t.Address.ToString(), ["role"] = t.Role == TargetRole.Local ? "local" : "external",
                ["provider"] = t.Provider, ["method"] = t.Method.ToString(),
            });
        return new JsonObject
        {
            ["format"] = Format, ["version"] = Version, ["id"] = h.Id, ["segment"] = h.Segment, ["appVersion"] = h.AppVersion,
            ["startedAt"] = Iso(h.StartedAt), ["utcOffsetMinutes"] = h.UtcOffsetMinutes, ["targets"] = targets,
            ["intervalMs"] = h.IntervalMs, ["timeoutMs"] = h.TimeoutMs, ["plannedMs"] = h.PlannedMs, ["location"] = h.LocationKind,
            ["expected"] = h.Expected is { } e ? ExpectedText(e) : null,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ExpectedText(ExpectedConnection e) => e switch { ExpectedConnection.Wired => "wired", ExpectedConnection.Wireless => "wireless", _ => "any" };

    public static string SummaryJson(RecordSummary s) => new JsonObject
    {
        ["format"] = Format, ["version"] = Version, ["id"] = s.Id, ["endReason"] = s.EndReason.ToString().ToLowerInvariant(),
        ["endedAt"] = Iso(s.EndedAt), ["observed"] = s.Observed, ["committed"] = s.Committed, ["pending"] = s.Pending, ["dropped"] = s.Dropped,
        ["unprocessed"] = s.Unprocessed, ["lastCommittedRound"] = s.LastCommittedRound, ["sealFailed"] = s.SealFailed, ["error"] = s.Error,
        ["eventsDropped"] = s.EventsDropped, ["endMs"] = s.EndMs,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // ---------------- 읽기 ----------------

    /// <summary>머리를 읽는다. 형식이 다르거나 버전이 높으면 Unsupported, 필수 필드가 틀리면 예외.</summary>
    public static (RecordHeader? Header, bool Unsupported) ParseHeader(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        if ((string?)o["format"] != Format || o["version"] is not JsonValue v || v.GetValue<int>() > Version || v.GetValue<int>() < 1) return (null, true);
        var targets = o["targets"]!.AsArray().Select(n =>
        {
            var t = n!.AsObject();
            return new Target((string)t["id"]!, (string)t["name"]!, IPAddress.Parse((string)t["address"]!),
                (string)t["role"]! switch { "local" => TargetRole.Local, "external" => TargetRole.External, var x => throw new FormatException("모르는 역할 " + x) },
                (string?)t["provider"], ProfileStore.ParseMethod((string)t["method"]!));
        }).ToArray();
        var header = new RecordHeader((string)o["id"]!, (int)o["segment"]!, (string)o["appVersion"]!, DateTimeOffset.Parse((string)o["startedAt"]!, CultureInfo.InvariantCulture),
            (int)o["utcOffsetMinutes"]!, targets, (int)o["intervalMs"]!, (int)o["timeoutMs"]!, (long)o["plannedMs"]!, (string?)o["location"] ?? "unknown",
            (string?)o["expected"] switch
            {
                null => null, "any" => ExpectedConnection.Any, "wired" => ExpectedConnection.Wired, "wireless" => ExpectedConnection.Wireless,
                var x => throw new FormatException("모르는 예상 연결 유형 " + x),
            });
        Check(header);
        return (header, false);
    }

    /// <summary>머리의 의미 검증. 틀리면 FormatException (읽을 수 없는 기록).</summary>
    private static void Check(RecordHeader h)
    {
        static void Require(bool ok, string what) { if (!ok) throw new FormatException("기록의 기본 정보 손상: " + what); }
        Require(!string.IsNullOrWhiteSpace(h.Id), "id");
        Require(h.Segment is >= 0 and <= MaxSegment, "segment");
        Require(!string.IsNullOrWhiteSpace(h.AppVersion), "appVersion");
        Require(h.UtcOffsetMinutes is >= -14 * 60 and <= 14 * 60, "utcOffsetMinutes");
        Require(h.Targets.Count > 0, "targets");
        Require(h.Targets.All(t => !string.IsNullOrWhiteSpace(t.Id)), "target id");
        Require(h.Targets.All(t => !string.IsNullOrWhiteSpace(t.Name)), "target name");
        Require(!string.IsNullOrWhiteSpace(h.LocationKind), "location");
        Require(h.Targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() == h.Targets.Count, "target id 중복");
        Require(h.IntervalMs > 0 && h.TimeoutMs > 0 && h.PlannedMs > 0, "시간 설정");
    }

    /// <summary>조각 번호 상한 (손상된 큰 값으로 누락 조각 목록이 터지지 않게).</summary>
    public const int MaxSegment = 1000;

    public enum LineProblem { None, Malformed, WrongId, UnknownTarget, BadCombination, UnknownValue }

    /// <summary>
    /// 회차 한 줄을 검증하며 읽는다 (M3 설계 6.7). 문제가 있으면 null과 종류.
    /// JsonDocument로 읽는다: JsonNode는 줄마다 사전과 노드를 만들어 12시간 기록(4만 줄)을 열 때 GC가 화면 프레임을 멈췄다
    /// (M7 UI 시험 1차 bench). 판정은 JsonNode 때와 같고, 예외로 새던 두 경우(같은 이름 속성, wall 없음)는 깨진 줄로 센다.
    /// </summary>
    public static (RoundData? Round, LineProblem Problem) ParseRound(string line, string id, IReadOnlySet<string> targetIds)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return (null, LineProblem.Malformed); }
        using (doc)
        {
            var o = doc.RootElement;
            try
            {
                if (o.ValueKind != JsonValueKind.Object || HasDuplicateNames(o)) return (null, LineProblem.Malformed);
                if (!Is(o, "id", id)) return (null, LineProblem.WrongId);
                long r = Num(o, "r"), due = Num(o, "due"), at = Num(o, "at");
                if (Str(o, "wall") is not { } wallText) return (null, LineProblem.Malformed);
                var wall = DateTimeOffset.Parse(wallText, CultureInfo.InvariantCulture);
                long skipped = Opt(o, "skipped") is { } s ? s.GetInt64() : 0;
                bool gap = Opt(o, "gap") is { } g && g.GetBoolean();
                long? end = Opt(o, "end") is { } en ? en.GetInt64() : null;
                if (Opt(o, "results") is not { ValueKind: JsonValueKind.Array } list) return (null, LineProblem.Malformed);
                // 결과는 정확한 크기의 배열, 대상 이름은 대상 목록의 같은 문자열을 쓴다 (4만 회차 × 대상마다 새 문자열·리스트가
                // 오래 살아남아 1세대 수집이 길어졌다)
                var results = new TargetResult[list.GetArrayLength()];
                int count = 0;
                foreach (var x in list.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object || HasDuplicateNames(x)) return (null, LineProblem.Malformed);
                    string? t = Known(x, "t", targetIds);
                    if (t is null) return (null, LineProblem.UnknownTarget);
                    ProbeOutcome outcome;
                    if (Is(x, "s", "ok")) outcome = ProbeOutcome.Success;
                    else if (Is(x, "s", "lost")) outcome = ProbeOutcome.Lost;
                    else if (Is(x, "s", "error")) outcome = ProbeOutcome.Error;
                    else return (null, LineProblem.UnknownValue);
                    double? ms = Opt(x, "ms") is { } m ? m.GetDouble() : null;
                    string? e = Str(x, "e");
                    bool consistent = outcome switch
                    {
                        ProbeOutcome.Success => ms is >= 0,
                        ProbeOutcome.Lost => ms is null,
                        _ => ms is null && !string.IsNullOrEmpty(e),
                    };
                    if (!consistent) return (null, LineProblem.BadCombination);
                    results[count++] = new TargetResult(t, outcome, ms, e);
                }
                if (r < 0 || skipped < 0 || results.GroupBy(x => x.TargetId).Any(gr => gr.Count() > 1)) return (null, LineProblem.BadCombination);
                if (end is long e2 && e2 < at) return (null, LineProblem.BadCombination);   // 끝이 시작보다 이름
                return (new RoundData(r, due, at, wall, skipped, gap, results, end), LineProblem.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or KeyNotFoundException)
            {
                return (null, LineProblem.Malformed);
            }
        }
    }

    /// <summary>값이 있고 null이 아니면 그 값 (JsonNode에서 null 값과 없는 속성이 같았던 것과 맞춘다).</summary>
    private static JsonElement? Opt(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    /// <summary>글 값이 <paramref name="expected"/>와 같은가 (새 문자열을 만들지 않는다). 글이 아니면 InvalidOperationException → 깨진 줄.</summary>
    private static bool Is(JsonElement o, string name, string expected) =>
        Opt(o, name) is { } v && (v.ValueKind == JsonValueKind.String ? v.ValueEquals(expected) : throw new InvalidOperationException(name));

    /// <summary>대상 목록에 있는 이름이면 그 목록의 문자열, 없으면(또는 값 없음) null. 글이 아니면 InvalidOperationException.</summary>
    private static string? Known(JsonElement o, string name, IReadOnlySet<string> names)
    {
        if (Opt(o, name) is not { } v) return null;
        if (v.ValueKind != JsonValueKind.String) throw new InvalidOperationException(name);
        foreach (var n in names) if (v.ValueEquals(n)) return n;
        return null;
    }

    /// <summary>글 값 (없거나 null이면 null, 글이 아니면 InvalidOperationException → 깨진 줄).</summary>
    private static string? Str(JsonElement o, string name) => Opt(o, name)?.GetString();

    /// <summary>꼭 있어야 하는 정수 (없거나 null이면 깨진 줄).</summary>
    private static long Num(JsonElement o, string name) =>
        Opt(o, name) is { } v ? v.GetInt64() : throw new InvalidOperationException(name);

    private static bool HasDuplicateNames(JsonElement o)
    {
        // 속성이 몇 개뿐이라 단순 비교로 충분하다
        int count = 0;
        foreach (var _ in o.EnumerateObject()) count++;
        if (count < 2) return false;
        var names = new string[count];
        int i = 0;
        foreach (var p in o.EnumerateObject())
        {
            string n = p.Name;
            for (int k = 0; k < i; k++) if (names[k] == n) return true;
            names[i++] = n;
        }
        return false;
    }
}
