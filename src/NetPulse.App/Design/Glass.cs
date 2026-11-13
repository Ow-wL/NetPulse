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
    private readonly List<WeakReference<UIElement>> shadowed = [];
    private MaterialMode mode = MaterialMode.Acrylic;
    private bool forceOpaque;

    public Glass(TokenBrushes tokens)
    {
        this.tokens = tokens;
        foreach (var level in Enum.GetValues<GlassLevel>())
            brushes[level] = new AcrylicBrush { TintTransitionDuration = TimeSpan.Zero };
    }

    public Brush Brush(GlassLevel level) => brushes[level];

    /// <summary>지금 아크릴로 그리는지 (진단용).</summary>
    public bool IsAcrylic => mode == MaterialMode.Acrylic && !forceOpaque;

    public void Apply(ThemeKind theme, MaterialMode material, Palette palette)
    {
        mode = material;
        foreach (var (level, b) in brushes)
        {
            var m = Materials.Glass(theme, level);
            b.TintColor = TokenBrushes.ToColor(m.Tint);
            b.TintOpacity = m.TintOpacity;
            b.TintLuminosityOpacity = m.TintOpacity;
            b.FallbackColor = TokenBrushes.ToColor(material == MaterialMode.HighContrast ? palette.Bg : Materials.OpaqueSurface(theme, level));
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
        foreach (var b in brushes.Values) b.AlwaysUseFallback = forceOpaque || mode != MaterialMode.Acrylic;
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
        // 그림자는 둥근 표면 자체에 건다 (바깥 사각 틀에 걸면 모서리가 각진 그림자가 보인다)
        double depth = Materials.Glass(ThemeKind.Dark, level).ShadowDepth;
        body.Translation = new Vector3(0, 0, (float)depth);
        body.Shadow = mode == MaterialMode.HighContrast ? null : new ThemeShadow();
        shadowed.Add(new WeakReference<UIElement>(body));
        return host;
    }
}
