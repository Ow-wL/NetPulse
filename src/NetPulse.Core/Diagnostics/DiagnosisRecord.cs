using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPulse.Core.Records;

namespace NetPulse.Core.Diagnostics;

/// <summary>읽은 진단 기록 상태.</summary>
public enum DiagnosisReadStatus { Ok, Unreadable, Unsupported }

/// <summary>기록에서 읽은 검사 하나 (판정·실행 상태·대상별 결과).</summary>
public sealed record StoredCheck(CheckId Id, CheckJudgment Judgment, bool PartlyUnconfirmed, RunState State, string? Code, long ElapsedMs, IReadOnlyList<TargetCheck> Targets);

/// <summary>진단 기록 하나.</summary>
public sealed record StoredDiagnosis(DiagnosisReadStatus Status, string? Problem, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string AppVersion,
    string? MeasurementId, IReadOnlyList<StoredCheck> Checks, int MainConclusion, IReadOnlyList<int> AlsoMatched);

/// <summary>
/// 진단 결과 기록 형식 버전 1 (M4 설계 C4): diagnosis-&lt;시작 UTC&gt;.json.
/// 대상은 순번(t1, t2)만, 주소·도메인 값·예외 메시지는 남기지 않는다. 실행 상태(run/notRun/timeout/error)를 구별한다.
/// 저장은 M6 화면이 부른다.
/// </summary>
public static class DiagnosisRecord
{
    public const string Format = "netpulse.diagnosis";
    public const int Version = 1;

    public static string FileName(DateTimeOffset startedAt) =>
        "diagnosis-" + startedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".json";

    public static string ToJson(DiagnosisResult r, string appVersion, string? measurementId) => new JsonObject
    {
        ["format"] = Format, ["version"] = Version, ["appVersion"] = appVersion, ["measurementId"] = measurementId,
        ["startedAt"] = RecordSchema.Iso(r.StartedAt), ["endedAt"] = RecordSchema.Iso(r.EndedAt),
        ["checks"] = new JsonArray(r.Checks.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = Text(c.Id), ["judgment"] = Text(c.Judgment), ["partlyUnconfirmed"] = c.PartlyUnconfirmed, ["state"] = Text(c.State),
            ["code"] = c.Code, ["elapsedMs"] = (long)c.Elapsed.TotalMilliseconds,
            ["targets"] = new JsonArray(c.Targets.Select(t => (JsonNode)new JsonObject
            {
                ["slot"] = t.Slot, ["outcome"] = Text(t.Outcome), ["code"] = t.Code, ["elapsedMs"] = (long)t.Elapsed.TotalMilliseconds,
            }).ToArray()),
        }).ToArray()),
        ["conclusion"] = r.Main.Number,
        ["also"] = new JsonArray(r.AlsoMatched.Select(n => (JsonNode)n).ToArray()),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static StoredDiagnosis Parse(string json)
    {
        try
        {
            var o = JsonNode.Parse(json)!.AsObject();
            if ((string?)o["format"] != Format || o["version"] is not JsonValue v || v.GetValue<int>() != Version)
                return Fail(DiagnosisReadStatus.Unsupported, "지원하지 않는 진단 기록 형식입니다.");
            var checks = o["checks"]!.AsArray().Select(n =>
            {
                var c = n!.AsObject();
                return new StoredCheck(Parse<CheckId>((string)c["id"]!), Parse<CheckJudgment>((string)c["judgment"]!), (bool)c["partlyUnconfirmed"]!,
                    Parse<RunState>((string)c["state"]!), (string?)c["code"], (long)c["elapsedMs"]!,
                    c["targets"]!.AsArray().Select(t =>
                    {
                        var x = t!.AsObject();
                        return new TargetCheck((string)x["slot"]!, Parse<TargetOutcome>((string)x["outcome"]!), (string?)x["code"], TimeSpan.FromMilliseconds((long)x["elapsedMs"]!));
                    }).ToArray());
            }).ToArray();
            int main = (int)o["conclusion"]!;
            if (main is < 1 or > 12) throw new FormatException("결론 번호");
            if (checks.Select(c => c.Id).Distinct().Count() != checks.Length) throw new FormatException("검사 중복");
            return new StoredDiagnosis(DiagnosisReadStatus.Ok, null, DateTimeOffset.Parse((string)o["startedAt"]!, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse((string)o["endedAt"]!, CultureInfo.InvariantCulture), (string)o["appVersion"]!, (string?)o["measurementId"],
                checks, main, o["also"]!.AsArray().Select(n => (int)n!).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or NullReferenceException or ArgumentException or KeyNotFoundException)
        {
            return Fail(DiagnosisReadStatus.Unreadable, "진단 기록을 읽을 수 없습니다.");
        }
    }

    private static StoredDiagnosis Fail(DiagnosisReadStatus s, string problem) => new(s, problem, default, default, "", null, [], 0, []);

    private static string Text<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    private static T Parse<T>(string text) where T : struct, Enum =>
        Enum.GetValues<T>().FirstOrDefault(v => Text(v) == text) is var found && Text(found) == text ? found : throw new FormatException("모르는 값 " + text);
}
