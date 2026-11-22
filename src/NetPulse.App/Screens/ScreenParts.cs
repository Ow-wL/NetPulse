using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Analysis;
using NetPulse.Core.Presentation;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>화면들이 함께 쓰는 작은 부품과 색 고르기.</summary>
public static class ScreenParts
{
    /// <summary>등급 색 (언제나 글자·등급 이름과 함께 쓴다).</summary>
    public static Brush GradeBrush(Ui ui, Grade? g) => g switch
    {
        Grade.Good => ui.Brushes.Ok,
        Grade.Fair => ui.Brushes.Accent,
        Grade.Bad => ui.Brushes.Warn,
        Grade.VeryBad => ui.Brushes.Bad,
        _ => ui.Brushes.Muted,
    };

    /// <summary>머리의 정보 칩 ("공유기 2 ms").</summary>
    public static FrameworkElement InfoChip(Ui ui, string id, string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2, VerticalAlignment = VerticalAlignment.Center };
        var l = Fonts.Text(label, Typography.Caption, ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(l, id + ".label");
        row.Children.Add(l);
        var v = Fonts.Text(value, Typography.Label, ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(v, id + ".value");
        // 실측 대비 시험 (M12 설계 F): 라벨·값만 투명하게 그려 컨트롤 유리를 찍는다
        if (ui.Options.HideMeasuredText) l.Foreground = v.Foreground = ui.Brushes.Transparent;
        row.Children.Add(v);
        var panel = new GroupPanel(id, label + " " + value);
        panel.Children.Add(ui.Glass.Surface(GlassLevel.Control, row, Radius.Inner, new Thickness(Space.S4, Space.S2, Space.S4, Space.S2)));
        return panel;
    }

    /// <summary>빈 상태·오류 문장 (줄바꿈하는 본문).</summary>
    public static FrameworkElement Message(Ui ui, string id, string icon, Brush iconBrush, string title, string body)
    {
        var row = new Grid { ColumnSpacing = Space.S3 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var i = Icons.Make(icon, iconBrush, Size.IconL);
        i.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(i);
        var texts = new StackPanel { Spacing = Space.S1 };
        texts.Children.Add(Fonts.Text(title, Typography.Label, ui.Brushes.Ink));
        texts.Children.Add(Fonts.Wrapped(body, Typography.Body, ui.Brushes.Muted));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var g = new GroupPanel(id, title);
        g.Children.Add(row);
        return g;
    }

    /// <summary>두꺼운 유리 카드 (제목 + 오른쪽 작은 글 + 바뀌는 본문). 본문은 <see cref="CardBody.Set"/>으로 바꾼다.</summary>
    public static (GroupPanel Card, CardBody Body) Card(Ui ui, string id, string title, string? note = null, params UIElement[] headerActions)
    {
        var stack = new Grid { RowSpacing = Space.S3 };
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 제목이 먼저 자리를 갖고, 남은 폭에 작은 글(줄임표)과 행동을 둔다 (좁은 창에서 제목이 사라지지 않게)
        var head = new Grid { ColumnSpacing = Space.S3 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = Fonts.Text(title, Typography.Headline, ui.Brushes.Ink);
        t.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(t, id + ".title");   // 실측 대비 시험이 찾는 카드 제목 (M11 설계 C2)
        head.Children.Add(t);
        var n = Fonts.Text(note ?? "", Typography.Caption, ui.Brushes.Muted);
        n.VerticalAlignment = VerticalAlignment.Center;
        n.HorizontalAlignment = HorizontalAlignment.Right;
        n.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(n, note ?? "");
        AutomationProperties.SetAutomationId(n, id + ".note");
        Grid.SetColumn(n, 1);
        head.Children.Add(n);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S2 };
        foreach (var a in headerActions) actions.Children.Add(a);
        Grid.SetColumn(actions, 2);
        head.Children.Add(actions);
        stack.Children.Add(head);
        // 실측 대비 시험 (M11 설계 C2, M5 설계 7.4와 같은 방식): 카드 제목·작은 글만 투명하게 그려 그 뒤 배경을 찍는다 (공유 브러시는 바꾸지 않는다)
        if (ui.Options.HideMeasuredText) t.Foreground = n.Foreground = ui.Brushes.Transparent;
        var body = new CardBody(n);
        Grid.SetRow(body, 1);
        stack.Children.Add(body);
        var card = new GroupPanel(id, title);
        card.Children.Add(ui.Glass.Surface(GlassLevel.Content, stack, Radius.Outer, new Thickness(Space.S6)));
        return (card, body);
    }

    /// <summary>시각 표시 ("오후 2:31:05", 측정 시작 벽시계 + 경과).</summary>
    /// <summary>경과 → 시각 글. 끝난 측정의 시작 시각은 기록(UTC)에서 오므로 지금 시계의 시간대로 바꾼다.</summary>
    public static Func<long, string> TimeLabel(DateTimeOffset startedAt, DateTimeOffset now) => ms => LiveSummary.Clock(startedAt.AddMilliseconds(ms), now);

    public static readonly System.Globalization.CultureInfo Ko = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
}

/// <summary>카드 본문: 상태에 따라 내용을 바꾼다 (불러오는 중, 빈 상태, 정상, 오류).</summary>
public sealed class CardBody : Grid
{
    private readonly TextBlock note;
    private UIElement? content;

    public CardBody(TextBlock note) => this.note = note;

    public void Set(UIElement next, string? state = null)
    {
        if (ReferenceEquals(content, next)) return;
        Children.Clear();
        Children.Add(next);
        content = next;
        if (state is not null && Parent is FrameworkElement) AutomationProperties.SetItemStatus(this, state);
    }

    public void SetNote(string text)
    {
        note.Text = text;
        AutomationProperties.SetName(note, text);
    }
}
