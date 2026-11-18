using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NetPulse.Core.Report;
using static NetPulse.Core.Tests.M8Records;

namespace NetPulse.Core.Tests;

/// <summary>HTML 한 파일 (M8 설계 3.4·3.5, 7.1 ReportHtml·같음).</summary>
public sealed class M8HtmlTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static XDocument Xml(string html)
    {
        using var r = XmlReader.Create(new StringReader(html), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(r);
    }

    private static IEnumerable<string> Normalized(IEnumerable<ReportRun> runs) => runs.Select(r => ReportHtml.Normalize(r.Text)).Where(t => t.Length > 0);

    [Fact]
    public void TheFileIsOneXmlReadableHtmlDocumentWithoutExternalReferences()
    {
        var d = Build(Typical(), new ReportOptions(IncludeMemos: true), diagnoses: [Diagnosis(W0.AddMinutes(3))]);
        var html = ReportHtml.Write(d);
        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"ko\">", html);
        Assert.Contains("<meta charset=\"utf-8\" />", html);
        var x = Xml(html);
        Assert.Equal("html", x.Root!.Name.LocalName);
        Assert.Empty(ReportHtml.Verify(html));
        Assert.Single(x.Descendants(Svg + "svg"));
        Assert.DoesNotContain(x.Descendants(), e => e.Name.LocalName is "script" or "link" or "img" or "iframe");
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "요소 script")]
    [InlineData("<img src=\"x.png\" />", "요소 img")]
    [InlineData("<a href=\"https://example.com/\">x</a>", "외부 참조 속성 href")]
    [InlineData("<a href=\"#summary\" onclick=\"x()\">x</a>", "이벤트 속성 onclick")]
    [InlineData("<div style=\"background:url(https://example.com/a.png)\">x</div>", "CSS 외부 url")]
    [InlineData("<style>@import 'x.css';</style>", "CSS @import")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"a.png\" /></svg>", "요소 image")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject /></svg>", "요소 foreignObject")]
    [InlineData("<p>broken", "XML로 읽히지 않음")]
    public void TheReferenceCheckCatchesElementsAttributesAndCss(string body, string expected)
    {
        var html = "<!DOCTYPE html>\n<html><head><title>t</title></head><body>" + body + "</body></html>";
        Assert.Contains(ReportHtml.Verify(html), p => p.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TheReferenceCheckAllowsInternalPatternsAndUrlLikeUserText()
    {
        var html = "<!DOCTYPE html>\n<html><head><style>.a { fill: url(#np-gap); }</style></head><body><p>https://example.com/ src=\"x\" @import</p>"
            + "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect fill=\"url(#np-none)\" /></svg></body></html>";
        Assert.Empty(ReportHtml.Verify(html));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("\"><img src=x onerror=alert(1)>")]
    [InlineData("]]> & &amp; <!-- -->")]
    [InlineData("줄\n바꿈\r\n과 탭\t")]
    [InlineData("이모지 😀 결합 문자 é 한글")]
    [InlineData("제어\u0001문자\uD800짝 없음")]
    public void InjectedStringsStayTextNodes(string value)
    {
        // 가리지 않은 문서: 이름·메모가 그대로 들어간다 (메모는 정리되므로 이름으로도 넣는다)
        var targets = new[] { Router with { Name = value }, ExtA, ExtB };
        var rec = Record(Rounds(Ok(120), Ok(120), Ok(120)), Routes().Append(Mark(5000, value)), targets: targets);
        var d = Build(rec, new ReportOptions(Masked: false, IncludeMemos: true));
        var html = ReportHtml.Write(d);
        Assert.Empty(ReportHtml.Verify(html));
        var x = Xml(html);
        Assert.DoesNotContain(x.Descendants(), e => e.Name.LocalName is "script" or "img");
        Assert.DoesNotContain(x.Descendants().Attributes(), a => a.Name.LocalName.StartsWith("on", StringComparison.Ordinal));
        var names = x.Descendants().Where(e => (string?)e.Attribute("data-field") == "target.name").Select(e => e.Value).ToArray();
        Assert.Equal(ReportHtml.Normalize(XmlSafe(value)), ReportHtml.Normalize(names[0]));
    }

    /// <summary>XML에 쓸 수 없는 문자(제어 문자, 짝 없는 대리 문자)는 U+FFFD로.</summary>
    private static string XmlSafe(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
            sb.Append(char.IsSurrogate(c) || (char.IsControl(c) && c is not '\t' and not '\n' and not '\r') ? '�' : c);
        }
        return sb.ToString();
    }

    [Fact]
    public void TheTextOrderOfTheFileIsTheTextOrderOfTheDocument()
    {
        foreach (var options in new[] { new ReportOptions(), new ReportOptions(Masked: false, IncludeMemos: true, EpisodeDetails: 10) })
        {
            var d = Build(Typical(), options, diagnoses: [Diagnosis(W0.AddMinutes(3))]);
            var html = ReportHtml.Write(d);
            Assert.Equal(Normalized(d.Texts()), ReportHtml.BodyTexts(html));
        }
    }

    [Fact]
    public void FieldValuesInTheFileAreTheDocumentFieldsWithFixedNames()
    {
        var d = Build(Typical(), new ReportOptions(IncludeMemos: true), diagnoses: [Diagnosis(W0.AddMinutes(3))]);
        var html = ReportHtml.Write(d);
        var fromFile = ReportHtml.FieldValues(html);
        Assert.Equal(d.Fields().Select(f => (f.Field, ReportHtml.Normalize(f.Value))), fromFile);
        Assert.All(fromFile, f => Assert.Matches("^[a-z]+(\\.[a-zA-Z]+)+$", f.Field));
    }

    [Fact]
    public void SvgElementsMatchTheFigureDataAndItsProjection()
    {
        var rec = Typical();
        var d = Build(rec);
        var f = ((ReportFigure)d.Sections.Single(s => s.Id == "timeline").Blocks[0]).Figure;
        var x = Xml(ReportHtml.Write(d));
        var svg = x.Descendants(Svg + "svg").Single();
        var bands = svg.Descendants(Svg + "rect").Where(e => ((string?)e.Attribute("class"))?.StartsWith("band", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(f.Bands.Count, bands.Length);
        for (int i = 0; i < bands.Length; i++)
        {
            Assert.Equal(Markup.Number(TimelineFigure.Left(f.Bands[i].FromColumn)), (string?)bands[i].Attribute("x"));
            Assert.Equal(Markup.Number(f.Bands[i].ToColumn - f.Bands[i].FromColumn), (string?)bands[i].Attribute("width"));
            Assert.Equal("band band-" + TimelineSvg.Kind(f.Bands[i].Kind), (string?)bands[i].Attribute("class"));
        }
        var internet = svg.Descendants(Svg + "polyline").Where(e => (string?)e.Attribute("class") == "line-internet").ToArray();
        Assert.Equal(f.Internet.Count(s => s.Count > 0), internet.Length);
        var firstPoints = ((string)internet[0].Attribute("points")!).Split(' ');
        Assert.Equal(f.Internet[0].Count, firstPoints.Length);
        Assert.Equal($"{Markup.Number(TimelineFigure.X(f.Internet[0][0].Column))},{Markup.Number(f.Y(f.Internet[0][0].ValueMs))}", firstPoints[0]);
        Assert.Equal(f.Marks.Count, svg.Descendants(Svg + "line").Count(e => (string?)e.Attribute("class") == "mark"));
        // 수치는 고정 문화권 (소수점 점, 천 단위 구분 없음)
        foreach (var a in svg.Descendants().Attributes().Where(a => a.Name.LocalName is "x" or "y" or "width" or "height" or "x1" or "y1"))
            Assert.Matches(@"^-?\d+(\.\d{1,2})?$", a.Value);
        Assert.Equal(f.Title, (string)svg.Element(Svg + "title")!);
        Assert.Equal(f.Description, (string)svg.Element(Svg + "desc")!);
    }

    [Fact]
    public void BandsInTheSameColumnKeepTheHigherPriorityKind()
    {
        // 공백 > 링크 끊김 > 외부 응답 없음 > 일부 응답 없음 (구현 중 결정 3)
        var rec = Typical();
        var a = Analyze(rec) with { Links = FourStates() };   // 링크 끊김 100~104초 = 첫 끊김(100~105초)과 겹침
        var d = Build(rec, a: a);
        var f = ((ReportFigure)d.Sections.Single(s => s.Id == "timeline").Blocks[0]).Figure;
        int col = f.Column(101_000);
        Assert.Equal(FigureBandKind.LinkDown, f.Bands.Single(b => b.FromColumn <= col && col < b.ToColumn).Kind);
        Assert.All(f.Bands.Zip(f.Bands.Skip(1)), p => Assert.True(p.First.ToColumn <= p.Second.FromColumn));
        Assert.True(f.Bands.Count <= TimelineFigure.Columns);
    }

    [Fact]
    public void ManyMarksInOneColumnBecomeOneMarkWithACount()
    {
        var events = Routes().Concat(Enumerable.Range(0, 100).Select(i => Mark(300_000 + i * 5, null, "m" + i))).ToArray();
        var d = Build(Record(Rounds(Ok(43200 / 60), Ok(43200 / 60), Ok(43200 / 60)), events));
        var f = ((ReportFigure)d.Sections.Single(s => s.Id == "timeline").Blocks[0]).Figure;
        var m = Assert.Single(f.Marks);
        Assert.Equal(100, m.Count);
    }

    [Fact]
    public void NumbersAreFiniteAndCultureFree()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1234.5", Markup.Number(1234.5));
            Assert.Equal("0", Markup.Number(double.NaN));
            Assert.Equal("0", Markup.Number(double.PositiveInfinity));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    // ---------------- G4 1라운드 a5·a6 ----------------

    [Fact]
    public void SinglePointSegmentsAreDrawnAsDots()
    {
        // 외부 하나만 번갈아 손실: 열마다 성공·손실이 섞여 한 점짜리 구간이 생긴다
        string alt = string.Concat(Enumerable.Range(0, 600).Select(i => i % 2 == 0 ? 'o' : 'x'));
        var d = Build(Record(Rounds(Ok(600), alt, alt)));
        var f = ((ReportFigure)d.Sections.Single(s => s.Id == "timeline").Blocks[0]).Figure;
        var svg = Xml(ReportHtml.Write(d)).Descendants(Svg + "svg").Single();
        int singles = f.Internet.Count(s => s.Count == 1) + f.Router.Count(s => s.Count == 1);
        int dots = svg.Descendants(Svg + "circle").Count(e => ((string?)e.Attribute("class"))?.EndsWith("-dot", StringComparison.Ordinal) == true);
        Assert.Equal(singles, dots);
        int lines = svg.Descendants(Svg + "polyline").Count();
        Assert.Equal(f.Internet.Count(s => s.Count > 1) + f.Router.Count(s => s.Count > 1), lines);
    }

    [Theory]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://example.invalid/\" />", "허용하지 않는 속성 http-equiv")]
    [InlineData("<style>.a { background: \\75 rl(https://example.invalid/a.png); }</style>", "CSS 이스케이프")]
    [InlineData("<a href=\"#summary\">x</a>", "요소 a")]
    [InlineData("<p onmouseover=\"x()\">x</p>", "허용하지 않는 속성 onmouseover")]
    // G4 M8 2라운드 a4: SVG 색 속성의 CSS 이스케이프·외부 값
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect fill=\"\\75 rl(https://example.invalid/p.svg#x)\" /></svg>", "속성 이스케이프 fill")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect stroke=\"var(--x)\" /></svg>", "허용하지 않는 색 값 stroke")]
    public void TheAllowListRejectsAnythingTheDocumentDoesNotUse(string body, string expected)
    {
        var html = "<!DOCTYPE html>\n<html><head><title>t</title></head><body>" + body + "</body></html>";
        Assert.Contains(ReportHtml.Verify(html), p => p.StartsWith(expected, StringComparison.Ordinal));
    }
}
