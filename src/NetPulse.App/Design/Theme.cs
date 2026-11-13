using Microsoft.UI.Xaml;
using NetPulse.Design.Effects;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>
/// 테마와 적용 모드를 화면에 반영한다 (M2 설계 5.1~5.4).
/// 공유 브러시의 색만 바꾸고 요소는 다시 만들지 않는다. 원형 전환은 C단계에서 여기에 더한다.
/// </summary>
public sealed class Theme
{
    private readonly Ui ui;

    public Theme(Ui ui) => this.ui = ui;

    public Palette Palette { get; private set; } = Palette.Dark;

    /// <summary>적용 모드를 즉시 반영한다.</summary>
    public void Apply(EffectiveMode mode)
    {
        Palette = mode.HighContrast ? ui.Effects.Os.SystemColors.ToPalette() : Palette.For(mode.Theme);
        ui.Brushes.Apply(Palette);
        ui.Glass.Apply(mode.Theme, mode.Material, Palette);
        ui.Motion.SetReduced(mode.ReduceMotion);
        ui.Lights.Apply(mode);
        if (ui.Root is FrameworkElement root)
            root.RequestedTheme = mode.HighContrast ? ElementTheme.Default : mode.Theme == ThemeKind.Dark ? ElementTheme.Dark : ElementTheme.Light;
        var bar = ui.Window.AppWindow.TitleBar;
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = ui.Brushes.Bg.Color;
        bar.ForegroundColor = bar.ButtonForegroundColor = ui.Brushes.Ink.Color;
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = ui.Brushes.Muted.Color;
        bar.ButtonHoverBackgroundColor = ui.Brushes.Faint.Color;
        bar.ButtonHoverForegroundColor = ui.Brushes.Ink.Color;
    }
}
