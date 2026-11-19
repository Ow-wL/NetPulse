using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Live;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>준비 중 화면 (M5 설계 4.5): 진단(M6), 기록(M7), 리포트(M8).</summary>
public sealed class PendingScreen : Screen
{
    private readonly PageHeader header;
    private readonly GroupPanel card;

    public PendingScreen(ScreenContext context, string id, string title, string what) : base(context, id)
    {
        RowSpacing = Space.S5;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header = new PageHeader(Ui, id + ".header", null, title);
        Children.Add(header);
        (card, var body) = ScreenParts.Card(Ui, id + ".pending", title, null);
        body.Set(ScreenParts.Message(Ui, id + ".pending.message", Icons.Info, Ui.Brushes.Muted, "이 화면은 다음 단계에서 만듭니다", what), "empty");
        Grid.SetRow(card, 1);
        Children.Add(card);
    }

    public override IReadOnlyList<UIElement> Parts => [header, card];
    public override void Render(HostSnapshot snapshot) { }
}
