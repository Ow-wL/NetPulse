using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NetPulse.Design.Effects;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>
/// 배경 빛과 연한 오로라 (M2 설계 5.5). 유리 뒤에 오는 유일한 그림이다 (대비 계산 4.3의 배경 층).
/// - 빛 원 4개: 가장자리가 투명한 원형 그라디언트. 정상 색과 끊김 색 두 겹을 겹쳐 두고 투명도로 바꾼다 (요소를 다시 만들지 않음).
/// - 오로라 띠 3개: 켜고 끌 때 투명도만 바꾼다.
/// - 흐름은 Composition 반복 애니메이션 (모션 도구에 등록, 움직임 줄이기면 현재 자리에서 멈춤).
/// - 흑요석 미세 결 (M12 설계 E): 맨 위의 고정 점 격자 이미지 한 장. 투명 효과를 쓸 때만 보인다. 크기·배율이 바뀌면 잠시 뒤 다시 만든다
///   (덮임 계산은 백그라운드, 색 입히기·비트맵은 UI 스레드). 테마가 바뀌면 같은 덮임에 색만 다시 입힌다.
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
    // 테마·상태별 브러시를 미리 만들어 두고 바꿔 끼우기만 한다 (전환 순간에 새로 만들면 프레임이 늦어졌다, 벤치 관측)
    private readonly Dictionary<(ThemeKind, bool, int), Brush> lightBrushes = [];
    private readonly Dictionary<(ThemeKind, int), Brush> ribbonBrushes = [];
    private bool problem, auroraOn, moving;
    private ThemeKind theme = ThemeKind.Dark;
    private readonly Image grain = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private readonly Microsoft.UI.Dispatching.DispatcherQueue queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? grainTimer;
    private (int W, int H, double Scale) grainSize;
    private ThemeKind? grainTheme;
    private byte[]? grainMask;
    private int grainBuild;
    private bool grainOn;

    public Lights(Motion motion)
    {
        this.motion = motion;
        Root = new Grid { IsHitTestVisible = false };
        Root.Children.Add(canvas);
        Root.Children.Add(aurora);
        Root.Children.Add(grain);
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
            ScheduleGrain();
        };
        Root.Loaded += (_, _) => { if (Root.XamlRoot is { } xr) xr.Changed += (_, _) => ScheduleGrain(); ScheduleGrain(); };
        motion.ReduceChanged += _ => RestartDrift();
    }

    public Grid Root { get; }

    /// <summary>진단용</summary>
    public bool Problem => problem;
    public bool AuroraOn => auroraOn;
    /// <summary>진단용: 결 층이 켜졌고 이미지가 지금 크기·배율·테마로 만들어졌는지 (M12 설계 E)</summary>
    public bool GrainOn => grainOn;
    public bool GrainReady => grain.Source is not null && grainTheme == theme && grainMask is not null;

    public void Apply(EffectiveMode mode)
    {
        theme = mode.Theme;
        Root.Visibility = mode.LightsVisible ? Visibility.Visible : Visibility.Collapsed;
        // 결은 투명 효과를 쓸 때만 (불투명 표면·고대비에서는 끈다, M12 설계 E)
        grainOn = mode.Material == NetPulse.Design.Effects.MaterialMode.Acrylic;
        grain.Visibility = grainOn ? Visibility.Visible : Visibility.Collapsed;
        if (grainOn) ScheduleGrain();
        // 그라디언트 정지점의 색만 바꾸면 이미 그려진 원형 그라디언트에 반영되지 않았다 (관측: 테마를 바꿔도 이전 테마의 빛이 남음).
        // 테마가 바뀌면 브러시를 새로 만들어 끼운다 (요소는 그대로, 8+3개라 비용이 작다).
        foreach (var l in lights)
        {
            l.Normal.Fill = LightBrush(theme, false, l.Index);
            l.Problem.Fill = LightBrush(theme, true, l.Index);
        }
        foreach (var r in ribbons) r.Shape.Fill = AuroraBrush(theme, r.Index);
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

    private Brush LightBrush(ThemeKind t, bool bad, int index)
    {
        if (!lightBrushes.TryGetValue((t, bad, index), out var b))
            lightBrushes[(t, bad, index)] = b = Radial(Materials.Lights(t, bad)[index].Color);
        return b;
    }

    private Brush AuroraBrush(ThemeKind t, int index)
    {
        if (!ribbonBrushes.TryGetValue((t, index), out var b))
            ribbonBrushes[(t, index)] = b = RibbonBrush(Materials.Aurora(t)[index].Color);
        return b;
    }

    /// <summary>두 테마의 브러시를 미리 만든다 (첫 전환에서도 새로 만들지 않게).</summary>
    public void Prepare()
    {
        foreach (var t in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            foreach (var l in lights) { LightBrush(t, false, l.Index); LightBrush(t, true, l.Index); }
            foreach (var r in ribbons) AuroraBrush(t, r.Index);
        }
    }

    /// <summary>크기·배율 변경이 잦을 때(창 크기 조절) 한 번만 만들도록 잠시 기다린다.</summary>
    private void ScheduleGrain()
    {
        if (!grainOn) return;
        grainTimer ??= CreateGrainTimer();
        grainTimer.Stop();
        grainTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateGrainTimer()
    {
        var t = queue.CreateTimer();
        t.Interval = MotionTokens.Slow;
        t.IsRepeating = false;
        t.Tick += (_, _) => BuildGrain();
        return t;
    }

    private void BuildGrain()
    {
        if (!grainOn || Root.XamlRoot is not { } xr || Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return;
        double w = Root.ActualWidth, h = Root.ActualHeight;
        var size = NetPulse.Design.GrainMask.Size(w, h, xr.RasterizationScale);
        if (size.Width <= 0) return;
        var forTheme = theme;
        if (size == grainSize && grainMask is not null)
        {
            if (grainTheme != forTheme) Paint(grainMask, size, forTheme, w, h);
            return;
        }
        int build = ++grainBuild;
        _ = Task.Run(() => NetPulse.Design.GrainMask.Build(size.Width, size.Height, size.Scale)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            queue.TryEnqueue(() =>
            {
                if (build != grainBuild) return;   // 그 사이 크기가 또 바뀌었다
                grainMask = t.Result;
                grainSize = size;
                Paint(t.Result, size, theme, w, h);
            });
        }, TaskScheduler.Default);
    }

    private void Paint(byte[] mask, (int Width, int Height, double Scale) size, ThemeKind forTheme, double w, double h)
    {
        var bitmap = new WriteableBitmap(size.Width, size.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(NetPulse.Design.GrainMask.ToBgra(mask, forTheme));
        bitmap.Invalidate();
        grain.Source = bitmap;
        grain.Width = w;
        grain.Height = h;
        grainTheme = forTheme;
    }

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
