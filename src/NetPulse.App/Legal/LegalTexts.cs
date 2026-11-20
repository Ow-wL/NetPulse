namespace NetPulse.App.Legal;

/// <summary>
/// 앱에 내장한 사용 조건·라이선스·제3자 고지 (M10 설계 A2·A3). exe 하나로 배포해도 보이게 리소스로 넣었다.
/// 읽지 못하면 null (화면은 읽기 실패 상태를 보이고, 동의 화면은 [동의하고 시작]을 끈다).
/// </summary>
public static class LegalTexts
{
    /// <summary>시험 전용 <c>--notices-fail</c>: 내장 고지를 읽지 못한 것처럼.</summary>
    public static bool FailForTest { get; set; }

    public sealed record Item(string Name, string Version, string License, string Text);

    public static string? Terms => Read("NetPulse.Legal.TERMS.txt");
    public static string? License => Read("NetPulse.Legal.LICENSE.txt");
    public static string? Notices => Read("NetPulse.Legal.THIRD-PARTY-NOTICES.txt");

    private static string? Read(string name)
    {
        if (FailForTest) return null;
        try
        {
            using var stream = typeof(LegalTexts).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// 보여 줄 항목: 사용 조건, NetPulse 라이선스, 그다음 고지의 구성요소들(<c>=== 구성요소: 이름 | 버전 | 라이선스 ===</c>로 나뉜다).
    /// 셋 중 하나라도 읽지 못하면 null.
    /// </summary>
    public static IReadOnlyList<Item>? Items()
    {
        if (Terms is not { } terms || License is not { } license || Notices is not { } notices) return null;
        var items = new List<Item>
        {
            new("NetPulse 사용 조건", NetPulse.Design.Settings.TermsConsent.CurrentTerms, "동의 필요", terms.Trim()),
            new("NetPulse", AppInfo.Current.Version, "MIT", license.Trim()),
        };
        items.AddRange(ParseNotices(notices));
        return items;
    }

    /// <summary>고지 글을 구성요소별로 나눈다. 머리(목록) 부분은 버린다.</summary>
    public static IEnumerable<Item> ParseNotices(string notices)
    {
        const string head = "=== 구성요소: ";
        string? name = null, version = null, license = null;
        var text = new System.Text.StringBuilder();
        foreach (var line in notices.Split('\n'))
        {
            if (line.StartsWith(head, StringComparison.Ordinal) && line.EndsWith(" ===", StringComparison.Ordinal))
            {
                if (name is not null) yield return new(name, version!, license!, text.ToString().Trim());
                var parts = line[head.Length..^4].Split(" | ");
                name = parts[0];
                version = parts.Length > 1 ? parts[1] : "";
                license = parts.Length > 2 ? parts[2] : "";
                text.Clear();
                continue;
            }
            if (name is not null) text.Append(line).Append('\n');
        }
        if (name is not null) yield return new(name, version!, license!, text.ToString().Trim());
    }
}
