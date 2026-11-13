using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetPulse.Core.Measurement;
using Windows.Graphics;
using Windows.UI;

namespace NetPulse.App;

/// <summary>
/// M1의 빈 창. 흑요석 바탕, 제목, 회차 번호 하나. 디자인 시스템과 화면은 M2·M5.
/// 엔진은 소유하지 않고 구독만 한다. 창이 닫히면 구독을 끊는다.
/// </summary>
public sealed class ShellWindow : Window
{
    private static readonly Color Background = Color.FromArgb(255, 5, 5, 7);       // 흑요석 --bg #050507
    private static readonly Color Ink = Color.FromArgb(255, 244, 244, 245);        // --ink #f4f4f5
    private static readonly Color Muted = Color.FromArgb(255, 155, 155, 164);      // --muted #9b9ba4

    private readonly TextBlock round;
    private readonly TextBlock status;
    private readonly IDisposable subscription;
    private readonly DispatcherQueueTimer? closeTimer;   // 필드로 붙잡아 둔다 (M0 교훈: 지역 변수 타이머는 수거될 수 있다)
    private long shownRounds = -1;

    public ShellWindow(MeasurementEngine engine, string mode, int runSeconds)
    {
        Title = "NetPulse";
        AppWindow.ResizeClient(new SizeInt32(1280, 800));
        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Background;
        bar.ForegroundColor = bar.ButtonForegroundColor = Ink;
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = Muted;

        var bold = new FontFamily("ms-appx:///Fonts/Pretendard-Bold.otf#Pretendard");
        var regular = new FontFamily("ms-appx:///Fonts/Pretendard-Regular.otf#Pretendard");
        var title = new TextBlock { Text = "NetPulse", FontSize = 30, FontFamily = bold, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Ink) };
        status = new TextBlock { Text = mode.StartsWith("demo:", StringComparison.Ordinal) ? "데모 · " + mode[5..] : "측정 대상 없음 (대상 설정은 다음 단계에서)",
            FontSize = 14, FontFamily = regular, Foreground = new SolidColorBrush(Muted) };
        round = new TextBlock { Text = "—", FontSize = 52, FontFamily = bold, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Ink) };
        AutomationProperties.SetAutomationId(status, "shell.status");
        AutomationProperties.SetAutomationId(round, "shell.round");
        AutomationProperties.SetHelpText(round, "현재 회차");   // 이름은 숫자 그대로 둔다 (화면 읽기와 자동화가 값을 읽는다)

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(48) };
        stack.Children.Add(title);
        stack.Children.Add(status);
        stack.Children.Add(round);
        var root = new Grid { Background = new SolidColorBrush(Background) };
        AutomationProperties.SetAutomationId(root, "shell.root");
        root.Children.Add(stack);
        Content = root;

        // 구독하는 순간 최신 상태가 한 번 온다 (화면 준비 전 결과도 놓치지 않는다).
        subscription = engine.Subscribe(state => DispatcherQueue.TryEnqueue(() => Show(state)));
        Closed += (_, _) => { subscription.Dispose(); closeTimer?.Stop(); };

        if (runSeconds > 0)
        {
            closeTimer = DispatcherQueue.CreateTimer();
            closeTimer.Interval = TimeSpan.FromSeconds(runSeconds);
            closeTimer.Tick += (_, _) => Close();
            closeTimer.Start();
        }
    }

    private void Show(EngineState state)
    {
        // 엔진 스레드에서 온 알림이 순서를 바꿔 도착해도 더 오래된 상태로 되돌리지 않는다.
        if (state.Rounds < shownRounds) return;
        shownRounds = state.Rounds;
        round.Text = state.Latest is { } latest ? latest.Tick.Index.ToString(CultureInfo.InvariantCulture) : "—";
    }
}
