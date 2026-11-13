using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

public enum CardState { Normal, Loading, Empty, Error }

/// <summary>
/// 카드: 두꺼운 유리 위 제목과 본문 (M2 설계 5.6). 상태마다 따로 그린다 (기획서 7.5 "모든 상태를 디자인한다").
/// 불러오는 중은 빈 화면 대신 윤곽이 은은하게 빛난다 (움직임 줄이기면 멈춘 윤곽).
/// </summary>
public sealed class GlassCard : GroupPanel
{
    public GlassCard(Ui ui, string automationId, string title, CardState state, UIElement? content = null, string? note = null)
        : base(automationId, title)
    {
        var stack = new StackPanel { Spacing = Space.S3 };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Fonts.Text(title, Typography.Headline, ui.Brushes.Ink));
        if (note is not null)
        {
            var n = Fonts.Text(note, Typography.Caption, ui.Brushes.Muted);
            n.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(n, 1);
            head.Children.Add(n);
        }
        stack.Children.Add(head);
        stack.Children.Add(state switch
        {
            CardState.Loading => Skeleton(ui),
            CardState.Empty => Message(ui, Icons.Info, ui.Brushes.Muted, "아직 측정 기록이 없습니다", "측정을 시작하면 여기에 결과가 쌓입니다."),
            CardState.Error => Message(ui, Icons.Alert, ui.Brushes.Bad, "기록을 읽지 못했습니다", "파일이 다른 프로그램에서 열려 있는지 확인하세요."),
            _ => content ?? new Grid(),
        });
        if (state == CardState.Error)
            stack.Children.Add(new GlassButton(ui, ButtonKind.Secondary, automationId + ".retry", "다시 시도", Icons.Refresh));
        AutomationProperties.SetItemStatus(this, state.ToString().ToLowerInvariant());
        Children.Add(ui.Glass.Surface(GlassLevel.Content, stack, Radius.Outer, new Thickness(Space.S6)));
    }

    private static UIElement Skeleton(Ui ui)
    {
        var lines = new StackPanel { Spacing = Space.S3 };
        foreach (var width in new[] { Space.S8 * 5, Space.S8 * 4, Space.S8 * 3 })
            lines.Children.Add(Pill.Apply(new Border { Height = Space.S3, Width = width, HorizontalAlignment = HorizontalAlignment.Left, Background = ui.Brushes.Faint, BorderBrush = ui.Brushes.Edge, BorderThickness = new Thickness(Size.Hairline) }));
        AutomationProperties.SetName(lines, "불러오는 중");
        ui.Motion.Breathe(lines, (float)Emphasis.SkeletonLow, (float)Emphasis.SkeletonHigh, MotionTokens.Slow * 6);
        return lines;
    }

    private static UIElement Message(Ui ui, string icon, Microsoft.UI.Xaml.Media.Brush iconBrush, string title, string body)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
        row.Children.Add(Icons.Make(icon, iconBrush, Size.IconL));
        var texts = new StackPanel { Spacing = Space.S1 };
        texts.Children.Add(Fonts.Text(title, Typography.Label, ui.Brushes.Ink));
        texts.Children.Add(Fonts.Text(body, Typography.Body, ui.Brushes.Muted));
        row.Children.Add(texts);
        return row;
    }
}

public enum Status { Ok, Warn, Bad, Unknown }

/// <summary>상태 칩: 아이콘 + 글자 (색만으로 전달하지 않는다).</summary>
public sealed class StatusChip : GroupPanel
{
    public StatusChip(Ui ui, string automationId, Status status, string label) : base(automationId, label)
    {
        var (icon, brush) = status switch
        {
            Status.Ok => (Icons.Check, ui.Brushes.Ok),
            Status.Warn => (Icons.Alert, ui.Brushes.Warn),
            Status.Bad => (Icons.Cross, ui.Brushes.Bad),
            _ => (Icons.Question, ui.Brushes.Muted),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Icons.Make(icon, brush, Size.IconS));
        row.Children.Add(Fonts.Text(label, Typography.Label, ui.Brushes.Ink));
        HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(Pill.Apply(new Border
        {
            Background = ui.Brushes.Faint,
            BorderBrush = ui.Brushes.Edge,
            BorderThickness = new Thickness(Size.Hairline),
                        Padding = new Thickness(Space.S3, Space.S1 + Space.S0, Space.S4, Space.S1 + Space.S0),
            Child = row,
        }));
    }
}
