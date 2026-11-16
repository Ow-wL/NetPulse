using System.Globalization;
using System.Text;
using NetPulse.Core.Presentation;

namespace NetPulse.Core.Report;

/// <summary>그림 띠 종류. 같은 열에 겹치면 값이 큰 쪽 (공백 &gt; 링크 끊김 &gt; 외부 응답 없음 &gt; 일부 응답 없음, 구현 중 결정 3).</summary>
public enum FigureBandKind { PartialNone = 0, ExternalNone = 1, LinkDown = 2, Gap = 3 }

/// <summary>열 [From, To)의 띠 하나.</summary>
public sealed record FigureBand(FigureBandKind Kind, int FromColumn, int ToColumn);

/// <summary>지연 점: 열과 그 열의 최댓값 (급증이 사라지지 않게).</summary>
public sealed record FigurePoint(int Column, double ValueMs);

public sealed record FigureMark(int Column, int Count);

public sealed record FigureTick(int Column, string Label);

/// <summary>
/// 타임라인 그림의 공통 데이터 (M8 설계 3.5, 구현 중 결정 3·4). 지연 선은 <see cref="TimelineModel.Frame"/>(열 수 = <see cref="Columns"/>)의 점,
/// 띠·표시는 여기서 열 단위로 합친다. SVG와 미리 보기가 이 값과 <see cref="X"/>·<see cref="Y"/> 투영을 같이 쓴다.
/// </summary>
public sealed record TimelineFigure(
    long FromMs, long ToMs, double AxisMaxMs,
    IReadOnlyList<IReadOnlyList<FigurePoint>> Internet, IReadOnlyList<IReadOnlyList<FigurePoint>> Router,
    IReadOnlyList<FigureBand> Bands, IReadOnlyList<FigureMark> Marks, IReadOnlyList<FigureTick> TimeTicks, IReadOnlyList<double> ValueTicks,
    string Title, string Description)
{
    public const int Width = 960, Height = 324, Columns = 900, PlotLeft = 48, PlotTop = 12, PlotHeight = 200;
    public const int TickTextY = PlotTop + PlotHeight + 18, LegendY = PlotTop + PlotHeight + 52;

    public static readonly string[] LegendLabels =
    [
        "인터넷 지연 (응답한 외부 대상의 중앙값)", "공유기 지연", "외부 대상 모두 응답 없음", "일부 대상 응답 없음", "링크 끊김 관측", "관측 공백", "증상 표시",
    ];

    /// <summary>열 가운데의 x.</summary>
    public static double X(int column) => PlotLeft + column + 0.5;

    public static double Left(int column) => PlotLeft + column;

    /// <summary>범례 한 줄에 셋 (글이 겹치지 않게).</summary>
    public const int LegendPerRow = 3, LegendStep = 300, LegendRowHeight = 22;

    /// <summary>오른쪽 끝 가까운 시각 눈금은 글 끝을 눈금에 맞춘다 (그림 밖으로 잘리지 않게).</summary>
    public static bool AtRightEdge(FigureTick t) => t.Column >= Columns - 40;

    public double Y(double ms) => PlotTop + PlotHeight * (1 - Math.Clamp(ms / AxisMaxMs, 0, 1));

    public int Column(double ms)
    {
        double span = Math.Max(1, ToMs - FromMs);
        return Math.Clamp((int)Math.Floor((ms - FromMs) * Columns / span), 0, Columns - 1);
    }

    public static string ValueLabel(double ms) => ms.ToString("0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>보이는 글 순서: 제목, 설명, 값 눈금(위에서 아래), 시각 눈금, 범례. SVG 요소 순서와 같다.</summary>
    public IEnumerable<ReportRun> Texts()
    {
        yield return ReportRun.Gen(Title);
        yield return ReportRun.Gen(Description);
        foreach (var v in ValueTicks) yield return ReportRun.Gen(ValueLabel(v));
        foreach (var t in TimeTicks) yield return ReportRun.Gen(t.Label);
        foreach (var l in LegendLabels) yield return ReportRun.Gen(l);
    }

    /// <summary>
    /// 기록 전체 → 그림. <paramref name="frame"/>은 [from, to]를 <see cref="Columns"/>열로 그린 장면,
    /// <paramref name="allBands"/>·<paramref name="downs"/>·<paramref name="marks"/>는 전체 목록 (설명 글의 수는 합치기 전 원본 수).
    /// </summary>
    public static TimelineFigure Build(TimelineFrame frame, IReadOnlyList<TimelineBand> allBands, IReadOnlyList<LinkDownView> downs,
        IReadOnlyList<Records.SymptomMark> marks, DateTimeOffset start, TimeSpan offset)
    {
        long from = frame.FromMs, to = Math.Max(frame.FromMs + 1, frame.ToMs);
        var shell = new TimelineFigure(from, to, frame.AxisMaxMs, [], [], [], [], [], [], "", "");

        // 띠: 열마다 우선순위가 가장 높은 종류 하나
        var kinds = new int[Columns];
        Array.Fill(kinds, -1);
        void Paint(long a, long b, FigureBandKind k)
        {
            if (b <= from || a >= to) return;
            int c0 = shell.Column(Math.Max(a, from)), c1 = shell.Column(Math.Min(b, to) - 1);
            for (int c = c0; c <= Math.Max(c0, c1); c++) kinds[c] = Math.Max(kinds[c], (int)k);
        }
        foreach (var b in allBands)
            Paint(b.FromMs, b.ToMs, b.Kind switch
            {
                BandKind.Gap => FigureBandKind.Gap,
                BandKind.ExternalNone => FigureBandKind.ExternalNone,
                _ => FigureBandKind.PartialNone,
            });
        foreach (var d in downs) Paint(d.FromMs, Math.Max(d.ToMs, d.FromMs + 1), FigureBandKind.LinkDown);
        var bands = new List<FigureBand>();
        for (int c = 0; c < Columns;)
        {
            if (kinds[c] < 0) { c++; continue; }
            int e = c;
            while (e + 1 < Columns && kinds[e + 1] == kinds[c]) e++;
            bands.Add(new FigureBand((FigureBandKind)kinds[c], c, e + 1));
            c = e + 1;
        }

        // 표시: 열마다 하나 + 개수
        var markColumns = marks.Where(m => m.AtMs >= from && m.AtMs <= to).GroupBy(m => shell.Column(m.AtMs))
            .OrderBy(g => g.Key).Select(g => new FigureMark(g.Key, g.Count())).ToArray();

        IReadOnlyList<IReadOnlyList<FigurePoint>> Points(IReadOnlyList<IReadOnlyList<TimelinePoint>> series) =>
            series.Select(s => (IReadOnlyList<FigurePoint>)s.Select(p => new FigurePoint(shell.Column(p.XMs), p.MaxMs)).ToArray()).ToArray();

        // 시각 눈금: 8개 이하가 되는 가장 작은 간격
        long span = to - from;
        long[] steps = [60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000, 3_600_000, 7_200_000, 10_800_000];
        long step = steps.FirstOrDefault(s => span / s <= 7, steps[^1]);
        string format = span <= 600_000 ? "HH:mm:ss" : "HH:mm";
        var ticks = new List<FigureTick>();
        for (long t = from; t <= to; t += step)
            ticks.Add(new FigureTick(shell.Column(t), (start + TimeSpan.FromMilliseconds(t)).ToOffset(offset).ToString(format, CultureInfo.InvariantCulture)));

        double axis = frame.AxisMaxMs;
        var valueTicks = new[] { axis, axis / 2, 0 };

        int external = allBands.Count(b => b.Kind == BandKind.ExternalNone), partial = allBands.Count(b => b.Kind == BandKind.PartialNone),
            gaps = allBands.Count(b => b.Kind == BandKind.Gap);
        string description = $"측정 전체 기간의 지연과 응답 없음. 외부 대상 모두 응답 없음 {external}번, 일부 대상 응답 없음 {partial}번, "
            + $"링크 끊김 관측 {downs.Count}번, 증상 표시 {marks.Count}개, 관측 공백 {gaps}번. 띠는 실제 끊김의 시작·끝이 아니라 응답 없음이 관측된 회차 칸입니다.";

        return shell with
        {
            Internet = Points(frame.Internet), Router = Points(frame.Router), Bands = bands, Marks = markColumns, TimeTicks = ticks,
            ValueTicks = valueTicks, Title = "측정 전체 타임라인", Description = description,
        };
    }
}

/// <summary>그림 → 인라인 SVG (M8 설계 3.4·3.5). 무늬는 내부 <c>url(#…)</c>만, 수치는 유한한 값을 고정 문화권으로.</summary>
public static class TimelineSvg
{
    public const string PatternNone = "np-none", PatternGap = "np-gap", PatternPartial = "np-partial";

    public static void Write(StringBuilder sb, TimelineFigure f, ReportPalette p)
    {
        string N(double v) => Markup.Number(v);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(TimelineFigure.Width).Append(' ').Append(TimelineFigure.Height)
            .Append("\" role=\"img\" aria-labelledby=\"np-figure-title np-figure-desc\" class=\"timeline\">");
        sb.Append("<title id=\"np-figure-title\">").Append(Markup.Text(f.Title)).Append("</title>");
        sb.Append("<desc id=\"np-figure-desc\">").Append(Markup.Text(f.Description)).Append("</desc>");
        sb.Append("<defs>");
        sb.Append("<pattern id=\"").Append(PatternNone).Append("\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\">")
            .Append("<rect width=\"6\" height=\"6\" fill=\"").Append(p.BadSoft).Append("\" />")
            .Append("<line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" stroke=\"").Append(p.Bad).Append("\" stroke-width=\"2\" /></pattern>");
        sb.Append("<pattern id=\"").Append(PatternPartial).Append("\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\">")
            .Append("<rect width=\"6\" height=\"6\" fill=\"").Append(p.WarnSoft).Append("\" />")
            .Append("<circle cx=\"3\" cy=\"3\" r=\"1\" fill=\"").Append(p.Warn).Append("\" /></pattern>");
        sb.Append("<pattern id=\"").Append(PatternGap).Append("\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(-45)\">")
            .Append("<line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"8\" stroke=\"").Append(p.Muted).Append("\" stroke-width=\"1\" /></pattern>");
        sb.Append("</defs>");

        // 그래프 면과 값 눈금
        sb.Append("<rect class=\"plot\" x=\"").Append(TimelineFigure.PlotLeft).Append("\" y=\"").Append(TimelineFigure.PlotTop)
            .Append("\" width=\"").Append(TimelineFigure.Columns).Append("\" height=\"").Append(TimelineFigure.PlotHeight)
            .Append("\" fill=\"").Append(p.Surface).Append("\" stroke=\"").Append(p.Edge).Append("\" />");
        foreach (var v in f.ValueTicks)
        {
            double y = f.Y(v);
            sb.Append("<line class=\"grid\" x1=\"").Append(TimelineFigure.PlotLeft).Append("\" y1=\"").Append(N(y)).Append("\" x2=\"")
                .Append(TimelineFigure.PlotLeft + TimelineFigure.Columns).Append("\" y2=\"").Append(N(y)).Append("\" stroke=\"").Append(p.Edge).Append("\" />");
        }

        // 띠 (그래프 뒤)
        foreach (var b in f.Bands)
        {
            string fill = b.Kind switch
            {
                FigureBandKind.Gap => $"url(#{PatternGap})",
                FigureBandKind.ExternalNone => $"url(#{PatternNone})",
                FigureBandKind.PartialNone => $"url(#{PatternPartial})",
                _ => p.Link,
            };
            bool link = b.Kind == FigureBandKind.LinkDown;
            double y = link ? TimelineFigure.PlotTop + TimelineFigure.PlotHeight - 10 : TimelineFigure.PlotTop;
            double h = link ? 10 : TimelineFigure.PlotHeight;
            sb.Append("<rect class=\"band band-").Append(Kind(b.Kind)).Append("\" x=\"").Append(N(TimelineFigure.Left(b.FromColumn))).Append("\" y=\"").Append(N(y))
                .Append("\" width=\"").Append(N(b.ToColumn - b.FromColumn)).Append("\" height=\"").Append(N(h)).Append("\" fill=\"").Append(fill).Append("\" />");
        }

        // 지연 선
        void Lines(IReadOnlyList<IReadOnlyList<FigurePoint>> series, string cls, string color, string? dash)
        {
            foreach (var seg in series)
            {
                if (seg.Count == 0) continue;
                sb.Append("<polyline class=\"").Append(cls).Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"1.5\"");
                if (dash is not null) sb.Append(" stroke-dasharray=\"").Append(dash).Append('"');
                sb.Append(" points=\"");
                for (int i = 0; i < seg.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(N(TimelineFigure.X(seg[i].Column))).Append(',').Append(N(f.Y(seg[i].ValueMs)));
                }
                sb.Append("\" />");
            }
        }
        Lines(f.Router, "line-router", p.Muted, "4 3");
        Lines(f.Internet, "line-internet", p.Accent, null);

        // 증상 표시
        foreach (var m in f.Marks)
        {
            double x = TimelineFigure.X(m.Column);
            sb.Append("<line class=\"mark\" x1=\"").Append(N(x)).Append("\" y1=\"").Append(TimelineFigure.PlotTop).Append("\" x2=\"").Append(N(x))
                .Append("\" y2=\"").Append(TimelineFigure.PlotTop + TimelineFigure.PlotHeight).Append("\" stroke=\"").Append(p.Mark).Append("\" stroke-width=\"")
                .Append(m.Count > 1 ? "2" : "1").Append("\" />");
        }

        // 눈금 글
        foreach (var v in f.ValueTicks)
            sb.Append("<text class=\"tick\" x=\"").Append(TimelineFigure.PlotLeft - 6).Append("\" y=\"").Append(N(f.Y(v) + 4))
                .Append("\" text-anchor=\"end\" fill=\"").Append(p.Muted).Append("\">").Append(Markup.Text(TimelineFigure.ValueLabel(v))).Append("</text>");
        foreach (var t in f.TimeTicks)
            sb.Append("<text class=\"tick\" x=\"").Append(N(TimelineFigure.X(t.Column))).Append("\" y=\"").Append(TimelineFigure.TickTextY)
                .Append("\" text-anchor=\"").Append(TimelineFigure.AtRightEdge(t) ? "end" : "middle").Append("\" fill=\"").Append(p.Muted).Append("\">").Append(Markup.Text(t.Label)).Append("</text>");

        // 범례: 견본 + 글
        string[] swatches = [p.Accent, p.Muted, $"url(#{PatternNone})", $"url(#{PatternPartial})", p.Link, $"url(#{PatternGap})", p.Mark];
        for (int i = 0; i < TimelineFigure.LegendLabels.Length; i++)
        {
            int col = i % TimelineFigure.LegendPerRow, row = i / TimelineFigure.LegendPerRow;
            double x = TimelineFigure.PlotLeft + col * TimelineFigure.LegendStep, y = TimelineFigure.LegendY + row * TimelineFigure.LegendRowHeight;
            sb.Append("<rect class=\"swatch\" x=\"").Append(N(x)).Append("\" y=\"").Append(N(y - 10)).Append("\" width=\"14\" height=\"12\" fill=\"")
                .Append(swatches[i]).Append("\" stroke=\"").Append(p.Edge).Append("\" />");
            sb.Append("<text class=\"legend\" x=\"").Append(N(x + 20)).Append("\" y=\"").Append(N(y)).Append("\" fill=\"").Append(p.Ink).Append("\">")
                .Append(Markup.Text(TimelineFigure.LegendLabels[i])).Append("</text>");
        }
        sb.Append("</svg>");
    }

    public static string Kind(FigureBandKind k) => k switch
    {
        FigureBandKind.Gap => "gap", FigureBandKind.LinkDown => "link", FigureBandKind.ExternalNone => "none", _ => "partial",
    };
}
