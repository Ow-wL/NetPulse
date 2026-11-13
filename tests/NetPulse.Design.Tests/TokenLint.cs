using System.Text;
using System.Text.RegularExpressions;

namespace NetPulse.Design.Tests;

/// <summary>위반 하나: 파일(저장소 기준 상대 경로), 줄 번호, 규칙, 줄 내용.</summary>
public sealed record LintFinding(string File, int Line, string Rule, string Text)
{
    public override string ToString() => $"{File}:{Line} [{Rule}] {Text.Trim()}";
}

/// <summary>
/// 화면 코드 정적 검사 (M2 설계 4.6). src/NetPulse.App 아래 .cs·.xaml에서 토큰 밖 값과 금지 API를 찾는다.
/// 정규식 기반이다: 토큰에서 계산한 값(예: Space.S2 * 2)은 잡지 않으며 이는 허용한다.
/// 주석은 검사하지 않는다. 문자열 안은 색 문자열("#...")만 검사한다.
/// </summary>
public static partial class TokenLint
{
    private sealed record Rule(string Name, Regex Cs, Regex? Xaml, string[] AllowedFiles);

    private const string LayoutProps = "Margin|Padding|Spacing|RowSpacing|ColumnSpacing|CornerRadius|BorderThickness|FontSize|Width|Height|MinWidth|MaxWidth|MinHeight|MaxHeight|StrokeThickness|Opacity|TintOpacity|TintLuminosityOpacity|LineHeight|CharacterSpacing";

    private static readonly Rule[] Rules =
    [
        new("color-literal",
            new(@"Color\.FromArgb\s*\(|ColorHelper\.FromArgb|\bColors\.\w|""#[0-9a-fA-F]{3,8}""", RegexOptions.Compiled),
            new(@"""#[0-9a-fA-F]{3,8}""", RegexOptions.Compiled),
            ["src/NetPulse.App/Design/TokenBrushes.cs", "src/NetPulse.App/Design/SystemColors.cs"]),
        new("numeric-layout",
            new($@"\b({LayoutProps})\s*=\s*-?(?!0(\.0+)?(?![\d.]))\.?\d|new\s+(Thickness|CornerRadius)\s*\([^)]*?(?<![\w.])(?!0(\.0+)?(?![\d.]))\.?\d|new\s+SizeInt32\s*\(\s*\d", RegexOptions.Compiled),
            new($@"\b({LayoutProps})\s*=\s*""(?!\{{)(?!0(,0)*"")[^""]*\d", RegexOptions.Compiled),
            []),
        new("hidden-constant",
            new(@"\b(const|static\s+readonly)\s+(double|float|int|long|decimal|Color|Rgba|TimeSpan|Thickness|CornerRadius|SolidColorBrush)\b", RegexOptions.Compiled),
            null,
            []),
        new("time-literal",
            new(@"TimeSpan\.From\w+\s*\(\s*-?\d|new\s+TimeSpan\s*\(|TintTransitionDuration\s*=(?>\s*)(?!TimeSpan\.Zero)", RegexOptions.Compiled),
            new(@"\b(Duration|BeginTime)\s*=\s*""[^""{]*\d", RegexOptions.Compiled),
            []),
        new("animation-api",
            new(@"\bStoryboard\b|\b\w*(DoubleAnimation|ColorAnimation|PointAnimation|ObjectAnimationUsingKeyFrames|KeyFrameAnimation|ExpressionAnimation|NaturalMotionAnimation)\b|\bCreate\w*Animation\s*\(|\bImplicitAnimations\b|\b(Transitions|ChildrenTransitions|ContentTransitions|ItemContainerTransitions)\s*=|\bConnectedAnimationService\b|\.StartAnimation\s*\(", RegexOptions.Compiled),
            new(@"<\s*(Storyboard|\w*Animation\w*|VisualTransition)\b|Transitions\s*>", RegexOptions.Compiled),
            ["src/NetPulse.App/Design/Motion.cs", "src/NetPulse.App/Design/Theme.cs", "src/NetPulse.App/Design/Lights.cs"]),
        new("popup-api",
            new(@"\b(Popup|Flyout|MenuFlyout|ContentDialog|ToolTip|TeachingTip)\b", RegexOptions.Compiled),
            new(@"<\s*(Popup|Flyout|MenuFlyout|ContentDialog|ToolTip|TeachingTip)\b|ToolTipService\.", RegexOptions.Compiled),
            []),
        new("setvalue-bypass",
            new($@"\.SetValue\s*\(\s*[\w.]*\.({LayoutProps}|Foreground|Background|BorderBrush|Fill|Stroke)Property", RegexOptions.Compiled),
            null,
            []),
    ];

    public static IReadOnlyList<string> RuleNames { get; } = Rules.Select(r => r.Name).ToArray();

    /// <summary>파일 하나의 내용을 검사한다. <paramref name="relativePath"/>는 '/'로 구분한 저장소 기준 경로.</summary>
    public static IEnumerable<LintFinding> ScanText(string relativePath, string text)
    {
        bool xaml = relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool inBlockComment = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string code = xaml ? StripXamlComment(lines[i], ref inBlockComment) : StripCsComment(lines[i], ref inBlockComment, out _);
            if (code.Trim().Length == 0) continue;
            string codeNoStrings = xaml ? code : BlankStrings(code);
            foreach (var rule in Rules)
            {
                if (rule.AllowedFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase)) continue;
                var regex = xaml ? rule.Xaml : rule.Cs;
                if (regex is null) continue;
                // 색 문자열은 문자열 안에서 찾고, 나머지는 문자열을 지운 코드에서 찾는다
                string subject = rule.Name == "color-literal" ? code : codeNoStrings;
                if (regex.IsMatch(subject)) yield return new LintFinding(relativePath, i + 1, rule.Name, lines[i]);
            }
        }
    }

    /// <summary>저장소의 src/NetPulse.App 전체를 검사하고, 예외 목록에 있는 것은 뺀다.</summary>
    public static IReadOnlyList<LintFinding> ScanRepository(string repoRoot, IReadOnlyList<LintException> exceptions)
    {
        var app = Path.Combine(repoRoot, "src", "NetPulse.App");
        var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var found = new List<LintFinding>();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            found.AddRange(ScanText(rel, File.ReadAllText(file, Encoding.UTF8))
                .Where(f => !exceptions.Any(e => e.Covers(f))));
        }
        return found;
    }

    private static string StripCsComment(string line, ref bool inBlock, out bool docComment)
    {
        docComment = line.TrimStart().StartsWith("///", StringComparison.Ordinal);
        var sb = new StringBuilder();
        bool inString = false, verbatim = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';
            if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
                continue;
            }
            if (inString)
            {
                sb.Append(c);
                if (!verbatim && c == '\\') { if (i + 1 < line.Length) sb.Append(line[++i]); continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '/' && next == '/') break;
            if (c == '/' && next == '*') { inBlock = true; i++; continue; }
            if (c == '"') { inString = true; verbatim = i > 0 && (line[i - 1] == '@' || (line[i - 1] == '$' && i > 1 && line[i - 2] == '@')); }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string StripXamlComment(string line, ref bool inBlock)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlock)
            {
                if (string.CompareOrdinal(line, i, "-->", 0, 3) == 0) { inBlock = false; i += 2; }
                continue;
            }
            if (string.CompareOrdinal(line, i, "<!--", 0, 4) == 0) { inBlock = true; i += 3; continue; }
            sb.Append(line[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringLiteral();

    private static string BlankStrings(string code) => StringLiteral().Replace(code, "\"\"");
}

/// <summary>예외 한 줄: 파일|규칙|줄 내용 일부|이유. 파일 단위 일괄 예외는 없다.</summary>
public sealed record LintException(string File, string Rule, string Fragment, string Reason)
{
    public bool Covers(LintFinding f) =>
        string.Equals(f.File, File, StringComparison.OrdinalIgnoreCase) && f.Rule == Rule && f.Text.Contains(Fragment, StringComparison.Ordinal);

    public static IReadOnlyList<LintException> Load(string path) =>
        System.IO.File.ReadAllLines(path, Encoding.UTF8)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l =>
            {
                var parts = l.Split('|');
                if (parts.Length != 4 || parts.Any(p => p.Trim().Length == 0)) throw new FormatException("예외 형식은 파일|규칙|줄 일부|이유: " + l);
                return new LintException(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts[3].Trim());
            })
            .ToArray();
}
