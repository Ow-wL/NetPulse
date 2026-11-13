using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetPulse.Design.Effects;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>
/// 유리 재질 (M2 설계 4.2). 단계마다 아크릴 브러시를 하나씩 두고 모든 표면이 공유한다.
/// 효과 줄이기·고대비·전환 중에는 같은 브러시의 대체색(불투명 표면)으로 그린다 (AlwaysUseFallback). 요소를 다시 만들지 않는다.
/// </summary>
public sealed class Glass
{
    private readonly Dictionary<GlassLevel, AcrylicBrush> brushes = [];
    private readonly TokenBrushes tokens;
    private readonly Motion motion;
    private readonly List<WeakReference<UIElement>> shadowed = [];
    private MaterialMode mode = MaterialMode.Acrylic;
    private bool forceOpaque;
    private ThemeKind theme = ThemeKind.Dark;
    private Palette palette = Palette.Dark;

    public Glass(TokenBrushes tokens, Motion motion)
    {
        this.tokens = tokens;
        this.motion = motion;
        foreach (var level in Enum.GetValues<GlassLevel>())
            brushes[level] = new AcrylicBrush { TintTransitionDuration = TimeSpan.Zero };
    }

    public Brush Brush(GlassLevel level) => brushes[level];

    /// <summary>지금 아크릴로 그리는지 (진단용).</summary>
    public bool IsAcrylic => mode == MaterialMode.Acrylic && !forceOpaque;

    public void Apply(ThemeKind theme, MaterialMode material, Palette palette)
    {
        mode = material;
        this.theme = theme;
        this.palette = palette;
        foreach (var (level, b) in brushes)
        {
            var m = Materials.Glass(theme, level);
            b.TintColor = TokenBrushes.ToColor(m.Tint);
            b.TintOpacity = m.TintOpacity;
            b.TintLuminosityOpacity = m.TintOpacity;
        }
        UpdateFallback();
        bool shadows = material != MaterialMode.HighContrast;
        foreach (var weak in shadowed)
            if (weak.TryGetTarget(out var el)) el.Shadow = shadows ? new ThemeShadow() : null;
    }

    /// <summary>원형 전환 동안 잠시 불투명 표면으로 (M2 설계 5.4의 4, 아크릴 회색 대책).</summary>
    public void ForceOpaque(bool value)
    {
        forceOpaque = value;
        UpdateFallback();
    }

    private void UpdateFallback()
    {
        foreach (var (level, b) in brushes)
        {
            // 전환 중 대체색은 아크릴처럼 보이게 바탕 위 틴트 (효과 줄이기의 최악 배경 기준 색과 다르다: 그 색은 라이트에서 누렇게 보였다)
            var m = Materials.Glass(theme, level);
            b.FallbackColor = TokenBrushes.ToColor(
                mode == MaterialMode.HighContrast ? palette.Bg
                : forceOpaque && mode == MaterialMode.Acrylic ? m.TintWithAlpha.Over(palette.Bg)
                : Materials.OpaqueSurface(theme, level));
            b.AlwaysUseFallback = forceOpaque || mode != MaterialMode.Acrylic;
        }
    }

    /// <summary>
    /// 유리의 반응: 포인터를 올리면 하이라이트가 포인터를 따라간다 (기획서 7.4). 움직임 줄이기·고대비면 보이지 않는다.
    /// 애니메이션이 아니라 포인터 위치를 그대로 옮기는 것이다.
    /// </summary>
    private void AddPointerGlow(Grid host, double radius)
    {
        var glow = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, RadiusX = Size.ControlL * 4, RadiusY = Size.ControlL * 4 };
        glow.GradientStops.Add(new GradientStop { Offset = 0, Color = TokenBrushes.ToColor(NetPulse.Design.Rgba.White(Emphasis.PointerGlow)) });
        glow.GradientStops.Add(new GradientStop { Offset = 1, Color = TokenBrushes.ToColor(NetPulse.Design.Rgba.White(0)) });
        var layer = new Border { Background = glow, CornerRadius = new CornerRadius(radius), IsHitTestVisible = false, Opacity = 0 };
        host.Children.Add(layer);
        host.PointerMoved += (_, e) =>
        {
            if (motion.Reduced || mode == MaterialMode.HighContrast) { layer.Opacity = 0; return; }
            var p = e.GetCurrentPoint(host).Position;
            glow.Center = p;
            glow.GradientOrigin = p;
            layer.Opacity = Emphasis.Full;
        };
        host.PointerExited += (_, _) => layer.Opacity = 0;
    }

    /// <summary>
    /// 유리 표면 하나: 바탕(재질) + 1 px 테두리 + 위쪽 하이라이트 + 그림자.
    /// 안쪽 반경은 동심 규칙(바깥 − 여백)으로 자식이 맞춘다.
    /// </summary>
    public Grid Surface(GlassLevel level, UIElement child, double radius, Thickness padding)
    {
        var body = new Border
        {
            Background = brushes[level],
            BorderBrush = tokens.Edge,
            BorderThickness = new Thickness(Size.Hairline),
            CornerRadius = new CornerRadius(radius),
            Padding = padding,
            Child = child,
        };
        var highlight = new Border
        {
            BorderBrush = tokens.Highlight,
            BorderThickness = new Thickness(0, Size.Hairline, 0, 0),
            CornerRadius = new CornerRadius(radius),
            IsHitTestVisible = false,
        };
        var host = new Grid();
        host.Children.Add(body);
        host.Children.Add(highlight);
        if (level is GlassLevel.Content or GlassLevel.Control) AddPointerGlow(host, radius);
        // 그림자는 둥근 표면 자체에 건다 (바깥 사각 틀에 걸면 모서리가 각진 그림자가 보인다)
        double depth = Materials.Glass(ThemeKind.Dark, level).ShadowDepth;
        body.Translation = new Vector3(0, 0, (float)depth);
        body.Shadow = mode == MaterialMode.HighContrast ? null : new ThemeShadow();
        shadowed.Add(new WeakReference<UIElement>(body));
        return host;
    }
}
