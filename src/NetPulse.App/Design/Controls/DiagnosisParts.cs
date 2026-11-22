using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetPulse.Core.Diagnostics;
using NetPulse.Core.Presentation;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design.Controls;

/// <summary>진단 화면 부품의 색·아이콘 (M6 설계 3.6·4.6). 색만으로 구별하지 않도록 아이콘과 글을 함께 쓴다.</summary>
public static class DiagnosisLook
{
    public static (string Icon, Brush Brush) Weight(Ui ui, ConclusionWeight w) => w switch
    {
        ConclusionWeight.Problem => (Icons.Cross, ui.Brushes.Bad),
        ConclusionWeight.Caution => (Icons.Alert, ui.Brushes.Warn),
        ConclusionWeight.Info => (Icons.Info, ui.Brushes.Accent),
        ConclusionWeight.Normal => (Icons.Check, ui.Brushes.Ok),
        _ => (Icons.Question, ui.Brushes.Muted),
    };

    public static (string Icon, Brush Brush) Judgment(Ui ui, CheckJudgment j) => j switch
    {
        CheckJudgment.Normal => (Icons.Check, ui.Brushes.Ok),
        CheckJudgment.Check => (Icons.Alert, ui.Brushes.Warn),
        CheckJudgment.Fail => (Icons.Cross, ui.Brushes.Bad),
        _ => (Icons.Question, ui.Brushes.Muted),
    };
}

/// <summary>
/// 검사 카드 (M6 설계 4.1): 이름, 판정, 한 줄 설명. Enter/Space로 펼치면 대상별 결과(이 PC 화면에서만 주소·도메인)와 실행 상태.
/// </summary>
public sealed class CheckCard : GroupPanel
{
    private readonly Ui ui;
    private readonly RowButton toggle;
    private readonly TextBlock name, judgment, explain;
    private readonly FrameworkElement judgmentIcon;
    private readonly ContentControl iconHost = new();
    private readonly StackPanel details = new() { Spacing = Space.S1, Visibility = Visibility.Collapsed };
    private CheckView? view;

    public bool Expanded { get; private set; }
    public CheckId Id { get; }

    public CheckCard(Ui ui, CheckId id) : base("diagnose.check." + id.ToString().ToLowerInvariant(), QuickDiagnosis.CheckName(id))
    {
        this.ui = ui;
        Id = id;
        name = Fonts.Text(QuickDiagnosis.CheckName(id), Typography.Label, ui.Brushes.Ink);
        judgment = Fonts.Text("", Typography.Label, ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(judgment, AutomationProperties.GetAutomationId(this) + ".judgment");
        explain = Fonts.Wrapped(DiagnosisTexts.What(id), Typography.Caption, ui.Brushes.Muted);
        AutomationProperties.SetAutomationId(explain, AutomationProperties.GetAutomationId(this) + ".explain");
        judgmentIcon = iconHost;
        var head = new Grid { ColumnSpacing = Space.S2 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(name);
        Grid.SetColumn(judgmentIcon, 1);
        head.Children.Add(judgmentIcon);
        Grid.SetColumn(judgment, 2);
        head.Children.Add(judgment);
        var top = new StackPanel { Spacing = Space.S1 };
        top.Children.Add(head);
        top.Children.Add(explain);
        toggle = new RowButton(ui, AutomationProperties.GetAutomationId(this) + ".toggle", QuickDiagnosis.CheckName(id), top) { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        toggle.Click += (_, _) =>
        {
            SetExpanded(!Expanded);
            // 펼친 내용이 화면 아래로 숨지 않게 (배치가 끝난 뒤)
            if (Expanded) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                UpdateLayout();
                StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            });
        };
        AutomationProperties.SetAutomationId(details, AutomationProperties.GetAutomationId(this) + ".details");
        // 머리(RowButton 안 내용)와 같은 들여쓰기 (RowButton은 내용에 좌우 S3 여백을 준다): 상세 줄이 머리보다 왼쪽에서 시작했다 (M11 C3)
        details.Margin = new Thickness(Space.S3, 0, Space.S3, 0);
        var stack = new StackPanel { Spacing = Space.S2 };
        stack.Children.Add(toggle);
        stack.Children.Add(details);
        Children.Add(ui.Glass.Surface(GlassLevel.Content, stack, Radius.Outer, new Thickness(Space.S4)));
        Show(null);
    }

    /// <summary>결과로 채운다. null이면 아직 결과 없음 (뼈대처럼 흐린 상태).</summary>
    public void Show(CheckView? v)
    {
        view = v;
        if (v is null)
        {
            judgment.Text = "";
            iconHost.Content = null;
            explain.Text = Fonts.KeepAll(DiagnosisTexts.What(Id));
            Opacity = Emphasis.Unconfirmed;
            toggle.IsEnabled = false;
            SetExpanded(false);
        }
        else
        {
            var (icon, brush) = DiagnosisLook.Judgment(ui, v.Judgment);
            iconHost.Content = Icons.Make(icon, brush, Size.IconS);
            judgment.Text = v.JudgmentText;
            judgment.Foreground = brush;
            explain.Text = Fonts.KeepAll(v.Explain);
            Opacity = Emphasis.Full;
            toggle.IsEnabled = true;
            BuildDetails(v);
        }
        string accessible = v is null ? QuickDiagnosis.CheckName(Id) + ", 검사 전" : $"{v.Name}, {v.JudgmentText}, {v.Explain}";
        AutomationProperties.SetName(toggle, accessible);
        AutomationProperties.SetName(judgment, judgment.Text);
        AutomationProperties.SetName(explain, explain.Text);
    }

    public void SetExpanded(bool on)
    {
        Expanded = on && view is not null;
        details.Visibility = Expanded ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(this, Expanded ? "expanded" : "collapsed");
    }

    private void BuildDetails(CheckView v)
    {
        details.Children.Clear();
        details.Children.Add(Fonts.Wrapped(DiagnosisTexts.What(v.Id), Typography.Caption, ui.Brushes.Muted));
        foreach (var t in v.Targets)
        {
            string text = $"{t.Label}{(t.Shown is null ? "" : " · " + t.Shown)} · {t.Outcome} · {t.ElapsedMs} ms" + (t.Reason is null ? "" : " · " + t.Reason);
            var line = Fonts.Wrapped(text, Typography.Caption, ui.Brushes.Ink);
            AutomationProperties.SetAutomationId(line, AutomationProperties.GetAutomationId(this) + "." + t.Slot);
            AutomationProperties.SetName(line, text);
            details.Children.Add(line);
        }
        if (v.StateText is { } st) details.Children.Add(Fonts.Text("실행 상태: " + st, Typography.Caption, ui.Brushes.Muted));
    }
}
