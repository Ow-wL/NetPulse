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
    public Ui(Window window, StartupOptions options, AppLog log, AppSettings settings)
    {
        Window = window;
        Options = options;
        Log = log;
        Brushes = new TokenBrushes(Palette.Dark);
        Glass = new Glass(Brushes);
        Motion = new Motion(window.Compositor) { Frozen = options.FreezeMotion };
        Lights = new Lights(Motion);
        Effects = new Effects(window.DispatcherQueue, settings, options, log);
        Theme = new Theme(this);
        Effects.Changed += (_, mode) => Theme.Apply(mode);
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
