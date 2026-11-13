using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetPulse.App.Design;
using NetPulse.Core.Measurement;
using NetPulse.Design.Tokens;
using Windows.Graphics;

namespace NetPulse.App;

/// <summary>
/// M1의 빈 창. 흑요석 바탕, 제목, 회차 번호 하나. 디자인 시스템과 화면은 M2·M5.
/// 엔진은 소유하지 않고 구독만 한다. 창이 닫히면 구독을 끊는다.
/// 닫기(닫기 버튼, Alt+F4, 자동화, --run-seconds)는 모두 한 경로로 간다:
/// 닫기를 잠시 막고 창을 숨긴 뒤, 앱이 준 정리 작업을 UI 스레드를 막지 않고 기다렸다가 실제로 닫는다.
/// </summary>
public sealed class ShellWindow : Window
{
    private readonly TokenBrushes brushes = new(Palette.Dark);
    private readonly TextBlock round;
    private readonly TextBlock status;
    private readonly IDisposable subscription;
    private readonly DispatcherQueueTimer? closeTimer;   // 필드로 붙잡아 둔다 (M0 교훈: 지역 변수 타이머는 수거될 수 있다)
    private readonly Func<Task> beforeClose;
    private long shownRounds = -1;
    private bool closing;
    private bool closeAllowed;

    public ShellWindow(MeasurementEngine engine, string mode, int runSeconds, Func<Task> beforeClose)
    {
        this.beforeClose = beforeClose;
        Title = "NetPulse";
        AppWindow.ResizeClient(new SizeInt32(Size.WindowWidth, Size.WindowHeight));
        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = brushes.Bg.Color;
        bar.ForegroundColor = bar.ButtonForegroundColor = brushes.Ink.Color;
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = brushes.Muted.Color;

        var title = Fonts.Text("NetPulse", Typography.Title, brushes.Ink);
        status = Fonts.Text(mode.StartsWith("demo:", StringComparison.Ordinal) ? "데모 · " + mode[5..] : "측정 대상 없음 (대상 설정은 다음 단계에서)",
            Typography.Body, brushes.Muted);
        round = Fonts.Text("—", Typography.Display, brushes.Ink);
        AutomationProperties.SetAutomationId(status, "shell.status");
        AutomationProperties.SetAutomationId(round, "shell.round");
        AutomationProperties.SetHelpText(round, "현재 회차");   // 이름은 숫자 그대로 둔다 (화면 읽기와 자동화가 값을 읽는다)

        var stack = new StackPanel { Spacing = Space.S2, Margin = new Thickness(Space.S8) };
        stack.Children.Add(title);
        stack.Children.Add(status);
        stack.Children.Add(round);
        var root = new Grid { Background = brushes.Bg };
        AutomationProperties.SetAutomationId(root, "shell.root");
        root.Children.Add(stack);
        Content = root;

        // 구독하는 순간 최신 상태가 한 번 온다 (화면 준비 전 결과도 놓치지 않는다).
        subscription = engine.Subscribe(state => DispatcherQueue.TryEnqueue(() => Show(state)));
        Closed += (_, _) => { subscription.Dispose(); closeTimer?.Stop(); };
        AppWindow.Closing += (_, e) =>
        {
            if (closeAllowed) return;
            e.Cancel = true;
            BeginClose();
        };

        if (runSeconds > 0)
        {
            closeTimer = DispatcherQueue.CreateTimer();
            closeTimer.Interval = TimeSpan.FromSeconds(runSeconds);
            closeTimer.Tick += (_, _) => BeginClose();
            closeTimer.Start();
        }
    }

    /// <summary>정리 작업이 끝난 뒤에 닫는다. 여러 번 불려도 한 번만 정리한다.</summary>
    private async void BeginClose()
    {
        if (closing) return;
        closing = true;
        closeTimer?.Stop();
        AppWindow.Hide();
        try { await beforeClose(); }
        finally
        {
            closeAllowed = true;
            Close();
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
