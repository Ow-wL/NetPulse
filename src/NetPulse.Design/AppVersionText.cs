namespace NetPulse.Design;

/// <summary>
/// 제품 버전 글 해석 (M10 설계 A1). <c>1.0.0+&lt;커밋 전체 SHA&gt;</c> → (1.0.0, 앞 7자).
/// <c>+</c> 뒤가 40자 16진수가 아니면 커밋 없음. 비었거나 없으면 버전 "0.0.0". 앱의 <c>AppInfo</c>가 쓴다 (시험하기 쉽게 여기에 둔다).
/// </summary>
public static class AppVersionText
{
    public static (string Version, string? Commit) Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return ("0.0.0", null);
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        string version = (plus < 0 ? informational : informational[..plus]).Trim();
        if (version.Length == 0) version = "0.0.0";
        string? commit = null;
        if (plus >= 0)
        {
            var tail = informational[(plus + 1)..].Trim();
            if (tail.Length == 40 && tail.All(Uri.IsHexDigit)) commit = tail[..7].ToLowerInvariant();
        }
        return (version, commit);
    }
}
