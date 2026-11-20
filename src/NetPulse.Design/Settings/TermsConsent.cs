using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPulse.Design.Settings;

/// <summary>
/// 사용 조건 동의 기록 (M10 설계 A2 [2-4]). 설정 파일과 따로 사용자 폴더의 <c>terms.json</c>에 둔다:
/// 설정 파일의 판·손상·새 판 고정과 엮이지 않게 하려는 것이다.
/// 없음·형식 오류·다른 조건 판은 모두 "동의 안 함"으로 읽는다.
/// </summary>
public static class TermsConsent
{
    /// <summary>지금 사용 조건의 판. 조건 글이 바뀌면 올린다 (다시 묻는다).</summary>
    public const string CurrentTerms = "terms-1";
    public const string FileName = "terms.json";

    public static string PathIn(string userData) => System.IO.Path.Combine(userData, FileName);

    /// <summary>이 사용자 폴더에서 지금 판에 동의했는가.</summary>
    public static bool IsAccepted(string userData)
    {
        try
        {
            var path = PathIn(userData);
            if (!File.Exists(path)) return false;
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return node?["terms"] is JsonValue v && v.TryGetValue(out string? terms) && terms == CurrentTerms;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>동의를 기록한다 (임시 파일 → 바꾸기). 쓰지 못하면 false (이번 실행에만 기억하고 다음에 다시 묻는다).</summary>
    public static bool TryAccept(string userData, DateTimeOffset now)
    {
        var path = PathIn(userData);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(userData);
            var json = new JsonObject { ["terms"] = CurrentTerms, ["acceptedAt"] = now.ToString("o", System.Globalization.CultureInfo.InvariantCulture) };
            File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
