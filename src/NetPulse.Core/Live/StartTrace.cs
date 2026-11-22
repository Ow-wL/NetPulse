using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPulse.Core.Live;

/// <summary>앞 실행의 시작 기록 해석 결과. <see cref="LastStep"/>은 <b>도달 지점</b>일 뿐 충돌 지점·원인의 증명이 아니다 (M12 설계 B2 [1-6]).</summary>
public sealed record StartTraceRun(string Run, string LastStep, DateTimeOffset? LastAt, bool Exited, int? ExitCode, IReadOnlyList<string> Steps);

/// <summary>
/// 시작 단계 기록 (M12 설계 B2): 실행마다 실행 ID를 두고 단계 이름·시각만 한 줄씩 쓴다 (개인 정보 없음).
/// 줄 형식 <c>{"run":"…","step":"…","t":"…"}</c>, 정상 종료는 <c>step="exit"</c>와 종료 코드.
/// 관리 코드보다 앞(단일 파일 압축 풀기·런타임 시작)은 기록할 수 없다. 그래서 기록이 없으면 "최초 기록 지점에 닿았는지 알 수 없음"이다 [2-5].
/// </summary>
public static class StartTrace
{
    public const string FileName = "startup-trace.jsonl";
    public const string ExitStep = "exit";
    /// <summary>파일이 이보다 크면 시작할 때 마지막 <see cref="KeepLines"/>줄만 남긴다</summary>
    public const int TrimBytes = 64 * 1024, KeepLines = 200;

    public static string Line(string run, string step, DateTimeOffset at, int? exitCode = null)
    {
        var o = new JsonObject
        {
            ["run"] = run,
            ["step"] = step,
            ["t"] = at.ToString("o", CultureInfo.InvariantCulture),
        };
        if (exitCode is { } code) o["code"] = code;
        return o.ToJsonString();
    }

    /// <summary>줄들을 실행별로 묶는다 (파일에 나온 순서). 읽을 수 없는 줄은 건너뛴다 (쓰는 도중 끊긴 마지막 줄 등).</summary>
    public static IReadOnlyList<StartTraceRun> Runs(IEnumerable<string> lines)
    {
        var order = new List<string>();
        var steps = new Dictionary<string, List<(string Step, DateTimeOffset? At, int? Code)>>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(raw) as JsonObject; }
            catch (JsonException) { continue; }
            if (o?["run"]?.GetValueKind() != JsonValueKind.String || o["step"]?.GetValueKind() != JsonValueKind.String) continue;
            string run = (string)o["run"]!, step = (string)o["step"]!;
            DateTimeOffset? at = o["t"]?.GetValueKind() == JsonValueKind.String
                && DateTimeOffset.TryParse((string)o["t"]!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
            int? code = o["code"]?.GetValueKind() == JsonValueKind.Number ? (int)o["code"]! : null;
            if (!steps.TryGetValue(run, out var list)) { steps[run] = list = []; order.Add(run); }
            list.Add((step, at, code));
        }
        return order.Select(r =>
        {
            var s = steps[r];
            var exit = s.LastOrDefault(x => x.Step == ExitStep);
            var last = s[^1];
            return new StartTraceRun(r, last.Step, last.At, exit.Step == ExitStep, exit.Step == ExitStep ? exit.Code : null, s.Select(x => x.Step).ToArray());
        }).ToArray();
    }

    /// <summary>
    /// 지금 실행 바로 앞 실행이 정상 종료 표시 없이 끝났으면 그 실행 (아니면 null). 정상 종료 표시가 없는 실행만 "끊김"으로 본다.
    /// </summary>
    public static StartTraceRun? PreviousInterrupted(IEnumerable<string> lines, string currentRun)
    {
        var runs = Runs(lines).Where(r => r.Run != currentRun).ToArray();
        return runs.Length > 0 && !runs[^1].Exited ? runs[^1] : null;
    }

    /// <summary>파일이 크면 마지막 줄들만 남긴 내용 (자르지 않아도 되면 null).</summary>
    public static IReadOnlyList<string>? Trimmed(IReadOnlyList<string> lines, long bytes) =>
        bytes > TrimBytes && lines.Count > KeepLines ? lines.Skip(lines.Count - KeepLines).ToArray() : null;
}
