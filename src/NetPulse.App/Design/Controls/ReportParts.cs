using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Core.Report;
using NetPulse.Design.Tokens;
using Point = Windows.Foundation.Point;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 미리 보기 색 (M8 설계 4.1 [1-6]): 보통은 문서 팔레트(내보낼 파일과 같은 밝은 면), 고대비에서는 시스템 색. 내용은 같다.
/// </summary>
public sealed record ReportLook(Brush Paper, Brush Head, Brush Ink, Brush Muted, Brush Accent, Brush Bad, Brush Warn, Brush Edge, Brush BadSoft, Brush Link, Brush Mark)
{
    public static ReportLook Document(DocumentBrushes d) => new(d.Paper, d.Bg, d.Ink, d.Muted, d.Accent, d.Bad, d.Warn, d.Edge, d.BadSoft, d.Link, d.Mark) { Signal = d.Signal };

    public static ReportLook System(TokenBrushes b) => new(b.Bg, b.Bg, b.Ink, b.Muted, b.Accent, b.Bad, b.Warn, b.Edge, b.Bg, b.Ink, b.Ok) { Signal = b.Signal };

    /// <summary>Wi‑Fi 신호 품질 선 (M12 설계 C). 고대비에서는 시스템 색(Hotlight).</summary>
    public Brush? Signal { get; init; }
}

/// <summary>
/// 리포트 문서 → XAML (M8 설계 4.1). 글 순서는 <see cref="ReportDocument.Texts"/>와 같고, 그림은 <see cref="TimelineFigure"/>의 데이터와
/// 투영을 SVG와 같이 쓴다. 절마다 요소 하나를 돌려준다 (프레임마다 나눠 붙이게).
/// </summary>
public static class ReportParts
{
    /// <summary>머리: 제목과 머리 문단.</summary>
    public static FrameworkElement Head(ReportDocument d, ReportLook look)
    {
        var stack = new StackPanel { Spacing = Space.S2 };
        var title = Fonts.Wrapped(d.Title, Typography.Headline, look.Ink);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        AutomationProperties.SetAutomationId(title, "report.preview.title");
        stack.Children.Add(title);
        foreach (var r in d.Lead) stack.Children.Add(Fonts.Wrapped(r.Text, Typography.Body, look.Ink));
        return stack;
    }

    public static FrameworkElement Section(ReportSection s, ReportLook look)
    {
        var group = new GroupPanel("report.preview." + s.Id, s.Title);
        var stack = new StackPanel { Spacing = Space.S3 };
        var title = Fonts.Wrapped(s.Title, Typography.Headline, look.Ink);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        stack.Children.Add(title);
        stack.Children.Add(new Rectangle { Height = Size.Hairline, Fill = look.Edge, HorizontalAlignment = HorizontalAlignment.Stretch });
        foreach (var b in s.Blocks) stack.Children.Add(Block(b, look));
        group.Children.Add(stack);
        return group;
    }

    private static FrameworkElement Block(ReportBlock b, ReportLook look) => b switch
    {
        ReportParagraph p => Paragraph(p, look),
        ReportHeading h => Heading(h, look),
        ReportFacts f => Facts(f, look),
        ReportTable t => Table(t, look),
        ReportFigure g => Figure(g.Figure, look),
        _ => new Grid(),
    };

    private static FrameworkElement Paragraph(ReportParagraph p, ReportLook look)
    {
        var text = string.Concat(p.Runs.Select(r => r.Text));
        var brush = p.Kind == ParagraphKind.Note ? look.Muted : look.Ink;
        var t = Fonts.Wrapped(text, Typography.Body, brush);
        if (p.Kind != ParagraphKind.Warning) return t;
        // 경고: 왼쪽 굵은 띠 + 옅은 바탕 (색만으로 구별하지 않게 아이콘도)
        var row = new Grid { ColumnSpacing = Space.S2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Icons.Make(Icons.Alert, look.Bad, Size.IconS);
        icon.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(icon);
        Grid.SetColumn(t, 1);
        row.Children.Add(t);
        return new Border
        {
            Background = look.BadSoft, BorderBrush = look.Bad, BorderThickness = new Thickness(Size.FocusOuter + Size.FocusOuter, 0, 0, 0),
            Padding = new Thickness(Space.S3, Space.S2, Space.S3, Space.S2), Child = row,
        };
    }

    private static FrameworkElement Heading(ReportHeading h, ReportLook look)
    {
        var t = Fonts.Wrapped(h.Text.Text, Typography.Label, look.Ink);
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level3);
        t.Margin = new Thickness(0, Space.S2, 0, 0);
        return t;
    }

    private static FrameworkElement Facts(ReportFacts f, ReportLook look)
    {
        var grid = new Grid { ColumnSpacing = Space.S4, RowSpacing = Space.S1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Size.FieldS + Space.S6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < f.Items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Fonts.Wrapped(f.Items[i].Label, Typography.Caption, look.Muted);
            var value = Fonts.Wrapped(f.Items[i].Value.Text, Typography.Label, look.Ink);
            AutomationProperties.SetAutomationId(value, "report.field." + f.Items[i].Field);
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        return grid;
    }

    /// <summary>큰 표는 이만큼씩 나눠 붙인다 (한 프레임에 수백 줄을 만들지 않게, M8 bench).</summary>
    private static int RowsPerPiece => FrameBudget.ReportRowsPerPiece;

    private static FrameworkElement Table(ReportTable t, ReportLook look)
    {
        var (whole, rows) = TableShell(t, look);
        for (int r = 0; r < t.Rows.Count; r += RowsPerPiece) rows.Children.Add(Rows(t, r, Math.Min(RowsPerPiece, t.Rows.Count - r), look));
        return whole;
    }

    /// <summary>표 껍데기: 이름, 테두리, 머리 줄. 몸통 줄 묶음은 돌려준 패널에 붙인다.</summary>
    private static (FrameworkElement Whole, StackPanel Rows) TableShell(ReportTable t, ReportLook look)
    {
        var stack = new StackPanel { Spacing = Space.S1 };
        stack.Children.Add(Fonts.Wrapped(t.Caption, Typography.Label, look.Ink));
        int columns = Math.Max(1, t.Columns.Count);
        var head = Columns(columns);
        var band = new Border { Background = look.Head };
        Grid.SetColumnSpan(band, columns);
        head.Children.Add(band);
        for (int c = 0; c < t.Columns.Count; c++)
        {
            var h = Fonts.Wrapped(t.Columns[c].Text, Typography.Caption, look.Ink);
            h.Padding = CellPadding;
            Grid.SetColumn(h, c);
            head.Children.Add(h);
        }
        var rows = new StackPanel();
        rows.Children.Add(head);
        stack.Children.Add(new Border { BorderBrush = look.Edge, BorderThickness = new Thickness(Size.Hairline), Child = rows });
        return (stack, rows);
    }

    private static Thickness CellPadding => new(Space.S2, Space.S1, Space.S2, Space.S1);

    private static Grid Columns(int columns)
    {
        var g = new Grid();
        for (int c = 0; c < columns; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    /// <summary>
    /// 몸통 줄 묶음 하나 (열 너비는 머리와 같은 비율이라 줄이 맞는다). 줄마다 화면 읽기 이름이 있는 묶음:
    /// "열 이름 값, 열 이름 값, …" (설계 4.3 "표는 행 이름으로", G4 M8 1라운드 b4).
    /// </summary>
    private static StackPanel Rows(ReportTable t, int from, int count, ReportLook look)
    {
        int columns = Math.Max(1, t.Columns.Count);
        var stack = new StackPanel();
        for (int k = 0; k < count; k++)
        {
            var row = t.Rows[from + k];
            var name = row.Kind == RowKind.Note || row.Cells.Count == 1
                ? row.Cells[0].Value.Text
                : string.Join(", ", row.Cells.Select((c, i) => (i < t.Columns.Count ? t.Columns[i].Text + " " : "") + c.Value.Text));
            var group = new GroupPanel("report.table.row", name);
            for (int c = 0; c < columns; c++) group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var line = new Rectangle { Height = Size.Hairline, Fill = look.Edge, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumnSpan(line, columns);
            group.Children.Add(line);
            var brush = row.Kind == RowKind.Normal ? look.Ink : look.Muted;
            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = Fonts.Wrapped(row.Cells[c].Value.Text, Typography.Caption, brush);
                cell.Padding = row.Kind == RowKind.Sub && c == 0 ? new Thickness(Space.S5, Space.S1, Space.S2, Space.S1) : CellPadding;
                if (row.Kind == RowKind.Note) Grid.SetColumnSpan(cell, columns);
                Grid.SetColumn(cell, Math.Min(c, columns - 1));
                group.Children.Add(cell);
            }
            stack.Children.Add(group);
        }
        return stack;
    }

    /// <summary>
    /// 절 하나를 붙이는 조각들 (차례대로 부른다): 절 머리, 블록마다 하나, 큰 표는 머리 + 줄 묶음마다 하나.
    /// 화면이 한 프레임에 몇 조각씩만 붙여 큰 문서에서도 프레임 예산을 지킨다 (M8 bench).
    /// </summary>
    public static IEnumerable<Action> Pieces(ReportSection s, ReportLook look, Panel paper)
    {
        StackPanel stack = null!;
        yield return () =>
        {
            var group = new GroupPanel("report.preview." + s.Id, s.Title);
            stack = new StackPanel { Spacing = Space.S3 };
            var title = Fonts.Wrapped(s.Title, Typography.Headline, look.Ink);
            AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
            stack.Children.Add(title);
            stack.Children.Add(new Rectangle { Height = Size.Hairline, Fill = look.Edge, HorizontalAlignment = HorizontalAlignment.Stretch });
            group.Children.Add(stack);
            paper.Children.Add(group);
        };
        foreach (var b in s.Blocks)
        {
            if (b is ReportTable t && t.Rows.Count > RowsPerPiece)
            {
                StackPanel rows = null!;
                yield return () => { var (whole, r) = TableShell(t, look); rows = r; stack.Children.Add(whole); };
                for (int from = 0; from < t.Rows.Count; from += RowsPerPiece)
                {
                    int start = from;
                    yield return () => rows.Children.Add(Rows(t, start, Math.Min(RowsPerPiece, t.Rows.Count - start), look));
                }
            }
            else
            {
                var block = b;
                yield return () => stack.Children.Add(Block(block, look));
            }
        }
    }

    /// <summary>
    /// 띠 모양 (G4 M8 1라운드 b3): 외부 모두 응답 없음 = 높이 전체, 일부 응답 없음 = 위쪽 얇은 띠, 링크 끊김 = 아래쪽 얇은 띠, 관측 공백 = 점선 테두리.
    /// 고대비에서 빨강·노랑이 같은 시스템 색이 되어도 모양으로 구별한다. <paramref name="full"/>은 범례 견본의 높이.
    /// </summary>
    private static FrameworkElement BandShape(FigureBandKind kind, double width, ReportLook look, double? full = null)
    {
        double whole = full ?? TimelineFigure.PlotHeight;
        double lane = full is null ? Size.TimelineLane : whole / 3;
        var shape = kind switch
        {
            FigureBandKind.ExternalNone => new Rectangle { Width = width, Height = whole, Fill = look.Bad, Opacity = Emphasis.LossBand + Emphasis.LossBand },
            FigureBandKind.PartialNone => new Rectangle { Width = width, Height = lane, Fill = look.Warn },
            FigureBandKind.LinkDown => new Rectangle { Width = width, Height = lane, Fill = look.Link },
            _ => new Rectangle { Width = width, Height = whole, Stroke = look.Muted, StrokeThickness = Size.Hairline, StrokeDashArray = new DoubleCollection { ChartStyle.DashOn, ChartStyle.DashOff } },
        };
        shape.VerticalAlignment = kind == FigureBandKind.LinkDown ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        return shape;
    }

    private static double BandTop(FigureBandKind kind) =>
        kind == FigureBandKind.LinkDown ? TimelineFigure.PlotTop + TimelineFigure.PlotHeight - Size.TimelineLane : TimelineFigure.PlotTop;

    /// <summary>그림: 제목 → 설명 → 값 눈금 → 시각 눈금 → 범례 (SVG와 같은 글 순서). 960×300 화면을 폭에 맞춰 줄인다.</summary>
    public static FrameworkElement Figure(TimelineFigure f, ReportLook look)
    {
        var group = new GroupPanel("report.preview.figure", f.Title);
        var stack = new StackPanel { Spacing = Space.S2 };
        stack.Children.Add(Fonts.Wrapped(f.Title, Typography.Label, look.Ink));
        stack.Children.Add(Fonts.Wrapped(f.Description, Typography.Caption, look.Muted));
        var canvas = new Canvas { Width = TimelineFigure.Width, Height = TimelineFigure.Height, Background = look.Paper };
        void Place(UIElement e, double x, double y)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            canvas.Children.Add(e);
        }
        Place(new Rectangle { Width = f.PlotColumns, Height = TimelineFigure.PlotHeight, Stroke = look.Edge, StrokeThickness = Size.Hairline }, TimelineFigure.PlotLeft, TimelineFigure.PlotTop);
        foreach (var v in f.ValueTicks)
            Place(new Line { X1 = 0, Y1 = 0, X2 = f.PlotColumns, Y2 = 0, Stroke = look.Edge, StrokeThickness = Size.Hairline }, TimelineFigure.PlotLeft, f.Y(v));
        foreach (var b in f.Bands)
        {
            Place(BandShape(b.Kind, b.ToColumn - b.FromColumn, look), TimelineFigure.Left(b.FromColumn), BandTop(b.Kind));
        }
        void Lines(IReadOnlyList<IReadOnlyList<FigurePoint>> series, Brush brush, bool dashed)
        {
            // 한 점짜리 구간은 점으로 (SVG와 같음, G4 M8 1라운드 a5). 최악 사례에는 수백 개라 요소 하나(경로)에 모은다 (M8 bench)
            var dots = new GeometryGroup { FillRule = FillRule.Nonzero };   // 기본 EvenOdd는 겹친 이웃 점의 겹친 곳을 비운다 (G4 M8 2라운드 b7)
            foreach (var seg in series)
            {
                if (seg.Count == 0) continue;
                if (seg.Count == 1)
                {
                    dots.Children.Add(new EllipseGeometry { Center = new Point(TimelineFigure.X(seg[0].Column), f.Y(seg[0].ValueMs)), RadiusX = Space.S0, RadiusY = Space.S0 });
                    continue;
                }
                var line = new Polyline { Stroke = brush, StrokeThickness = ChartStyle.Line2 };
                if (dashed) line.StrokeDashArray = new DoubleCollection { ChartStyle.DashOn, ChartStyle.DashOff };
                foreach (var p in seg) line.Points.Add(new Point(TimelineFigure.X(p.Column), f.Y(p.ValueMs)));
                canvas.Children.Add(line);
            }
            if (dots.Children.Count > 0) canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = dots, Fill = brush });
        }
        Lines(f.Router, look.Muted, true);
        Lines(f.Internet, look.Accent, false);
        // Wi‑Fi 신호 품질 (오른쪽 축, 점선) 과 무선 어댑터 바뀜 (위쪽 짧은 막대): SVG와 같은 투영 (M12 설계 C)
        var signalBrush = look.Signal ?? look.Accent;
        foreach (var seg in f.Signal)
        {
            if (seg.Count == 0) continue;
            var line = new Polyline { Stroke = signalBrush, StrokeThickness = ChartStyle.SignalLine, StrokeDashArray = new DoubleCollection { ChartStyle.SignalDotOn, ChartStyle.SignalDotOff },
                StrokeDashCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            foreach (var p in seg) line.Points.Add(new Point(TimelineFigure.X(p.Column), TimelineFigure.SignalY(p.ValueMs)));
            if (seg.Count == 1) line.Points.Add(new Point(TimelineFigure.X(seg[0].Column) + ChartStyle.SignalLine, TimelineFigure.SignalY(seg[0].ValueMs)));
            canvas.Children.Add(line);
        }
        foreach (var c in f.SignalChanges)
            Place(new Rectangle { Width = ChartStyle.SignalChange, Height = Size.TimelineLane, Fill = signalBrush }, TimelineFigure.X(c) - ChartStyle.SignalChange / 2, TimelineFigure.PlotTop);
        foreach (var m in f.Marks)
            Place(new Line { X1 = 0, Y1 = 0, X2 = 0, Y2 = TimelineFigure.PlotHeight, Stroke = look.Mark, StrokeThickness = m.Count > 1 ? ChartStyle.Line : Size.Hairline },
                TimelineFigure.X(m.Column), TimelineFigure.PlotTop);
        foreach (var v in f.ValueTicks)
        {
            var t = Fonts.Text(TimelineFigure.ValueLabel(v), Typography.Caption, look.Muted);
            t.Width = TimelineFigure.PlotLeft - Space.S1;
            t.TextAlignment = TextAlignment.Right;
            Place(t, 0, f.Y(v) - Space.S2);
        }
        if (f.SignalLine)
            for (int i = 0; i < TimelineFigure.SignalAxisLabels.Length; i++)
                Place(Fonts.Text(TimelineFigure.SignalAxisLabels[i], Typography.Caption, look.Muted), f.SignalAxisX, TimelineFigure.SignalY(i == 0 ? NetPulse.Core.Presentation.TimelineGeometry.QualityMax : 0) - Space.S2);
        foreach (var tick in f.TimeTicks)
        {
            var t = Fonts.Text(tick.Label, Typography.Caption, look.Muted);
            t.Width = Space.S8 + Space.S8;
            bool right = TimelineFigure.AtRightEdge(tick, f.PlotColumns);
            t.TextAlignment = right ? TextAlignment.Right : TextAlignment.Center;
            Place(t, TimelineFigure.X(tick.Column) - (right ? t.Width : t.Width / 2), TimelineFigure.TickTextY - Space.S2);
        }
        var legend = f.Legend;
        for (int i = 0; i < legend.Count; i++)
        {
            double x = TimelineFigure.PlotLeft + i % TimelineFigure.LegendPerRow * TimelineFigure.LegendStep,
                y = TimelineFigure.LegendY + i / TimelineFigure.LegendPerRow * TimelineFigure.LegendRowHeight;
            // 범례 견본도 띠와 같은 모양 (고대비에서 색이 같아져도 구별)
            FrameworkElement swatch = i switch
            {
                2 => BandShape(FigureBandKind.ExternalNone, Space.S3, look, Space.S3),
                3 => BandShape(FigureBandKind.PartialNone, Space.S3, look, Space.S3),
                4 => BandShape(FigureBandKind.LinkDown, Space.S3, look, Space.S3),
                5 => BandShape(FigureBandKind.Gap, Space.S3, look, Space.S3),
                _ => new Rectangle { Width = Space.S3, Height = Space.S3, Fill = i switch { 0 => look.Accent, 1 => look.Muted, 6 => look.Mark, _ => signalBrush }, Stroke = look.Edge, StrokeThickness = Size.Hairline },
            };
            // 신호 상태 글(값 없음)은 견본 없이 글만 (SVG와 같음)
            if (i < TimelineFigure.LegendLabels.Length || f.SignalLine)
            {
                var box = new Grid { Width = Space.S3, Height = Space.S3 };
                box.Children.Add(swatch);
                Place(box, x, y - Space.S3);
            }
            Place(Fonts.Text(legend[i], Typography.Caption, look.Ink), x + Space.S4, y - Space.S4);
        }
        stack.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, HorizontalAlignment = HorizontalAlignment.Left });
        group.Children.Add(stack);
        return group;
    }
}
