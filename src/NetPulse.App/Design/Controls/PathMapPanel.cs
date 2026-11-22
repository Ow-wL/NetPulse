using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Core.Presentation;
using NetPulse.Design;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>
/// 경로 맵 (M5 설계 4.6, N13). 노드·선은 관측 사실(응답 확인 = 실선, 아니면 점선), 구간 후보만 문제 색 + ✕ + 글자.
/// 흐르는 빛은 응답이 확인된 선에만, 후보가 새로 생기면 3번 깜빡인다 (움직임 줄이기에서 둘 다 없음). 값은 Core <see cref="PathMapModel"/>이 정한다.
/// </summary>
public sealed class PathMapPanel : GroupPanel
{
    private readonly Ui ui;
    private readonly Node pc, local, internet;
    private readonly Link first, second;
    private readonly TextBlock note, legend;
    private MapCandidate shown = MapCandidate.None;

    public PathMapPanel(Ui ui, string automationId) : base(automationId, PathMapView.ConceptNote)
    {
        this.ui = ui;
        var stack = new StackPanel { Spacing = Space.S4 };
        var row = new Grid();
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        pc = new Node(ui, automationId + ".pc", Icons.Pc);
        local = new Node(ui, automationId + ".local", Icons.Router);
        internet = new Node(ui, automationId + ".internet", Icons.Globe);
        first = new Link(ui, automationId + ".link1");
        second = new Link(ui, automationId + ".link2");
        Place(row, pc, 0); Place(row, first, 1); Place(row, local, 2); Place(row, second, 3); Place(row, internet, 4);
        // 선 끝점: 노드 칸이 원보다 넓으면(설명 글이 넓음) 그만큼 선을 칸 안으로 늘여 원 가장자리에 닿게 한다 (M11 C3, 인터넷 노드)
        void Fit()
        {
            var (l1, r1) = PathMapGeometry.LinkMargins(pc.ActualWidth, local.ActualWidth);
            var (l2, r2) = PathMapGeometry.LinkMargins(local.ActualWidth, internet.ActualWidth);
            first.Margin = new Thickness(l1, 0, r1, 0);
            second.Margin = new Thickness(l2, 0, r2, 0);
        }
        pc.SizeChanged += (_, _) => Fit();
        local.SizeChanged += (_, _) => Fit();
        internet.SizeChanged += (_, _) => Fit();
        stack.Children.Add(row);
        note = Fonts.Wrapped("", Typography.Body, ui.Brushes.Ink);
        AutomationProperties.SetAutomationId(note, automationId + ".note");
        note.Visibility = Visibility.Collapsed;
        stack.Children.Add(note);
        legend = Fonts.Wrapped(PathMapView.LegendNote, Typography.Caption, ui.Brushes.Muted);
        stack.Children.Add(legend);
        Children.Add(stack);
    }

    private static void Place(Grid g, FrameworkElement e, int column)
    {
        Grid.SetColumn(e, column);
        g.Children.Add(e);
    }

    public MapCandidate Candidate => shown;

    public void Show(PathMapView view)
    {
        AutomationProperties.SetName(this, view.AccessibleText);
        pc.Set(view.Pc, Brush(view.Pc.State));
        local.Set(view.Local, Brush(view.Local.State));
        internet.Set(view.Internet, Brush(view.Internet.State));
        bool c1 = view.Candidate is MapCandidate.PcToLocal or MapCandidate.PcToLocalAndLocalNode;
        bool c2 = view.Candidate == MapCandidate.LocalToInternet;
        first.Set(view.LocalConfirmed, c1);
        second.Set(view.InternetConfirmed, c2);
        if (view.Candidate == MapCandidate.PcToLocalAndLocalNode) local.Highlight(ui.Brushes.Bad);
        // 후보는 선 위 ✕와 함께 문장으로 (선이 짧아 글자를 선 위에 두지 않는다)
        string? sentence = view.Candidate != MapCandidate.None ? "가능성이 높은 곳: " + PathMapModel.PlaceText(view.Candidate) : view.CandidateNote;
        note.Text = Fonts.KeepAll(sentence ?? "");
        AutomationProperties.SetName(note, sentence ?? "");
        note.Visibility = sentence is null ? Visibility.Collapsed : Visibility.Visible;
        if (view.Candidate != shown && view.Candidate != MapCandidate.None)
        {
            if (c1) ui.Motion.Blink(first.Line, MotionTokens.BlinkTimes);
            if (c2) ui.Motion.Blink(second.Line, MotionTokens.BlinkTimes);
        }
        shown = view.Candidate;
    }

    private Brush Brush(NodeState s) => s switch
    {
        NodeState.Responding => ui.Brushes.Accent,
        NodeState.NotResponding => ui.Brushes.Warn,
        NodeState.LinkDownObserved => ui.Brushes.Bad,
        _ => ui.Brushes.Edge,
    };

    private sealed class Node : StackPanel
    {
        private readonly Ellipse ring;
        private readonly TextBlock name, detail;

        public Node(Ui ui, string id, string icon)
        {
            Spacing = Space.S2;
            HorizontalAlignment = HorizontalAlignment.Center;
            var circle = new Grid { Width = Size.MapNode, Height = Size.MapNode, HorizontalAlignment = HorizontalAlignment.Center };
            ring = new Ellipse { Fill = ui.Brushes.Faint, Stroke = ui.Brushes.Edge, StrokeThickness = Size.FocusOuter };
            circle.Children.Add(ring);
            var i = Icons.Make(icon, ui.Brushes.Ink, Size.IconL);
            i.HorizontalAlignment = HorizontalAlignment.Center;
            i.VerticalAlignment = VerticalAlignment.Center;
            circle.Children.Add(i);
            AutomationProperties.SetAccessibilityView(circle, AccessibilityView.Raw);
            Children.Add(circle);
            name = Fonts.Text("", Typography.Label, ui.Brushes.Ink);
            detail = Fonts.Text("", Typography.Caption, ui.Brushes.Muted);
            name.HorizontalAlignment = detail.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextAlignment = detail.TextAlignment = TextAlignment.Center;
            AutomationProperties.SetAutomationId(name, id + ".name");
            AutomationProperties.SetAutomationId(detail, id + ".detail");
            Children.Add(name);
            Children.Add(detail);
        }

        public void Set(MapNode node, Brush stroke)
        {
            name.Text = node.Name;
            detail.Text = node.Detail;
            ring.Stroke = stroke;
        }

        public void Highlight(Brush stroke) => ring.Stroke = stroke;
    }

    /// <summary>노드 사이 선. 응답 확인은 실선, 아니면 점선·흐리게. 후보면 문제 색 + ✕ + "가능성이 높은 곳".</summary>
    private sealed class Link : Grid
    {
        private readonly Ui ui;
        private readonly Ellipse glow;
        private readonly FrameworkElement cross;
        private readonly TextBlock label;
        private bool sweeping, confirmed;

        public Line Line { get; }

        public Link(Ui ui, string id)
        {
            this.ui = ui;
            VerticalAlignment = VerticalAlignment.Top;
            Height = Size.MapNode;
            Margin = new Thickness(Space.S2, 0, Space.S2, 0);
            Line = new Line { X1 = 0, Y1 = Size.MapNode / 2, Y2 = Size.MapNode / 2, StrokeThickness = Size.MapLine, Stroke = ui.Brushes.Muted };
            Children.Add(Line);
            glow = new Ellipse { Width = Size.MapGlow, Height = Size.MapGlow, Fill = ui.Brushes.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            Children.Add(glow);
            cross = Icons.Make(Icons.Cross, ui.Brushes.Bad, Size.IconM);
            cross.HorizontalAlignment = HorizontalAlignment.Center;
            cross.VerticalAlignment = VerticalAlignment.Center;
            cross.Visibility = Visibility.Collapsed;
            Children.Add(cross);
            label = Fonts.Text("가능성이 높은 곳", Typography.Caption, ui.Brushes.Ink);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Bottom;
            label.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(label, id + ".candidate");
            Children.Add(label);
            AutomationProperties.SetAccessibilityView(Line, AccessibilityView.Raw);
            AutomationProperties.SetAccessibilityView(glow, AccessibilityView.Raw);
            SizeChanged += (_, e) =>
            {
                Line.X2 = e.NewSize.Width;
                StartSweep();
            };
        }

        public void Set(bool isConfirmed, bool candidate)
        {
            confirmed = isConfirmed;
            Line.Stroke = candidate ? ui.Brushes.Bad : confirmed ? ui.Brushes.Accent : ui.Brushes.Muted;
            Line.StrokeDashArray = confirmed || candidate ? null : [ChartStyle.DashOn, ChartStyle.DashOff];
            Line.Opacity = confirmed || candidate ? Emphasis.Full : Emphasis.Unconfirmed;
            cross.Visibility = candidate ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = Visibility.Collapsed;
            glow.Visibility = confirmed && !candidate ? Visibility.Visible : Visibility.Collapsed;
            StartSweep();
        }

        /// <summary>흐르는 빛 (반복형, 움직임 줄이기에서 멈춤). 폭을 처음 알 때 한 번 건다.</summary>
        private void StartSweep()
        {
            if (sweeping || !confirmed || ActualWidth <= 0) return;
            sweeping = true;
            ui.Motion.Sweep(glow, (float)Math.Max(0, ActualWidth - Size.MapGlow), MotionTokens.Slow * 6);
        }
    }
}
