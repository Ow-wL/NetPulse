using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

public enum MotionKind { Move, Scale, Repeat, Fade }

/// <summary>실행 중인 모션 하나. 멈추면 최종(또는 쉬는) 상태로 맞춘다.</summary>
public sealed class MotionHandle
{
    private readonly Action stop;
    internal MotionHandle(MotionKind kind, Action stop) { Kind = kind; this.stop = stop; }
    public MotionKind Kind { get; }
    internal void Stop() => stop();
}

/// <summary>
/// 모든 애니메이션의 유일한 진입점 (M2 설계 5.3, 4.5). 화면 코드는 이 클래스만 쓴다 (정적 검사).
/// - 모든 모션은 핸들로 등록되고, 소유 요소가 화면에서 떨어지거나 끝나면 해제된다.
/// - 움직임 줄이기를 켜는 순간 활성 핸들을 모두 멈추고 최종 상태로 맞춘다. 이후 이동·크기·반복은 만들지 않고 짧은 페이드만.
/// - Composition 애니메이션을 쓴다 (UI 스레드가 바빠도 부드럽게).
/// </summary>
public sealed class Motion
{
    private readonly Compositor compositor;
    private readonly HashSet<MotionHandle> active = [];
    private readonly Dictionary<MotionKind, int> created = Enum.GetValues<MotionKind>().ToDictionary(k => k, _ => 0);
    private readonly CompositionEasingFunction enter, move;
    private readonly LinearEasingFunction linear;

    public Motion(Compositor compositor)
    {
        this.compositor = compositor;
        enter = Ease(MotionTokens.Enter);
        move = Ease(MotionTokens.Move);
        linear = compositor.CreateLinearEasingFunction();
    }

    public bool Reduced { get; private set; }

    /// <summary>시험용: 반복 모션을 시작하지 않는다 (같은 화면을 매번 같은 모습으로 찍기 위해). 진단에 남는다.</summary>
    public bool Frozen { get; set; }

    /// <summary>시험·캡처: 한 번 도는 모션을 이 진행도(0~1)에서 멈춘다 (화면 전환 장면, M5).</summary>
    public double? HoldAt { get; set; }

    public event Action? ActiveChanged;

    public IReadOnlyDictionary<MotionKind, int> ActiveByKind =>
        Enum.GetValues<MotionKind>().ToDictionary(k => k, k => active.Count(h => h.Kind == k));

    public IReadOnlyDictionary<MotionKind, int> CreatedByKind => created;

    /// <summary>움직임 줄이기 켜고 끄기. 켜는 순간 활성 모션을 모두 멈춘다. 끄면 반복형은 소유자가 다시 시작한다 (ReduceChanged).</summary>
    public void SetReduced(bool value)
    {
        if (value == Reduced) return;
        Reduced = value;
        if (value)
            foreach (var h in active.ToArray()) End(h);
        ReduceChanged?.Invoke(value);
    }

    /// <summary>움직임 줄이기가 바뀐 뒤 (배경 빛·펄스 같은 반복형 소유자가 다시 시작하는 데 쓴다).</summary>
    public event Action<bool>? ReduceChanged;

    // ---------- 등장, 퇴장, 페이드 ----------

    /// <summary>요소 등장: 아래에서 살짝 올라오며 나타남. 줄이기면 짧은 페이드.</summary>
    public void Enter(UIElement element, int order = 0)
    {
        var visual = Prepare(element);
        var delay = MotionTokens.StaggerStep * order;
        if (Reduced)
        {
            Fade(element, 0, 1);
            return;
        }
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, enter);
        opacity.Duration = MotionTokens.Slow;
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, (float)MotionTokens.RiseDistance, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, enter);
        rise.Duration = MotionTokens.Slow;
        rise.DelayTime = delay;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        Run(element, MotionKind.Move, visual, ("Opacity", opacity), ("Translation", rise), () =>
        {
            visual.Opacity = (float)Emphasis.Full;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        });
    }

    /// <summary>여러 요소가 차례로 등장.</summary>
    public void Stagger(IEnumerable<UIElement> elements)
    {
        int i = 0;
        foreach (var e in elements) Enter(e, i++);
    }

    /// <summary>투명도 변화. 줄이기여도 쓴다 (83 ms).</summary>
    public void Fade(UIElement element, float from, float to, TimeSpan? duration = null)
    {
        var visual = Prepare(element);
        var a = compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, linear);
        a.Duration = Reduced ? MotionTokens.Fade : duration ?? MotionTokens.Fade;
        Run(element, MotionKind.Fade, visual, ("Opacity", a), null, () => visual.Opacity = to);
    }

    /// <summary>
    /// 교차 페이드 (M2 설계 4.5가 약속한 화면 전환, M5에서 만듦): 나가는 요소를 짧게 흐리고, 들어오는 요소들을 차례로 등장시킨다.
    /// 줄이기면 들어오는 쪽도 짧은 페이드만 (Enter가 보장).
    /// </summary>
    public void CrossFade(UIElement? outgoing, IReadOnlyList<UIElement> incoming)
    {
        if (outgoing is not null) Fade(outgoing, (float)Emphasis.Full, 0, MotionTokens.Fade);
        Stagger(incoming);
    }

    /// <summary>요소를 지정 위치(Translation)로 옮김. 줄이기면 바로 옮김.</summary>
    public void MoveTo(UIElement element, Vector3 to, TimeSpan? duration = null)
    {
        var visual = Prepare(element);
        if (Reduced)
        {
            visual.Properties.InsertVector3("Translation", to);
            return;
        }
        var a = compositor.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1, to, move);
        a.Duration = duration ?? MotionTokens.Mid;
        Run(element, MotionKind.Move, visual, ("Translation", a), null, () => visual.Properties.InsertVector3("Translation", to));
    }

    /// <summary>애니메이션 없이 바로 그 위치에 둔다 (첫 배치). 같은 요소의 다른 이동과 같은 속성을 쓴다.</summary>
    public void Place(UIElement element, Vector3 at) => Prepare(element).Properties.InsertVector3("Translation", at);

    // ---------- 누름, 반복 ----------

    /// <summary>누르면 살짝 가라앉음 (유리의 반응). 줄이기면 없음.</summary>
    public void AttachPress(ButtonBase button)
    {
        button.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, (_, _) =>
        {
            if (Reduced) return;
            var visual = Prepare(button);
            visual.CenterPoint = new Vector3((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
            float target = button.IsPressed ? (float)MotionTokens.PressScale : 1f;
            var a = compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1, new Vector3(target, target, 1), enter);
            a.Duration = MotionTokens.Fast;
            Run(button, MotionKind.Scale, visual, ("Scale", a), null, () => visual.Scale = Vector3.One);
        });
    }

    /// <summary>
    /// 투명도가 끝없이 오가는 반복 (불러오는 중 윤곽, 상태 점). 줄이기·고정이면 시작하지 않고 쉬는 값으로 둔다.
    /// 줄이기를 끄면 다시 시작한다.
    /// </summary>
    public void Breathe(UIElement element, float low, float high, TimeSpan period)
    {
        void Start()
        {
            var visual = Prepare(element);
            if (Reduced || Frozen) { visual.Opacity = high; return; }
            var a = compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0, low);
            a.InsertKeyFrame(0.5f, high, linear);
            a.InsertKeyFrame(1, low, linear);
            a.Duration = period;
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            Run(element, MotionKind.Repeat, visual, ("Opacity", a), null, () => visual.Opacity = high);
        }
        Start();
        Action<bool> restart = reduced => { if (!reduced) Start(); };
        ReduceChanged += restart;
        element.RegisterUnloaded(() => ReduceChanged -= restart);
    }

    /// <summary>값이 오가는 반복을 직접 정의 (배경 빛 흐름 등, Lights가 씀). 등록·정리·줄이기 규칙은 같다.</summary>
    internal MotionHandle? RunRepeat(UIElement owner, Visual visual, string property, CompositionAnimation animation, Action rest)
    {
        if (Reduced || Frozen) { rest(); return null; }
        return Run(owner, MotionKind.Repeat, visual, (property, animation), null, rest);
    }

    /// <summary>모션 하나를 멈추고 최종(쉬는) 상태로 맞춘다.</summary>
    public void Stop(MotionHandle? handle) => handle?.Stop();

    /// <summary>
    /// 가로지르기 반복 (펄스의 빛이 화면을 가로지름). 줄이기·고정이면 시작하지 않는다. 줄이기를 끄면 다시 시작.
    /// </summary>
    public void Sweep(UIElement element, float distance, TimeSpan period)
    {
        void Start()
        {
            var visual = Prepare(element);
            if (Reduced || Frozen) { visual.Opacity = 0; return; }
            visual.Opacity = (float)Emphasis.Full;
            var a = compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0, Vector3.Zero);
            a.InsertKeyFrame(1, new Vector3(distance, 0, 0), enter);
            a.Duration = period;
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            Run(element, MotionKind.Repeat, visual, ("Translation", a), null, () => { visual.Properties.InsertVector3("Translation", Vector3.Zero); visual.Opacity = 0; });
        }
        Start();
        Action<bool> restart = reduced => { if (!reduced) Start(); };
        ReduceChanged += restart;
        element.RegisterUnloaded(() => ReduceChanged -= restart);
    }

    /// <summary>깜빡임 (끊긴 구간 강조). 정해진 횟수만 돌고 끝난다. 줄이기면 없음.</summary>
    public void Blink(UIElement element, int times)
    {
        if (Reduced) return;
        var visual = Prepare(element);
        var a = compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, (float)Emphasis.Full);
        a.InsertKeyFrame(0.5f, (float)Emphasis.Disabled, linear);
        a.InsertKeyFrame(1, (float)Emphasis.Full, linear);
        a.Duration = MotionTokens.Slow * 2;
        a.IterationBehavior = AnimationIterationBehavior.Count;
        a.IterationCount = times;
        Run(element, MotionKind.Repeat, visual, ("Opacity", a), null, () => visual.Opacity = (float)Emphasis.Full, endless: false);
    }

    /// <summary>
    /// 공통 요소 이동 (화면 전환): 대상 요소가 출발 요소의 자리·크기에서 자기 자리로 커지며 옮겨 온다. 줄이기면 교차 페이드만.
    /// </summary>
    public void SharedMove(FrameworkElement from, FrameworkElement to, UIElement relativeTo)
    {
        if (from.ActualWidth <= 0) { Fade(to, 0, (float)Emphasis.Full); return; }
        SharedMove(from.TransformToVisual(relativeTo).TransformBounds(new Windows.Foundation.Rect(0, 0, from.ActualWidth, from.ActualHeight)), to, relativeTo);
    }

    /// <summary>출발 요소가 이미 화면에서 빠진 경우: 빠지기 전에 잰 경계에서 옮겨 온다 (M5 화면 전환).</summary>
    public void SharedMove(Windows.Foundation.Rect f, FrameworkElement to, UIElement relativeTo)
    {
        if (Reduced || f.Width <= 0 || to.ActualWidth <= 0) { Fade(to, 0, (float)Emphasis.Full); return; }
        var t = to.TransformToVisual(relativeTo).TransformBounds(new Windows.Foundation.Rect(0, 0, to.ActualWidth, to.ActualHeight));
        var visual = Prepare(to);
        visual.CenterPoint = Vector3.Zero;
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3((float)(f.Width / t.Width), (float)(f.Height / t.Height), 1));
        scale.InsertKeyFrame(1, Vector3.One, move);
        scale.Duration = MotionTokens.Slow;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, new Vector3((float)(f.X - t.X), (float)(f.Y - t.Y), 0));
        offset.InsertKeyFrame(1, Vector3.Zero, move);
        offset.Duration = MotionTokens.Slow;
        Run(to, MotionKind.Move, visual, ("Scale", scale), ("Translation", offset), () => { visual.Scale = Vector3.One; visual.Properties.InsertVector3("Translation", Vector3.Zero); });
        Fade(to, 0, (float)Emphasis.Full, MotionTokens.Mid);
    }

    /// <summary>한 번 도는 색·투명도 변화 (상태 반응). 줄이기면 바로 최종값.</summary>
    internal void RunOnce(UIElement owner, Visual visual, string property, CompositionAnimation animation, Action final)
    {
        if (Reduced) { final(); return; }
        Run(owner, MotionKind.Fade, visual, (property, animation), null, final);
    }

    public CompositionEasingFunction EnterEasing => enter;
    public CompositionEasingFunction MoveEasing => move;
    public Compositor Compositor => compositor;

    // ---------- 내부 ----------

    private Visual Prepare(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }

    /// <param name="endless">끝없이 도는 반복이면 true (끝나지 않으므로 완료로 해제하지 않는다). 횟수가 정해진 반복은 false.</param>
    private MotionHandle Run(UIElement owner, MotionKind kind, Visual visual, (string Property, CompositionAnimation Animation) first,
        (string Property, CompositionAnimation Animation)? second, Action final, bool? endless = null)
    {
        created[kind]++;
        bool forever = endless ?? kind == MotionKind.Repeat;
        MotionHandle? handle = null;
        Action? detachUnloaded = null;
        bool ended = false;
        void Finish()
        {
            if (ended) return;
            ended = true;
            detachUnloaded?.Invoke();   // 끝난 모션이 화면 이탈 처리기·클로저를 붙잡고 있지 않게
            visual.StopAnimation(first.Property);
            if (second is { } s) visual.StopAnimation(s.Property);
            final();
            if (handle is not null && active.Remove(handle)) ActiveChanged?.Invoke();
        }
        handle = new MotionHandle(kind, Finish);
        active.Add(handle);
        ActiveChanged?.Invoke();
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(first.Property, first.Animation);
        if (second is { } sec) visual.StartAnimation(sec.Property, sec.Animation);
        batch.End();
        if (HoldAt is { } p && !forever)
        {
            // 캡처용: 화면 전환 등 한 번 도는 모션을 이 진행도에서 멈춘다 (M5 G5 전환 장면)
            foreach (var prop in second is { } s2 ? new[] { first.Property, s2.Property } : [first.Property])
                if (visual.TryGetAnimationController(prop) is { } controller) { controller.Pause(); controller.Progress = (float)p; }
        }
        // 끝없는 반복은 끝나지 않는다. 한 번 돌거나 횟수가 정해진 것은 끝나면 최종 상태로 정리하고 해제한다.
        batch.Completed += (_, _) => owner.DispatcherQueue?.TryEnqueue(() => { if (!forever) Finish(); });
        detachUnloaded = owner.RegisterUnloaded(Finish);
        return handle;
    }

    private void End(MotionHandle h) => h.Stop();

    private CompositionEasingFunction Ease(Easing e) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2((float)e.X1, (float)e.Y1), new Vector2((float)e.X2, (float)e.Y2));
}

internal static class UnloadedExtensions
{
    /// <summary>요소가 화면에서 떨어질 때 한 번 부른다. 돌려받은 동작을 부르면 등록을 푼다.</summary>
    public static Action RegisterUnloaded(this UIElement element, Action action)
    {
        if (element is not FrameworkElement fe) return () => { };
        void Handler(object s, RoutedEventArgs e) { fe.Unloaded -= Handler; action(); }
        fe.Unloaded += Handler;
        return () => fe.Unloaded -= Handler;
    }
}
