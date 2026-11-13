using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPulse.Core.Records;

/// <summary>증상 표시 하나 (M5 설계 3.1). 메모는 이벤트 → 노트 순서로 적용한 마지막 값이다.</summary>
/// <param name="Round">누른 순간까지 시작한 마지막 회차 (아직 회차가 없으면 null)</param>
/// <param name="AtMs">누른 순간의 측정 경과 ms</param>
/// <param name="EditedAt">메모를 마지막으로 고친 벽시계 (표시와 함께 쓴 메모만 있으면 null)</param>
public sealed record SymptomMark(string Mark, long? Round, long AtMs, DateTimeOffset Wall, string? Memo, DateTimeOffset? EditedAt, bool Truncated);

/// <summary>
/// 증상 표시·메모 (M5 설계 3.1). 기록은 줄 추가만 하므로 메모를 고치면 <c>symptomMemo</c> 줄을 더한다.
/// 측정이 끝난 뒤에는 같은 줄 형식을 기록 폴더의 <c>notes.jsonl</c>에 쓴다 (<see cref="RecordNotes"/>).
/// </summary>
public static class SymptomMarks
{
    public const string MarkType = "symptom";
    public const string MemoType = "symptomMemo";
    public const int MaxMemoLength = 200;

    /// <summary>
    /// 메모 정리: 앞뒤 공백 제거, 줄바꿈·제어 문자를 공백 하나로, 200자까지. 빈 결과는 null (메모 없음).
    /// 글자 수는 화면에 보이는 문자(문자 요소) 기준이라 한글·이모지를 반쪽으로 자르지 않는다.
    /// </summary>
    public static (string? Text, bool Truncated) Clean(string? memo)
    {
        if (memo is null) return (null, false);
        var sb = new StringBuilder(memo.Length);
        bool space = false;
        foreach (var ch in memo)
        {
            if (char.IsControl(ch) || ch is '\u2028' or '\u2029' || char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        var text = sb.ToString();
        if (text.Length == 0) return (null, false);
        var info = new StringInfo(text);
        if (info.LengthInTextElements <= MaxMemoLength) return (text, false);
        return (info.SubstringByTextElements(0, MaxMemoLength).TrimEnd(), true);
    }

    /// <summary>표시 이벤트의 고유 필드.</summary>
    public static JsonObject MarkFields(string mark, string? memo)
    {
        var (text, truncated) = Clean(memo);
        var fields = new JsonObject { ["mark"] = mark };
        if (text is not null) fields["memo"] = text;
        if (truncated) fields["memoTruncated"] = true;
        return fields;
    }

    /// <summary>메모 이벤트의 고유 필드. 메모가 비면 null로 남긴다 (지움).</summary>
    public static JsonObject MemoFields(string mark, string? memo)
    {
        var (text, truncated) = Clean(memo);
        var fields = new JsonObject { ["mark"] = mark, ["memo"] = text };
        if (truncated) fields["memoTruncated"] = true;
        return fields;
    }

    /// <summary>
    /// 이벤트(측정 중)와 노트(끝난 뒤)에서 표시를 만든다. 순서: 이벤트의 표시·메모를 줄 순서대로, 그 다음 노트를 줄 순서대로.
    /// 무시하는 줄: 같은 표시 ID의 두 번째 표시, 모르는 표시의 메모, 필드가 맞지 않는 줄.
    /// </summary>
    public static (IReadOnlyList<SymptomMark> Marks, int Ignored) FromEvents(IEnumerable<JsonObject> events, IEnumerable<JsonObject>? notes = null)
    {
        var marks = new List<SymptomMark>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int ignored = 0;

        void ApplyMemo(JsonObject o)
        {
            if (!TryMark(o, out var mark) || !index.TryGetValue(mark, out var i) || !TryWall(o, out var wall))
            {
                ignored++;
                return;
            }
            string? memo = o["memo"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (o["memo"] is JsonNode n && n is not JsonValue) { ignored++; return; }
            var (clean, cut) = Clean(memo);
            bool truncated = cut || (o["memoTruncated"] is JsonValue t && t.TryGetValue<bool>(out var b) && b);
            marks[i] = marks[i] with { Memo = clean, EditedAt = wall, Truncated = truncated };
        }

        foreach (var o in events)
        {
            var type = o["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
            if (type == MarkType)
            {
                if (!TryMark(o, out var mark) || index.ContainsKey(mark) || !TryWall(o, out var wall)
                    || o["at"] is not JsonValue av || !av.TryGetValue<long>(out var at))
                {
                    ignored++;
                    continue;
                }
                long? round = o["round"] is JsonValue rv && rv.TryGetValue<long>(out var r) ? r : null;
                string? memo = o["memo"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
                var (clean, cut) = Clean(memo);
                bool truncated = cut || (o["memoTruncated"] is JsonValue t && t.TryGetValue<bool>(out var b) && b);
                index[mark] = marks.Count;
                marks.Add(new SymptomMark(mark, round, at, wall, clean, null, truncated));
            }
            else if (type == MemoType) ApplyMemo(o);
        }
        if (notes is not null)
            foreach (var o in notes)
            {
                var type = o["type"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
                if (type == MemoType) ApplyMemo(o);
                else ignored++;   // 노트에는 메모 줄만 쓴다
            }
        return (marks, ignored);
    }

    private static bool TryMark(JsonObject o, out string mark)
    {
        mark = "";
        if (o["mark"] is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length == 0 || s.Length > 16) return false;
        mark = s;
        return true;
    }

    private static bool TryWall(JsonObject o, out DateTimeOffset wall)
    {
        wall = default;
        return o["wall"] is JsonValue v && v.TryGetValue<string>(out var s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out wall);
    }
}

/// <summary>
/// 끝난 측정의 노트 (M5 설계 3.1): 기록 폴더의 <c>notes.jsonl</c>. 봉인한 기록 본문(events, summary)은 건드리지 않는다.
/// 줄 형식은 이벤트 줄과 같다. 줄마다 디스크까지 내린다.
/// </summary>
public static class RecordNotes
{
    public const string FileName = "notes.jsonl";

    /// <summary>메모 한 줄을 덧붙인다. 실패하면 예외 (부르는 쪽이 재시도·상태를 정한다).</summary>
    public static void AppendMemo(string segmentFolder, string measurementId, string mark, long markAtMs, DateTimeOffset editedWall, string? memo)
    {
        var line = RecordSchema.EventLine(measurementId, SymptomMarks.MemoType, null, markAtMs, editedWall, SymptomMarks.MemoFields(mark, memo));
        Append(segmentFolder, line);
    }

    public static void Append(string segmentFolder, string line)
    {
        var path = Path.Combine(segmentFolder, FileName);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        // 앞 줄이 잘려 끝났으면(줄바꿈 없음) 새 줄을 붙여 그 줄만 손상으로 남긴다
        if (stream.Length > 0)
        {
            using var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            check.Seek(-1, SeekOrigin.End);
            if (check.ReadByte() != '\n') stream.Write("\n"u8);
        }
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        stream.Flush(flushToDisk: true);
    }
}
