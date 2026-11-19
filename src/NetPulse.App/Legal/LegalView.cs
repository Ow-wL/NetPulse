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
    private TextBox? openText;

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
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(row, $"{item.Name} {item.Version}, {item.License}");
            var holder = new Border { Visibility = Visibility.Collapsed };
            row.Click += (_, _) => Toggle(index, textHeight);
            rows.Add((row, holder));
            list.Children.Add(row);
            list.Children.Add(holder);
        }
        Children.Add(list);
    }

    /// <summary>읽은 항목 (읽지 못했으면 null).</summary>
    public IReadOnlyList<LegalTexts.Item>? Items { get; }

    /// <summary>펼친 항목 번호 (없으면 -1).</summary>
    public int Expanded { get; private set; } = -1;

    public event Action? Changed;

    /// <summary>펼친 원문이 처음 배치될 때까지 걸린 시간 (항목 번호, ms) — bench (M10 설계 5절).</summary>
    public event Action<int, double>? Opened;

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
            openText = null;
        }
        Expanded = -1;
        if (open)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var text = new TextBox
            {
                Text = Items[index].Text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = textHeight,
                Foreground = ui.Brushes.Ink,
                Background = ui.Brushes.Bg,
                BorderBrush = ui.Brushes.Edge,
            };
            Fonts.Apply(text, Typography.Caption);
            ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
            AutomationProperties.SetAutomationId(text, $"{prefix}.text.{index}");
            AutomationProperties.SetName(text, Items[index].Name + " 원문");
            text.Loaded += (_, _) => Opened?.Invoke(index, watch.Elapsed.TotalMilliseconds);
            rows[index].Holder.Child = text;
            rows[index].Holder.Visibility = Visibility.Visible;
            AutomationProperties.SetItemStatus(rows[index].Row, "펼침");
            openText = text;
            Expanded = index;
        }
        Changed?.Invoke();
    }

    /// <summary>펼친 글 상자 (시험·초점용).</summary>
    public TextBox? OpenText => openText;
}
