using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using NetPulse.Design.Settings;
using NetPulse.Design.Tokens;

namespace NetPulse.App.Design;

/// <summary>
/// 창 하나의 디자인 시스템 묶음: 공유 브러시, 재질, 모션, 배경 빛, 적용 모드, 테마.
/// 부품은 이것 하나를 받아 토큰과 도구를 쓴다.
/// </summary>
public sealed class Ui
{
    public Ui(Window window, StartupOptions options, AppLog log, AppSettings settings, Action<AppSettings>? persist = null)
    {
        Window = window;
        Options = options;
        Log = log;
        Brushes = new TokenBrushes(Palette.Dark);
        Motion = new Motion(window.Compositor) { Frozen = options.FreezeMotion, HoldAt = options.MotionHold };
        Glass = new Glass(Brushes, Motion);
        Lights = new Lights(Motion);
        Lights.Prepare();
        Effects = new Effects(window.DispatcherQueue, settings, options, log, persist);
        Theme = new Theme(this) { HoldAt = options.RevealHold };
        Effects.Changed += (previous, mode) => Theme.OnModeChanged(previous, mode);
        Pacer = new FramePacer(() => Theme.InTransition, low => Effects.SetLowPerformance(low || options.SimulateLowPerformance));
        window.SizeChanged += (_, _) => Pacer.Suspend(MotionTokens.Slow * 3);   // 창 크기 조절 중 표본은 넣지 않는다
    }

    public FramePacer Pacer { get; }

    /// <summary>창을 닫기 전에: OS 이벤트, 렌더링 구독, 진행 중인 전환, 실행 중인 모션을 모두 정리한다.</summary>
    public void Detach()
    {
        Theme.Detach();
        Pacer.Detach();
        Effects.Detach();
        Motion.SetReduced(true);
    }

    public Window Window { get; }
    public StartupOptions Options { get; }
    public AppLog Log { get; }
    public TokenBrushes Brushes { get; }
    public Glass Glass { get; }
    public Motion Motion { get; }
    public Lights Lights { get; }
    public Effects Effects { get; }
    public Theme Theme { get; }

    /// <summary>창의 내용 뿌리 (요청 테마를 거는 곳).</summary>
    public FrameworkElement? Root { get; set; }
}
