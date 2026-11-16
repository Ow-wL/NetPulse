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
    public static ReportLook Document(DocumentBrushes d) => new(d.Paper, d.Bg, d.Ink, d.Muted, d.Accent, d.Bad, d.Warn, d.Edge, d.BadSoft, d.Link, d.Mark);

    public static ReportLook System(TokenBrushes b) => new(b.Bg, b.Bg, b.Ink, b.Muted, b.Accent, b.Bad, b.Warn, b.Edge, b.Bg, b.Ink, b.Ok);
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

    private static FrameworkElement Table(ReportTable t, ReportLook look)
    {
        var stack = new StackPanel { Spacing = Space.S1 };
        stack.Children.Add(Fonts.Wrapped(t.Caption, Typography.Label, look.Ink));
        var grid = new Grid();
        int columns = Math.Max(1, t.Columns.Count);
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cellPadding = new Thickness(Space.S2, Space.S1, Space.S2, Space.S1);
        void Band(int row, Brush brush)
        {
            var bg = new Border { Background = brush };
            Grid.SetRow(bg, row);
            Grid.SetColumnSpan(bg, columns);
            grid.Children.Add(bg);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Band(0, look.Head);
        for (int c = 0; c < t.Columns.Count; c++)
        {
            var h = Fonts.Wrapped(t.Columns[c].Text, Typography.Caption, look.Ink);
            h.Padding = cellPadding;
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            int y = r + 1;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Rectangle { Height = Size.Hairline, Fill = look.Edge, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(line, y);
            Grid.SetColumnSpan(line, columns);
            grid.Children.Add(line);
            var brush = row.Kind == RowKind.Normal ? look.Ink : look.Muted;
            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = Fonts.Wrapped(row.Cells[c].Value.Text, Typography.Caption, brush);
                cell.Padding = cellPadding;
                if (row.Kind == RowKind.Note) Grid.SetColumnSpan(cell, columns);
                if (row.Kind == RowKind.Sub && c == 0) cell.Padding = new Thickness(Space.S5, Space.S1, Space.S2, Space.S1);
                Grid.SetRow(cell, y);
                Grid.SetColumn(cell, Math.Min(c, columns - 1));
                grid.Children.Add(cell);
            }
        }
        stack.Children.Add(new Border { BorderBrush = look.Edge, BorderThickness = new Thickness(Size.Hairline), Child = grid });
        return stack;
    }

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
        Place(new Rectangle { Width = TimelineFigure.Columns, Height = TimelineFigure.PlotHeight, Stroke = look.Edge, StrokeThickness = Size.Hairline }, TimelineFigure.PlotLeft, TimelineFigure.PlotTop);
        foreach (var v in f.ValueTicks)
            Place(new Line { X1 = 0, Y1 = 0, X2 = TimelineFigure.Columns, Y2 = 0, Stroke = look.Edge, StrokeThickness = Size.Hairline }, TimelineFigure.PlotLeft, f.Y(v));
        foreach (var b in f.Bands)
        {
            bool link = b.Kind == FigureBandKind.LinkDown;
            var (brush, opacity) = b.Kind switch
            {
                FigureBandKind.Gap => (look.Muted, Emphasis.Unconfirmed),
                FigureBandKind.ExternalNone => (look.Bad, Emphasis.LossBand * 2),
                FigureBandKind.PartialNone => (look.Warn, Emphasis.LossBand * 2),
                _ => (look.Link, Emphasis.Full),
            };
            double h = link ? Size.TimelineLane : TimelineFigure.PlotHeight;
            double y = link ? TimelineFigure.PlotTop + TimelineFigure.PlotHeight - Size.TimelineLane : TimelineFigure.PlotTop;
            Place(new Rectangle { Width = b.ToColumn - b.FromColumn, Height = h, Fill = brush, Opacity = opacity }, TimelineFigure.Left(b.FromColumn), y);
        }
        void Lines(IReadOnlyList<IReadOnlyList<FigurePoint>> series, Brush brush, bool dashed)
        {
            foreach (var seg in series)
            {
                if (seg.Count == 0) continue;
                var line = new Polyline { Stroke = brush, StrokeThickness = ChartStyle.Line2 };
                if (dashed) line.StrokeDashArray = new DoubleCollection { ChartStyle.DashOn, ChartStyle.DashOff };
                foreach (var p in seg) line.Points.Add(new Point(TimelineFigure.X(p.Column), f.Y(p.ValueMs)));
                canvas.Children.Add(line);
            }
        }
        Lines(f.Router, look.Muted, true);
        Lines(f.Internet, look.Accent, false);
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
        foreach (var tick in f.TimeTicks)
        {
            var t = Fonts.Text(tick.Label, Typography.Caption, look.Muted);
            Place(t, TimelineFigure.X(tick.Column) - Space.S5, TimelineFigure.TickTextY - Space.S4);
        }
        Brush[] swatches = [look.Accent, look.Muted, look.Bad, look.Warn, look.Link, look.Muted, look.Mark];
        for (int i = 0; i < TimelineFigure.LegendLabels.Length; i++)
        {
            double x = TimelineFigure.PlotLeft + i % 4 * (TimelineFigure.Columns / 4.0), y = TimelineFigure.LegendY + i / 4 * Space.S5;
            Place(new Rectangle { Width = Space.S3, Height = Space.S3, Fill = swatches[i], Stroke = look.Edge, StrokeThickness = Size.Hairline }, x, y - Space.S3);
            Place(Fonts.Text(TimelineFigure.LegendLabels[i], Typography.Caption, look.Ink), x + Space.S4, y - Space.S4);
        }
        stack.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, HorizontalAlignment = HorizontalAlignment.Left });
        group.Children.Add(stack);
        return group;
    }
}
