using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Design.Effects;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>
/// 배경 빛과 연한 오로라 (M2 설계 5.5). 유리 뒤에 오는 유일한 그림이다 (대비 계산 4.3의 배경 층).
/// - 빛 원 4개: 가장자리가 투명한 원형 그라디언트. 정상 색과 끊김 색 두 겹을 겹쳐 두고 투명도로 바꾼다 (요소를 다시 만들지 않음).
/// - 오로라 띠 3개: 켜고 끌 때 투명도만 바꾼다.
/// - 흐름은 Composition 반복 애니메이션 (모션 도구에 등록, 움직임 줄이기면 현재 자리에서 멈춤).
/// </summary>
public sealed class Lights
{
    private sealed record Light(Ellipse Normal, Ellipse Problem, GradientStop[] NormalStops, GradientStop[] ProblemStops, LightPlacement Place, int Index);
    private sealed record Ribbon(Rectangle Shape, GradientStop[] Stops, RibbonPlacement Place, int Index);

    private readonly Motion motion;
    private readonly Canvas canvas = new() { IsHitTestVisible = false };
    private readonly Canvas aurora = new() { IsHitTestVisible = false };
    private readonly List<Light> lights = [];
    private readonly List<Ribbon> ribbons = [];
    private readonly List<MotionHandle> drift = [];
    private bool problem, auroraOn, moving;
    private ThemeKind theme = ThemeKind.Dark;

    public Lights(Motion motion)
    {
        this.motion = motion;
        Root = new Grid { IsHitTestVisible = false };
        Root.Children.Add(canvas);
        Root.Children.Add(aurora);
        for (int i = 0; i < LightLayout.Lights.Count; i++)
        {
            var (normal, ns) = Blob();
            var (bad, ps) = Blob();
            canvas.Children.Add(normal);
            canvas.Children.Add(bad);
            lights.Add(new Light(normal, bad, ns, ps, LightLayout.Lights[i], i));
        }
        for (int i = 0; i < LightLayout.Ribbons.Count; i++)
        {
            var shape = new Rectangle();
            aurora.Children.Add(shape);
            ribbons.Add(new Ribbon(shape, [], LightLayout.Ribbons[i], i));
        }
        Root.SizeChanged += (_, e) =>
        {
            Root.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };   // 창 밖으로 뻗은 빛은 그리지 않는다
            Layout();
        };
        motion.ReduceChanged += _ => RestartDrift();
    }

    public Grid Root { get; }

    /// <summary>진단용</summary>
    public bool Problem => problem;
    public bool AuroraOn => auroraOn;

    public void Apply(EffectiveMode mode)
    {
        theme = mode.Theme;
        Root.Visibility = mode.LightsVisible ? Visibility.Visible : Visibility.Collapsed;
        // 그라디언트 정지점의 색만 바꾸면 이미 그려진 원형 그라디언트에 반영되지 않았다 (관측: 테마를 바꿔도 이전 테마의 빛이 남음).
        // 테마가 바뀌면 브러시를 새로 만들어 끼운다 (요소는 그대로, 8+3개라 비용이 작다).
        foreach (var l in lights)
        {
            l.Normal.Fill = Radial(Materials.Lights(theme, false)[l.Index].Color);
            l.Problem.Fill = Radial(Materials.Lights(theme, true)[l.Index].Color);
        }
        var colors = Materials.Aurora(theme);
        foreach (var r in ribbons) r.Shape.Fill = RibbonBrush(colors[r.Index].Color);
        SetState(problem, animate: false);
        SetAurora(mode.AuroraVisible, animate: false);
        if (mode.LightsMoving != moving) { moving = mode.LightsMoving; RestartDrift(); }
    }

    /// <summary>끊김 상태: 빛이 끊김 색으로 바뀐다 (1.2초, 줄이기면 바로).</summary>
    public void SetState(bool isProblem, bool animate = true)
    {
        problem = isProblem;
        foreach (var l in lights)
        {
            float normalTo = isProblem ? 0 : (float)Materials.Lights(theme, false)[l.Index].MaxOpacity;
            float badTo = isProblem ? (float)Materials.Lights(theme, true)[l.Index].MaxOpacity : 0;
            FadeTo(l.Normal, normalTo, animate ? MotionTokens.StateShift : TimeSpan.Zero);
            FadeTo(l.Problem, badTo, animate ? MotionTokens.StateShift : TimeSpan.Zero);
        }
    }

    public void SetAurora(bool on, bool animate = true)
    {
        auroraOn = on;
        foreach (var r in ribbons)
            FadeTo(r.Shape, on ? (float)Materials.Aurora(theme)[r.Index].MaxOpacity : 0, animate ? MotionTokens.Slow : TimeSpan.Zero);
    }

    private void FadeTo(UIElement element, float to, TimeSpan duration)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (duration == TimeSpan.Zero) { visual.StopAnimation("Opacity"); visual.Opacity = to; return; }
        var a = motion.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, to, motion.EnterEasing);
        a.Duration = duration;
        motion.RunOnce(Root, visual, "Opacity", a, () => visual.Opacity = to);
    }

    private static (Ellipse, GradientStop[]) Blob() => (new Ellipse(), []);

    private static RadialGradientBrush Radial(NetPulse.Design.Rgba color)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = TokenBrushes.ToColor(color) });
        brush.GradientStops.Add(new GradientStop { Offset = LightLayout.FalloffStart, Color = TokenBrushes.ToColor(color) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = TokenBrushes.ToColor(color.WithAlpha(0)) });
        return brush;
    }

    private static LinearGradientBrush RibbonBrush(NetPulse.Design.Rgba color)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = TokenBrushes.ToColor(color.WithAlpha(0)) });
        brush.GradientStops.Add(new GradientStop { Offset = LightLayout.RibbonCenter, Color = TokenBrushes.ToColor(color) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = TokenBrushes.ToColor(color.WithAlpha(0)) });
        return brush;
    }

    private void Layout()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight, m = Math.Max(w, h);
        if (w <= 0) return;
        foreach (var l in lights)
            foreach (var e in new[] { l.Normal, l.Problem })
            {
                e.Width = e.Height = l.Place.Size * m;
                Canvas.SetLeft(e, l.Place.Left * w);
                Canvas.SetTop(e, l.Place.Top * h);
            }
        foreach (var r in ribbons)
        {
            r.Shape.Width = r.Place.Width * w;
            r.Shape.Height = r.Place.Height * h;
            Canvas.SetLeft(r.Shape, r.Place.Left * w);
            Canvas.SetTop(r.Shape, r.Place.Top * h);
            r.Shape.RenderTransform = new RotateTransform { Angle = r.Place.Angle };
        }
        RestartDrift();
    }

    /// <summary>빛의 느린 흐름. 줄이기·정지·고정이면 현재 자리에서 멈춘다.</summary>
    private void RestartDrift()
    {
        foreach (var old in drift) motion.Stop(old);
        drift.Clear();
        double w = Root.ActualWidth, h = Root.ActualHeight, m = Math.Max(w, h);
        foreach (var l in lights)
            foreach (var e in new[] { l.Normal, l.Problem })
            {
                ElementCompositionPreview.SetIsTranslationEnabled(e, true);
                var v = ElementCompositionPreview.GetElementVisual(e);
                v.StopAnimation("Translation");
                v.StopAnimation("Scale");
                if (!moving || m <= 0) continue;
                var a = motion.Compositor.CreateVector3KeyFrameAnimation();
                a.InsertExpressionKeyFrame(0, "this.StartingValue");   // 멈췄던 자리에서 이어서 (화면 찍기 뒤 다시 시작해도 튀지 않게)
                a.InsertKeyFrame(1, new Vector3((float)(l.Place.DriftX * m), (float)(l.Place.DriftY * m), 0), motion.MoveEasing);
                a.Duration = MotionTokens.LightDrift[l.Index % MotionTokens.LightDrift.Length];
                a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                a.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
                if (motion.RunRepeat(e, v, "Translation", a, () => { }) is { } h1) drift.Add(h1);
            }
        foreach (var r in ribbons)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(r.Shape, true);
            var v = ElementCompositionPreview.GetElementVisual(r.Shape);
            v.StopAnimation("Translation");
            if (!moving || m <= 0) continue;
            var a = motion.Compositor.CreateVector3KeyFrameAnimation();
            a.InsertExpressionKeyFrame(0, "this.StartingValue");
            a.InsertKeyFrame(1, new Vector3((float)(r.Place.DriftX * w), 0, 0), motion.MoveEasing);
            a.Duration = MotionTokens.LightDrift[(r.Index + 1) % MotionTokens.LightDrift.Length];
            a.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            a.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
            if (motion.RunRepeat(r.Shape, v, "Translation", a, () => { }) is { } h2) drift.Add(h2);
        }
    }
}
