using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace NetPulse.App.Design;

/// <summary>
/// "그리기 끝남" 진단 (M12 설계 G1-B [2-4], 기록 docs/loop/m12/capture-wait.md). 캡처 시험이 고정 대기(700 ms) 대신 이 값을 기다린다.
/// settled = 예약된 그리기 작업 0 + 끝나는 모션 0 (끝없는 반복·진행도 고정 모션 제외) + 필수 요소 배치 + 타임라인 열 수 일치.
/// 값은 진단(diag.state)의 <c>settle</c>로 나간다. 진단은 그 순간의 값이라, 시험은 두 캡처 앞뒤로 같은 값인지 다시 본다.
/// </summary>
internal sealed class DrawSettle
{
    private readonly FrameworkElement layoutSource;
    private readonly Action queueDiag;
    private Func<bool>? waitingFor;

    /// <param name="layoutSource">배치가 끝났는지 들을 요소 (창의 뿌리).</param>
    /// <param name="queueDiag">진단 다시 쓰기 요청.</param>
    public DrawSettle(FrameworkElement layoutSource, Action queueDiag)
    {
        this.layoutSource = layoutSource;
        this.queueDiag = queueDiag;
    }

    /// <summary>진단 값. <paramref name="laidOut"/>·<paramref name="timelineOk"/>는 배치가 바뀔 때 다시 보기 위해 함수로 받는다.</summary>
    public object Diag(Ui ui, int pendingWork, int generation, int sceneGeneration, Func<bool> laidOut, Func<bool> timelineOk)
    {
        var theme = ui.Theme;
        // 원형 테마 전환도 끝나는 모션이다 (Motion 밖에서 돈다). 진행도 고정으로 멈춘 것은 세지 않는다
        int finite = ui.Motion.FiniteRunning + (theme.InTransition && !theme.TransitionHeld ? 1 : 0);
        int held = ui.Motion.HeldRunning + (theme.InTransition && theme.TransitionHeld ? 1 : 0);
        bool laid = laidOut(), timeline = timelineOk();
        if (!(laid && timeline)) Watch(() => laidOut() && timelineOk());
        return new
        {
            pendingWork, finiteMotions = finite, heldMotions = held, generation, sceneGeneration, laidOut = laid, timelineOk = timeline,
            settled = pendingWork == 0 && finite == 0 && laid && timeline,
        };
    }

    /// <summary>
    /// 배치·타임라인 조건이 참이 되는 순간을 알려 주는 사건이 없다. 거짓인 동안 배치가 끝날 때마다 조건만 다시 보고, 참이 되면 진단을 한 번 다시 쓴다.
    /// 거짓인 동안 진단을 다시 쓰지는 않는다: 진단 글이 바뀌면 배치가 다시 일어나 되먹임 고리가 된다.
    /// </summary>
    private void Watch(Func<bool> ready)
    {
        bool hooked = waitingFor is not null;
        waitingFor = ready;
        if (!hooked) layoutSource.LayoutUpdated += OnLayout;
    }

    private void OnLayout(object? sender, object e)
    {
        if (waitingFor is not { } ready || !ready()) return;
        layoutSource.LayoutUpdated -= OnLayout;
        waitingFor = null;
        queueDiag();
    }

    /// <summary>
    /// 필수 요소 배치: 그릇이 트리에 붙어 크기가 있고, 보이는 필수 요소가 모두 크기를 가졌다.
    /// 자기나 조상이 접힌(Collapsed) 요소, 그릇까지 부모가 이어지지 않는 요소는 판정에서 뺀다.
    /// </summary>
    public static bool LaidOut(FrameworkElement owner, IEnumerable<UIElement> required)
    {
        if (!owner.IsLoaded || owner.ActualWidth <= 0 || owner.ActualHeight <= 0) return false;
        foreach (var e in required)
            if (Shown(e, owner) && (e.ActualSize.X <= 0 || e.ActualSize.Y <= 0)) return false;
        return true;
    }

    private static bool Shown(UIElement element, DependencyObject owner)
    {
        for (DependencyObject? n = element; n is not null; n = VisualTreeHelper.GetParent(n) ?? (n as FrameworkElement)?.Parent)
        {
            if (n is UIElement u && u.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(n, owner)) return true;
        }
        return false;
    }
}
