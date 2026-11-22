using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Legal;

/// <summary>
/// 라이선스와 고지 (M10 설계 A3 [1-11]). 고지는 큰 글이라(구성요소 하나가 34만 자) 처음에는 목록(이름·버전·라이선스)만 보이고,
/// 항목 하나를 펼칠 때만 그 원문을 읽기 전용 글 상자에 넣는다 (키보드로 펼치기·스크롤·선택·복사).
/// 외부 브라우저·파일을 열지 않는다. 사용 조건 화면과 설정의 대화상자가 함께 쓴다.
/// </summary>
public sealed class LegalView : Grid
{
    private readonly Ui ui;
    private readonly string prefix;
    private readonly StackPanel list = new() { Spacing = Space.S2 };
    private readonly List<(GlassButton Row, Border Holder)> rows = [];
    private Control? openText;

    public LegalView(Ui ui, string prefix, double textHeight)
    {
        this.ui = ui;
        this.prefix = prefix;
        Items = LegalTexts.Items();
        if (Items is null)
        {
            Children.Add(new Banner(ui, prefix + ".failed", BannerKind.Error, "고지를 읽지 못했습니다. 배포 zip의 THIRD-PARTY-NOTICES.txt를 보세요."));
            return;
        }
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            int index = i;
            var row = new GlassButton(ui, ButtonKind.Secondary, $"{prefix}.item.{i}", $"{item.Name} {item.Version} · {item.License}", Icons.Chevron)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            row.AlignStart();
            AutomationProperties.SetName(row, $"{item.Name} {item.Version}, {item.License}");
            var holder = new Border { Visibility = Visibility.Collapsed };
            row.Click += (_, _) => Toggle(index, textHeight);
            rows.Add((row, holder));
            list.Children.Add(row);
            list.Children.Add(holder);
        }
        Children.Add(list);
        Unloaded += (_, _) => StopFrames();   // 대화상자를 닫으면 프레임 측정 구독을 바로 푼다
    }

    /// <summary>읽은 항목 (읽지 못했으면 null).</summary>
    public IReadOnlyList<LegalTexts.Item>? Items { get; }

    /// <summary>펼친 항목 번호 (없으면 -1).</summary>
    public int Expanded { get; private set; } = -1;

    public event Action? Changed;

    /// <summary>펼친 원문이 처음 배치될 때까지 걸린 시간 (항목 번호, ms) — bench (M10 설계 5절).</summary>
    public event Action<int, double>? Opened;

    /// <summary>펼친 뒤 1초 동안의 프레임 간격 (항목 번호, 가장 긴 간격 ms, 33 ms 초과 횟수, 표본 수) — bench (설계 5절: 50 ms, 33 ms 초과 2회 이하).</summary>
    /// <remarks>마지막 인수는 간격 목록 (ms): 시험이 주사율 계약의 "긴 간격"을 센다 (M12 설계 G2, 33 고정값만 쓰지 않음).</remarks>
    public event Action<int, double, int, int, IReadOnlyList<double>>? Frames;

    /// <summary>첫 항목 단추 (초점 보낼 곳).</summary>
    public Control? FirstFocus => rows.Count > 0 ? rows[0].Row : null;

    /// <summary>항목 하나를 펼치거나 접는다. 한 번에 하나만 펼친다 (큰 글을 여러 개 들고 있지 않게).</summary>
    public void Toggle(int index, double textHeight)
    {
        if (Items is null || index < 0 || index >= rows.Count) return;
        bool open = Expanded != index;
        if (Expanded >= 0)
        {
            rows[Expanded].Holder.Child = null;
            rows[Expanded].Holder.Visibility = Visibility.Collapsed;
            AutomationProperties.SetItemStatus(rows[Expanded].Row, "접힘");
            PointArrow(rows[Expanded].Row, false);
            openText = null;
        }
        Expanded = -1;
        if (open)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            WatchFrames(index, watch);
            string body = Items[index].Text;
            FrameworkElement shown;
            if (body.Length > NetPulse.Design.LegalParagraphs.LongText)
            {
                // 긴 원문은 문단 단위 가상화 목록 (설계 5절: 글 상자 하나에 33만 자를 넣으면 첫 배치가 0.8초 걸렸다, UI 시험 1차)
                var list = ParagraphList(index, body, textHeight);
                list.Loaded += (_, _) => Opened?.Invoke(index, watch.Elapsed.TotalMilliseconds);
                openText = list;
                var copy = new GlassButton(ui, ButtonKind.Secondary, $"{prefix}.copy.{index}", "전문 복사", Icons.Card) { HorizontalAlignment = HorizontalAlignment.Left };
                var copyState = new TextBlock { Foreground = ui.Brushes.Muted, VerticalAlignment = VerticalAlignment.Center };
                Fonts.Apply(copyState, Typography.Caption);
                AutomationProperties.SetAutomationId(copyState, $"{prefix}.copystate.{index}");
                AutomationProperties.SetLiveSetting(copyState, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
                copy.Click += (_, _) => copyState.Text = CopyAll(body) ? "전문을 복사했습니다" : "복사하지 못했습니다";
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.S3 };
                actions.Children.Add(copy);
                actions.Children.Add(copyState);
                var panel = new StackPanel { Spacing = Space.S2 };
                panel.Children.Add(list);
                panel.Children.Add(actions);
                shown = panel;
            }
            else
            {
                // AcceptsReturn을 Text보다 먼저: 한 줄 상자에 넣은 글은 첫 줄바꿈에서 잘린다 (M10 UI 시험: 원문이 첫 줄만 보였다)
                var text = new TextBox
                {
                    AcceptsReturn = true,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Height = textHeight,
                    Foreground = ui.Brushes.Ink,
                    Background = ui.Brushes.Bg,
                    BorderBrush = ui.Brushes.Edge,
                };
                text.Text = body;
                Fonts.Apply(text, Typography.Caption);
                ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
                AutomationProperties.SetAutomationId(text, $"{prefix}.text.{index}");
                AutomationProperties.SetName(text, Items[index].Name + " 원문");
                text.Loaded += (_, _) => Opened?.Invoke(index, watch.Elapsed.TotalMilliseconds);
                openText = text;
                shown = text;
            }
            rows[index].Holder.Child = shown;
            rows[index].Holder.Visibility = Visibility.Visible;
            AutomationProperties.SetItemStatus(rows[index].Row, "펼침");
            PointArrow(rows[index].Row, true);
            Expanded = index;
            BringOpenIntoView(index);
        }
        Changed?.Invoke();
    }

    /// <summary>펼침 화살표: 접힘이면 오른쪽(>), 펼침이면 아래(v). 펼쳐도 방향이 그대로였다 (M11 C3).</summary>
    private static void PointArrow(GlassButton row, bool open)
    {
        if (row.IconElement is not { } icon) return;
        icon.RenderTransform = open ? new Microsoft.UI.Xaml.Media.RotateTransform { Angle = 90, CenterX = Size.IconM / 2, CenterY = Size.IconM / 2 } : null;
    }

    /// <summary>
    /// 펼친 항목(머리 줄부터 원문·[전문 복사]까지)을 보이게 한다. 머리 줄을 위에 맞추므로 다 들어가면 전부 보이고, 넘치면 머리 줄과 원문 앞부분이 보인다.
    /// 그대로 두면 100%에서 [전문 복사] 단추의 위 테두리만 보기 아래 끝에 걸려 "남은 한 줄"처럼 보였다 (M11 C3).
    /// </summary>
    private void BringOpenIntoView(int index)
    {
        var (row, holder) = rows[index];
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (Expanded != index || !row.IsLoaded) return;
            row.UpdateLayout();
            double height = holder.TransformToVisual(row).TransformPoint(default).Y + holder.ActualHeight;
            row.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0,
                TargetRect = new Windows.Foundation.Rect(0, 0, row.ActualWidth, Math.Max(row.ActualHeight, height)),
            });
        });
    }

    /// <summary>펼친 원문 (글 상자 또는 문단 목록).</summary>
    public Control? OpenText => openText;

    private static DataTemplate? paragraphTemplate;

    private ListView ParagraphList(int index, string body, double height)
    {
        paragraphTemplate ??= (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<TextBlock TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\" Margin=\"0,2,0,6\"/></DataTemplate>");
        var paragraphs = NetPulse.Design.LegalParagraphs.Split(body);
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            Height = height,
            Background = ui.Brushes.Bg,
            BorderBrush = ui.Brushes.Edge,
            BorderThickness = new Thickness(Size.Hairline),
            ItemTemplate = paragraphTemplate,
            ItemsSource = paragraphs,
        };
        // 보이는 줄만 만든다 (가상화): 글은 줄이 만들어질 때 넣는다
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.InRecycleQueue || e.ItemContainer.ContentTemplateRoot is not TextBlock t) return;
            if (t.Tag is null) { Fonts.Apply(t, Typography.Caption); t.Foreground = ui.Brushes.Ink; t.Tag = true; }
            t.Text = (string)e.Item;
        };
        AutomationProperties.SetAutomationId(list, $"{prefix}.text.{index}");
        AutomationProperties.SetName(list, $"{Items![index].Name} 원문, 문단 {paragraphs.Count}개");
        return list;
    }

    /// <summary>펼친 순간부터 1초 동안 프레임 간격을 잰다 (첫 간격 = 펼치기부터 첫 프레임까지, 배치 비용이 여기에 든다). 끝나면 구독을 푼다.</summary>
    private void WatchFrames(int index, System.Diagnostics.Stopwatch watch)
    {
        StopFrames();   // 앞 항목의 측정은 버린다 (한 번에 하나)
        double last = 0, max = 0;
        int over = 0, samples = 0;
        var gaps = new List<double>();
        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            double now = watch.Elapsed.TotalMilliseconds;
            double gap = now - last;
            last = now;
            samples++;
            gaps.Add(gap);
            max = Math.Max(max, gap);
            if (gap > NetPulse.Design.Tokens.FrameBudget.Hitch) over++;   // 기존 관측값 (판정은 시험이 주사율 계약으로)
            if (now < 1000) return;
            StopFrames();
            Frames?.Invoke(index, max, over, samples, gaps);
        };
        frameTick = tick;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += tick;
    }

    private EventHandler<object>? frameTick;

    /// <summary>펼친 뒤 프레임 측정 구독이 살아 있는가 (숨긴 뒤 프레임 구독 수에 센다).</summary>
    public bool FramesHooked => frameTick is not null;

    /// <summary>프레임 측정을 그만둔다 (대화상자 닫기·트레이로 숨기기). 결과는 보내지 않는다.</summary>
    public void StopFrames()
    {
        if (frameTick is null) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= frameTick;
        frameTick = null;
    }

    /// <summary>원문 전체를 클립보드에 (사용자가 누를 때만).</summary>
    private static bool CopyAll(string body)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(body);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }
}
