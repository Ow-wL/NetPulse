using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Live;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Screens;

/// <summary>
/// 설정 (M5 설계 4.5, 최소): 테마, 연한 오로라, 효과 줄이기, 움직임 줄이기, 측정 구성 열기. 알림·저장 위치는 M9.
/// </summary>
public sealed class SettingsScreen : Screen
{
    private readonly PageHeader header;
    private readonly GroupPanel appearance, measurement;

    public SettingsScreen(ScreenContext context) : base(context, "settings")
    {
        RowSpacing = Space.S5;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        header = new PageHeader(Ui, "settings.header", null, "설정");
        Children.Add(header);

        var s = Ui.Effects.Settings;
        var theme = new SegmentedChoice(Ui, "settings.theme", "테마", [("dark", "다크"), ("light", "라이트"), ("system", "Windows 설정 따르기")],
            s.Theme switch { ThemePreference.Light => "light", ThemePreference.System => "system", _ => "dark" });
        theme.Changed += v =>
        {
            Context.SetRevealOrigin(theme.Item(v));
            Ui.Effects.UpdateSettings(Ui.Effects.Settings with { Theme = v switch { "light" => ThemePreference.Light, "system" => ThemePreference.System, _ => ThemePreference.Dark } }, persist: true);
        };
        var aurora = new SettingToggle(Ui, "settings.aurora", "연한 오로라", s.Aurora);
        aurora.Checked += (_, _) => Update(x => x with { Aurora = true });
        aurora.Unchecked += (_, _) => Update(x => x with { Aurora = false });
        var effects = new SettingToggle(Ui, "settings.reduceEffects", "효과 줄이기 (유리 대신 불투명한 표면)", s.ReduceEffects);
        effects.Checked += (_, _) => Update(x => x with { ReduceEffects = true });
        effects.Unchecked += (_, _) => Update(x => x with { ReduceEffects = false });
        var motion = new SettingToggle(Ui, "settings.reduceMotion", "움직임 줄이기", s.ReduceMotion);
        motion.Checked += (_, _) => Update(x => x with { ReduceMotion = true });
        motion.Unchecked += (_, _) => Update(x => x with { ReduceMotion = false });
        var look = new StackPanel { Spacing = Space.S4 };
        look.Children.Add(theme);
        look.Children.Add(aurora);
        look.Children.Add(effects);
        look.Children.Add(motion);
        (appearance, var appearanceBody) = ScreenParts.Card(Ui, "settings.appearance", "보기", "Windows의 투명 효과·애니메이션 설정도 따릅니다");
        appearanceBody.Set(look);

        var open = new GlassButton(Ui, ButtonKind.Secondary, "settings.profile", "측정 구성 열기", Icons.Pencil);
        open.Click += (_, _) => { ProfileEditor.ReturnTo = "settings"; Context.Navigate("profile", null); };
        var m = new StackPanel { Spacing = Space.S3 };
        m.Children.Add(Fonts.Wrapped("측정 대상, 측정 시간, 예상 연결을 바꿉니다. 알림과 저장 위치 설정은 다음 단계에서 더합니다.", Typography.Body, Ui.Brushes.Muted));
        m.Children.Add(open);
        (measurement, var measurementBody) = ScreenParts.Card(Ui, "settings.measurement", "측정", null);
        measurementBody.Set(m);

        var stack = new StackPanel { Spacing = Space.S5 };
        stack.Children.Add(appearance);
        stack.Children.Add(measurement);
        var scroll = new ScrollViewer { Content = stack, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, Space.S3, Space.S5) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(scroll, "settings.scroll");
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);
    }

    private void Update(Func<AppSettings, AppSettings> change) => Ui.Effects.UpdateSettings(change(Ui.Effects.Settings), persist: true);

    public override IReadOnlyList<UIElement> Parts => [header, appearance, measurement];
    public override void Render(HostSnapshot snapshot) { }
}

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
