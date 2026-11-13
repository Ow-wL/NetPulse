using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetPulse.Design.Effects;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>
/// 테마와 적용 모드를 화면에 반영한다 (M2 설계 5.1~5.4).
/// 공유 브러시의 색만 바꾸고 요소는 다시 만들지 않는다.
/// 테마가 바뀔 때는 누른 지점에서 새 테마가 원형으로 퍼진다:
///   찍기(직전 화면) → [양보 없이] 찍은 화면을 살아 있는 화면 아래에 넣고, 반경 0 원형 클립, 유리 불투명, 색 바꾸기 → 클립을 키움 → 정리.
/// 정리는 finally에서 반드시 한다. 움직임 줄이기·고대비가 켜지면 진행 중인 전환을 바로 끝낸다.
/// </summary>
public sealed class Theme
{
    private readonly Ui ui;
    private Grid? host;              // [찍은 화면 층, 살아 있는 화면]
    private UIElement? live;
    private readonly Image snapshotLayer = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly RenderTargetBitmap shot = new();   // 하나를 계속 다시 쓴다 (전환마다 새로 만들면 화면 한 장만큼씩 메모리가 늘었다)
    private CancellationTokenSource? transition;
    private EffectiveMode? pending;
    private Compositor? compositor;

    public Theme(Ui ui)
    {
        this.ui = ui;
        AutomationProperties.SetAccessibilityView(snapshotLayer, AccessibilityView.Raw);
    }

    public Palette Palette { get; private set; } = Palette.Dark;

    /// <summary>원형 전환이 퍼져 나갈 점 (살아 있는 화면 좌표). 테마를 바꾼 컨트롤이 정한다. 없으면 가운데.</summary>
    public Windows.Foundation.Point? RevealOrigin { get; set; }

    // ---- 진단 (설계 5.4, 7.3) ----
    public int Transitions { get; private set; }
    public int Fallbacks { get; private set; }
    public int UnclippedNewThemeFrames { get; private set; }
    public bool InTransition => transition is not null;
    public int SnapshotLayersInTree => host?.Children.Count(c => ReferenceEquals(c, snapshotLayer) && snapshotLayer.Visibility == Visibility.Visible) ?? 0;
    public double LastSnapshotMs { get; private set; }

    /// <summary>원형 전환이 시작·끝날 때 (진단 갱신용).</summary>
    public event Action? TransitionChanged;

    /// <summary>시험용: 원형 전환을 이 진행도(0~1)에서 멈춘다 (캡처 매트릭스 37~41).</summary>
    public double? HoldAt { get; set; }

    /// <summary>찍은 화면 층과 살아 있는 화면을 담을 그릇을 만든다. 창의 Content로 쓴다.</summary>
    public Grid Host(UIElement liveContent)
    {
        live = liveContent;
        // 배경 빛·오로라가 창 밖으로 뻗어 있으면 찍은 화면이 창보다 커져, 늘려 깔면 이전 화면이 줄어든 모습으로 보였다 (G5 충실도 시험에서 발견).
        // 그래서 빛 층(Lights.Root)을 창 크기로 자른다. 살아 있는 화면 자체에는 XAML Clip을 걸지 않는다:
        // 같은 요소에 원형 전환의 Composition 클립을 걸면 앱이 멈췄다 (관측).
        host = new Grid();
        host.Children.Add(snapshotLayer);
        host.Children.Add(liveContent);
        host.SizeChanged += (_, _) => transition?.Cancel();   // 창 크기가 바뀌면 전환을 끝으로 건너뛴다
        compositor = ElementCompositionPreview.GetElementVisual(liveContent).Compositor;
        CompositionTarget.Rendering += OnRendering;
        return host;
    }

    /// <summary>적용 모드가 바뀌었을 때. 테마만 바뀌었고 움직임 줄이기·고대비가 아니면 원형으로, 아니면 즉시.</summary>
    public void OnModeChanged(EffectiveMode previous, EffectiveMode next)
    {
        if (transition is not null)
        {
            if (next.ReduceMotion || next.HighContrast) { pending = next; transition.Cancel(); return; }   // 바로 끝내고 최신 모드
            pending = next;
            return;
        }
        bool themeOnly = previous.Theme != next.Theme && previous.Material == next.Material;
        if (themeOnly && !next.ReduceMotion && !next.HighContrast && live is not null) _ = SwitchAsync(next);
        else Apply(next);
    }

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

    private bool clipInstalled;
    private string? appliedDuringTransition;

    private void OnRendering(object? sender, object e)
    {
        // 경계 검사: 새 테마 색이 들어갔는데 클립이 없는 렌더링 콜백 수 (0이어야 한다)
        if (transition is not null && appliedDuringTransition is not null && !clipInstalled) UnclippedNewThemeFrames++;
    }

    private async Task SwitchAsync(EffectiveMode next)
    {
        var cts = new CancellationTokenSource();
        transition = cts;
        Transitions++;
        TransitionChanged?.Invoke();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var visual = ElementCompositionPreview.GetElementVisual(live!);
        CompositionEllipseGeometry? ellipse = null;
        try
        {
            // 1~2. 직전 화면 찍기 (기다리는 동안 아무것도 바꾸지 않는다). 시간 제한을 넘거나 실패하면 페이드로 대체.
            var render = shot.RenderAsync(live).AsTask();
            bool rendered = await Task.WhenAny(render, Task.Delay(MotionTokens.SnapshotLimit)) == render && !render.IsFaulted;
            if (!rendered)
            {
                Fallbacks++;
                ui.Log.Write("themeFallback", new { reason = render.IsFaulted ? "snapshot-failed" : "snapshot-timeout" });
                Apply(next);
                ui.Motion.Fade(live!, 0, 1, MotionTokens.Fast);
                return;
            }
            LastSnapshotMs = started.Elapsed.TotalMilliseconds;

            // 3~5. 양보 없이 이어서: 찍은 화면을 아래에, 반경 0 클립, 유리 불투명, 새 색
            snapshotLayer.Source = shot;
            snapshotLayer.Visibility = Visibility.Visible;
            var size = live!.ActualSize;
            var origin = RevealOrigin ?? new Windows.Foundation.Point(size.X / 2, size.Y / 2);
            ellipse = compositor!.CreateEllipseGeometry();
            ellipse.Center = new Vector2((float)origin.X, (float)origin.Y);
            ellipse.Radius = Vector2.Zero;
            visual.Clip = compositor.CreateGeometricClip(ellipse);
            clipInstalled = true;
            ui.Glass.ForceOpaque(true);
            Apply(next);
            appliedDuringTransition = next.Theme.ToString();

            // 6. 원형 클립을 창 대각선까지 키운다
            float max = (float)Math.Sqrt(Math.Pow(Math.Max(origin.X, size.X - origin.X), 2) + Math.Pow(Math.Max(origin.Y, size.Y - origin.Y), 2));
            var grow = compositor.CreateVector2KeyFrameAnimation();
            grow.InsertKeyFrame(1, new Vector2(max, max), ui.Motion.EnterEasing);
            grow.Duration = MotionTokens.ThemeReveal;
            var done = new TaskCompletionSource();
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            ellipse.StartAnimation("Radius", grow);
            batch.End();
            batch.Completed += (_, _) => done.TrySetResult();
            if (HoldAt is { } p && ellipse.TryGetAnimationController("Radius") is { } controller)
            {
                controller.Pause();
                controller.Progress = (float)p;
                ui.Log.Write("themeHold", new { progress = p });
            }
            using (cts.Token.Register(() => done.TrySetResult())) await done.Task;
            ellipse.StopAnimation("Radius");
        }
        catch (Exception ex)
        {
            // 전환 중 오류가 앱을 멈추지 않게: 기록하고 즉시 적용으로 끝낸다 (G5에서는 대체 1회로 잡혀 실패한다)
            Fallbacks++;
            ui.Log.Write("themeError", new { type = ex.GetType().Name, message = ex.Message, at = ex.StackTrace?.Split(Environment.NewLine).Take(5) });
            Apply(next);
        }
        finally
        {
            // 정리: 클립 해제, 찍은 화면 제거, 유리를 현재 모드대로, 최신 입력 적용
            visual.Clip = null;
            clipInstalled = false;
            appliedDuringTransition = null;
            snapshotLayer.Source = null;
            snapshotLayer.Visibility = Visibility.Collapsed;
            ui.Glass.ForceOpaque(false);
            transition = null;
            cts.Dispose();
            TransitionChanged?.Invoke();
            ui.Log.Write("themeSwitch", new { theme = next.Theme.ToString().ToLowerInvariant(), ms = Math.Round(started.Elapsed.TotalMilliseconds), snapshotMs = Math.Round(LastSnapshotMs, 1) });
            if (pending is { } p2)
            {
                pending = null;
                if (p2 != next) OnModeChanged(next, p2); else Apply(p2);
            }
        }
    }

    /// <summary>창을 닫을 때: 진행 중인 전환을 끝내고 렌더링 구독을 푼다.</summary>
    public void Detach()
    {
        transition?.Cancel();
        CompositionTarget.Rendering -= OnRendering;
    }
}
