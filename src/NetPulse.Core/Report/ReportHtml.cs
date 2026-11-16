using System.Globalization;
using System.Text;
using System.Xml;

namespace NetPulse.Core.Report;

/// <summary>
/// 문서 팔레트 (M8 설계 4.1 [1-6]): 내보내는 파일은 언제나 이 색. 앞 여덟 값은 디자인 토큰 라이트 팔레트와 같다 (Design 시험이 대조).
/// 나머지는 흰 종이 위의 보조 색.
/// </summary>
public sealed record ReportPalette(
    string Bg, string Ink, string Muted, string Accent, string Ok, string Warn, string Bad, string Focus,
    string Surface, string Edge, string BadSoft, string WarnSoft, string Link, string Mark)
{
    public static readonly ReportPalette Document = new(
        Bg: "#f4f4f2", Ink: "#111113", Muted: "#52525b", Accent: "#0369a1", Ok: "#047857", Warn: "#a16207", Bad: "#be123c", Focus: "#0369a1",
        Surface: "#ffffff", Edge: "#d4d4d8", BadSoft: "#fde4ea", WarnSoft: "#fdf1d8", Link: "#111113", Mark: "#047857");
}

/// <summary>HTML·SVG 문맥별 인코딩 (M8 설계 3.4 [1-7]). XML로도 읽히는 형태.</summary>
public static class Markup
{
    /// <summary>텍스트 노드·따옴표 속성 값: &amp; &lt; &gt; &quot; &#39;만 개체로. XML에 쓸 수 없는 문자(제어 문자·짝 없는 대리 문자)는 U+FFFD.</summary>
    public static string Text(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); continue;
                case '<': sb.Append("&lt;"); continue;
                case '>': sb.Append("&gt;"); continue;
                case '"': sb.Append("&quot;"); continue;
                case '\'': sb.Append("&#39;"); continue;
            }
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }
            bool valid = c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
            sb.Append(valid ? c : '�');
        }
        return sb.ToString();
    }

    /// <summary>SVG 수치: 유한한 값만, 고정 문화권 소수 둘째 자리까지. NaN·무한대는 0 (그리는 쪽이 미리 거른다).</summary>
    public static string Number(double v) => double.IsFinite(v) ? Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture) : "0";
}

/// <summary>문서 → HTML 한 파일 (M8 설계 3.4). 인라인 CSS·SVG, 스크립트·외부 참조 없음.</summary>
public static class ReportHtml
{
    public static string Write(ReportDocument d) => Write(d, ReportPalette.Document);

    public static string Write(ReportDocument d, ReportPalette p)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"ko\">\n<head>\n<meta charset=\"utf-8\" />\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n")
            .Append("<meta name=\"generator\" content=\"NetPulse\" />\n")
            .Append("<title>").Append(Markup.Text(d.Title)).Append("</title>\n<style>").Append(Css(p)).Append("</style>\n</head>\n<body>\n<main>\n");
        sb.Append("<header>\n<h1>").Append(Markup.Text(d.Title)).Append("</h1>\n");
        foreach (var r in d.Lead) sb.Append("<p class=\"lead\">").Append(Markup.Text(r.Text)).Append("</p>\n");
        sb.Append("</header>\n");
        for (int i = 0; i < d.Sections.Count; i++)
        {
            var s = d.Sections[i];
            sb.Append("<section id=\"").Append(s.Id).Append("\" aria-labelledby=\"").Append(s.Id).Append("-title\">\n<h2 id=\"").Append(s.Id).Append("-title\">")
                .Append(Markup.Text(s.Title)).Append("</h2>\n");
            foreach (var b in s.Blocks) Block(sb, b, p);
            sb.Append("</section>\n");
        }
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void Block(StringBuilder sb, ReportBlock b, ReportPalette p)
    {
        switch (b)
        {
            case ReportParagraph para:
                sb.Append("<p class=\"").Append(para.Kind switch
                {
                    ParagraphKind.Lead => "lead", ParagraphKind.Note => "note", ParagraphKind.Warning => "warning", _ => "text",
                }).Append("\">");
                if (para.Runs.Count == 1) sb.Append(Markup.Text(para.Runs[0].Text));
                else foreach (var r in para.Runs) sb.Append("<span>").Append(Markup.Text(r.Text)).Append("</span>");
                sb.Append("</p>\n");
                break;
            case ReportHeading h:
                sb.Append("<h3>").Append(Markup.Text(h.Text.Text)).Append("</h3>\n");
                break;
            case ReportFacts f:
                sb.Append("<dl class=\"facts\">\n");
                foreach (var x in f.Items)
                    sb.Append("<div class=\"fact\"><dt>").Append(Markup.Text(x.Label)).Append("</dt><dd data-field=\"").Append(x.Field).Append("\">")
                        .Append(Markup.Text(x.Value.Text)).Append("</dd></div>\n");
                sb.Append("</dl>\n");
                break;
            case ReportTable t:
                sb.Append("<table>\n<caption>").Append(Markup.Text(t.Caption)).Append("</caption>\n<thead><tr>");
                foreach (var c in t.Columns) sb.Append("<th scope=\"col\">").Append(Markup.Text(c.Text)).Append("</th>");
                sb.Append("</tr></thead>\n<tbody>\n");
                foreach (var row in t.Rows)
                {
                    sb.Append(row.Kind switch { RowKind.Sub => "<tr class=\"sub\">", RowKind.Note => "<tr class=\"note\">", _ => "<tr>" });
                    for (int i = 0; i < row.Cells.Count; i++)
                    {
                        var cell = row.Cells[i];
                        bool head = i == 0 && row.Kind == RowKind.Normal;
                        sb.Append(head ? "<th scope=\"row\"" : "<td");
                        if (row.Kind == RowKind.Note) sb.Append(" colspan=\"").Append(t.Columns.Count).Append('"');
                        if (cell.Field is { } field) sb.Append(" data-field=\"").Append(field).Append('"');
                        sb.Append('>').Append(Markup.Text(cell.Value.Text)).Append(head ? "</th>" : "</td>");
                    }
                    sb.Append("</tr>\n");
                }
                sb.Append("</tbody>\n</table>\n");
                break;
            case ReportFigure g:
                sb.Append("<figure>");
                TimelineSvg.Write(sb, g.Figure, p);
                sb.Append("</figure>\n");
                break;
        }
    }

    /// <summary>고정 CSS (문자열을 끼워 넣지 않는다: 팔레트 상수만). 인쇄는 A4 세로, 표 머리 반복, 줄·그림이 쪽 사이에서 잘리지 않게.</summary>
    private static string Css(ReportPalette p) => $$"""

:root { color-scheme: light; }
* { box-sizing: border-box; }
body { margin: 0; background: {{p.Bg}}; color: {{p.Ink}}; font-family: "Segoe UI", "Malgun Gothic", sans-serif; font-size: 14px; line-height: 1.55; }
main { max-width: 980px; margin: 0 auto; padding: 32px 24px 48px; background: {{p.Surface}}; }
h1 { font-size: 26px; margin: 0 0 12px; }
h2 { font-size: 19px; margin: 32px 0 10px; padding-bottom: 6px; border-bottom: 2px solid {{p.Edge}}; break-after: avoid; }
h3 { font-size: 15px; margin: 20px 0 6px; break-after: avoid; }
p { margin: 6px 0; }
p.lead { color: {{p.Ink}}; }
p.note { color: {{p.Muted}}; }
p.warning { border-left: 4px solid {{p.Bad}}; padding: 6px 10px; background: {{p.BadSoft}}; }
dl.facts { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 6px 18px; margin: 10px 0; }
div.fact { display: flex; gap: 10px; break-inside: avoid; }
dt { color: {{p.Muted}}; min-width: 96px; }
dd { margin: 0; font-weight: 600; }
table { width: 100%; border-collapse: collapse; margin: 10px 0 16px; font-size: 13px; }
caption { text-align: left; font-weight: 600; padding: 4px 0; caption-side: top; }
th, td { border: 1px solid {{p.Edge}}; padding: 4px 6px; text-align: left; vertical-align: top; }
thead th { background: {{p.Bg}}; }
tbody th { font-weight: 600; }
tr.sub td { color: {{p.Muted}}; }
tr.note td { color: {{p.Muted}}; font-style: italic; }
figure { margin: 12px 0; break-inside: avoid; }
svg.timeline { width: 100%; height: auto; font-family: "Segoe UI", "Malgun Gothic", sans-serif; font-size: 12px; }
@page { size: A4 portrait; margin: 0.4in; }
@media print {
  body { background: {{p.Surface}}; font-size: 11px; }
  main { max-width: none; padding: 0; }
  thead { display: table-header-group; }
  tr, th, td { break-inside: avoid; }
}

""";

    // ---------------- 검사 (시험과 내보내기 전 확인이 같이 쓴다) ----------------

    private static readonly HashSet<string> BannedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "link", "iframe", "object", "embed", "img", "image", "foreignObject", "base", "frame", "frameset", "audio", "video", "source", "use",
    };

    private static XmlReader Reader(string html) => XmlReader.Create(new StringReader(html), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = false,
    });

    /// <summary>
    /// 외부 참조 검사 (M8 설계 3.4 [1-8]): 금지 요소, URL 속성은 <c>#</c> 내부 조각만, 인라인 이벤트 속성 없음, CSS <c>@import</c> 없음·<c>url(</c>은 <c>url(#…)</c>만.
    /// XML로 읽히지 않으면 그것도 문제. 문제가 없으면 빈 목록.
    /// </summary>
    public static IReadOnlyList<string> Verify(string html)
    {
        var problems = new List<string>();
        try
        {
            using var r = Reader(html);
            bool inStyle = false;
            while (r.Read())
            {
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                        if (BannedElements.Contains(r.LocalName)) problems.Add("요소 " + r.LocalName);
                        inStyle = r.LocalName == "style" && !r.IsEmptyElement;
                        if (r.MoveToFirstAttribute())
                        {
                            do
                            {
                                string name = r.Name, value = r.Value;
                                string local = r.LocalName.ToLowerInvariant();
                                if (local.StartsWith("on", StringComparison.Ordinal)) problems.Add("이벤트 속성 " + name);
                                if (local is "href" or "src" or "action" or "formaction" or "srcset" or "poster" or "data" && !value.StartsWith('#'))
                                    problems.Add("외부 참조 속성 " + name);
                                if (local == "style" || value.Contains("url(", StringComparison.OrdinalIgnoreCase)) CheckCss(value, problems);
                            }
                            while (r.MoveToNextAttribute());
                            r.MoveToElement();
                        }
                        break;
                    case XmlNodeType.EndElement:
                        if (r.LocalName == "style") inStyle = false;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        if (inStyle) CheckCss(r.Value, problems);
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        problems.Add("처리 명령");
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            problems.Add("XML로 읽히지 않음: " + ex.LineNumber);
        }
        return problems;
    }

    private static void CheckCss(string css, List<string> problems)
    {
        if (css.Contains("@import", StringComparison.OrdinalIgnoreCase)) problems.Add("CSS @import");
        int i = 0;
        while ((i = css.IndexOf("url(", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += 4;
            while (i < css.Length && css[i] is ' ' or '"' or '\'') i++;
            if (i >= css.Length || css[i] != '#') problems.Add("CSS 외부 url");
        }
    }

    /// <summary>본문(<c>body</c>)의 텍스트 노드를 문서 순서대로, 공백을 정리해 빈 것은 뺀다. 글 순서 대조용.</summary>
    public static IReadOnlyList<string> BodyTexts(string html)
    {
        var list = new List<string>();
        using var r = Reader(html);
        int depthBody = -1;
        bool skip = false;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (r.LocalName == "body") depthBody = r.Depth;
                if (r.LocalName is "style" or "script") skip = !r.IsEmptyElement;
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName is "style" or "script") skip = false;
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && depthBody >= 0 && !skip)
            {
                var t = Normalize(r.Value);
                if (t.Length > 0) list.Add(t);
            }
        }
        return list;
    }

    /// <summary><c>data-field</c>가 붙은 요소의 (필드 이름, 글)을 문서 순서대로.</summary>
    public static IReadOnlyList<(string Field, string Value)> FieldValues(string html)
    {
        var list = new List<(string, string)>();
        using var r = Reader(html);
        r.Read();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.Element && r.GetAttribute("data-field") is { } field)
            {
                // ReadInnerXml은 다음 노드로 넘어가 있으므로 Read하지 않는다 (붙어 있는 다음 칸을 건너뛰지 않게)
                if (r.IsEmptyElement) { list.Add((field, "")); r.Read(); }
                else list.Add((field, Normalize(System.Net.WebUtility.HtmlDecode(StripTags(r.ReadInnerXml())))));
                continue;
            }
            r.Read();
        }
        return list;
    }

    /// <summary>모든 속성 값 (요소 이름, 속성 이름, 값). 안전망의 속성 검사용.</summary>
    public static IEnumerable<(string Element, string Attribute, string Value)> Attributes(string html)
    {
        using var r = Reader(html);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            string element = r.LocalName;
            if (!r.MoveToFirstAttribute()) continue;
            do yield return (element, r.Name, r.Value);
            while (r.MoveToNextAttribute());
            r.MoveToElement();
        }
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string StripTags(string xml)
    {
        var sb = new StringBuilder(xml.Length);
        bool tag = false;
        foreach (var c in xml)
        {
            if (c == '<') { tag = true; continue; }
            if (c == '>') { tag = false; continue; }
            if (!tag) sb.Append(c);
        }
        return sb.ToString();
    }
}
