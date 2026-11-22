using Microsoft.UI.Dispatching;
using NetPulse.Design.Effects;
using NetPulse.Design.Settings;
using Windows.UI.ViewManagement;
using PowerManager = Microsoft.Windows.System.Power.PowerManager;
using EnergySaverStatus = Microsoft.Windows.System.Power.EnergySaverStatus;

namespace NetPulse.App.Design;

/// <summary>
/// 적용 모드의 입력을 모으고 바뀌면 다시 계산한다 (M2 설계 5.2).
/// 입력은 OS 상태 공급자(투명 효과, 애니메이션, 고대비, 배터리 절약, 시스템 색, 시스템 테마)와 앱 설정, 저성능 대체.
/// 시뮬레이션 인수가 있으면 해당 OS 값을 고정값으로 바꾼다 (Windows 설정은 바꾸지 않는다).
/// OS 이벤트는 다른 스레드에서 올 수 있어 UI 스레드로 넘겨 계산한다.
/// </summary>
public sealed class Effects
{
    private readonly UISettings ui = new();               // 필드로 붙잡아야 이벤트가 계속 온다
    private readonly AccessibilitySettings accessibility = new();
    private readonly DispatcherQueue dispatcher;
    private readonly StartupOptions options;
    private readonly AppLog log;
    private bool lowPerformance;
    private readonly Action<AppSettings>? persist;

    /// <param name="persist">설정을 저장하는 길 (<c>SettingsStore</c>, M9 설계 B1 [2-7]). null이면 저장하지 않는다.</param>
    public Effects(DispatcherQueue dispatcher, AppSettings settings, StartupOptions options, AppLog log, Action<AppSettings>? persist = null)
    {
        this.persist = persist;
        this.dispatcher = dispatcher;
        this.options = options;
        this.log = log;
        Settings = settings;
        lowPerformance = options.SimulateLowPerformance;
        Mode = EffectsPolicy.Compute(ReadOs(), Settings, lowPerformance);

        // 고대비를 켜고 끄면 ColorValuesChanged도 온다. AccessibilitySettings.HighContrastChanged는 패키지 없는 데스크톱 앱에서
        // 구독이 실패하므로(0x80070490, 관측) 구독을 시도만 하고 실패하면 ColorValuesChanged로 대신한다.
        Subscribe("advancedEffects", () => ui.AdvancedEffectsEnabledChanged += OnUi);
        Subscribe("animations", () => ui.AnimationsEnabledChanged += OnUi);
        Subscribe("colorValues", () => ui.ColorValuesChanged += OnUi);
        Subscribe("highContrast", () => accessibility.HighContrastChanged += OnAccessibility);
        Subscribe("energySaver", () => PowerManager.EnergySaverStatusChanged += OnPower);
    }

    private void OnUi(UISettings sender, object args) => Post();
    private void OnAccessibility(AccessibilitySettings sender, object args) => Post();
    private void OnPower(object? sender, object args) => Post();

    /// <summary>창을 닫기 전에 OS 이벤트 구독을 모두 푼다 (정적 이벤트가 남으면 종료 때 문제가 된다).</summary>
    public void Detach()
    {
        Unsubscribe(() => ui.AdvancedEffectsEnabledChanged -= OnUi);
        Unsubscribe(() => ui.AnimationsEnabledChanged -= OnUi);
        Unsubscribe(() => ui.ColorValuesChanged -= OnUi);
        Unsubscribe(() => accessibility.HighContrastChanged -= OnAccessibility);
        Unsubscribe(() => PowerManager.EnergySaverStatusChanged -= OnPower);
        Changed = null;
    }

    private static void Unsubscribe(Action unsubscribe)
    {
        try { unsubscribe(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    /// <summary>OS 이벤트 하나를 구독한다. 실패하면 기록하고 계속한다 (다른 이벤트·시작 때 읽은 값으로 동작).</summary>
    private void Subscribe(string name, Action subscribe)
    {
        try { subscribe(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.Write("osEventUnavailable", new { name, hresult = ex.HResult });
        }
    }

    public AppSettings Settings { get; private set; }
    public EffectiveMode Mode { get; private set; }
    public OsState Os { get; private set; } = null!;

    /// <summary>모드가 바뀌었을 때 (이전, 새). UI 스레드에서 부른다.</summary>
    public event Action<EffectiveMode, EffectiveMode>? Changed;

    /// <summary>진단: 모드 변경 구독자 수 (전환을 거듭해도 늘지 않아야 한다)</summary>
    public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

    public void UpdateSettings(AppSettings next, bool persist)
    {
        Settings = next;
        if (persist) this.persist?.Invoke(next);   // 저장 실패 기록은 저장소 쪽이 한다
        Refresh();
    }

    public void SetLowPerformance(bool value)
    {
        if (value == lowPerformance) return;
        lowPerformance = value;
        log.Write("lowPerformance", new { on = value });
        Refresh();
    }

    private void Post() => dispatcher.TryEnqueue(Refresh);

    public void Refresh()
    {
        var previous = Mode;
        var previousOs = Os;
        Mode = EffectsPolicy.Compute(ReadOs(), Settings, lowPerformance);
        if (EffectsPolicy.NeedsReapply(previous, previousOs, Mode, Os))
        {
            log.Write("mode", Describe(Mode));
            Changed?.Invoke(previous, Mode);
        }
    }

    public object Describe(EffectiveMode m) => new
    {
        theme = m.Theme.ToString().ToLowerInvariant(),
        material = m.Material.ToString().ToLowerInvariant(),
        lights = m.LightsVisible, moving = m.LightsMoving, aurora = m.AuroraVisible, reduceMotion = m.ReduceMotion,
        grain = m.Material == MaterialMode.Acrylic,   // 흑요석 미세 결 (M12 설계 E)
        reasons = m.Reasons, simulated = options.AnySimulation,
    };

    private OsState ReadOs()
    {
        bool energySaver = false;
        try { energySaver = PowerManager.EnergySaverStatus == EnergySaverStatus.On; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        var os = new OsState(
            TransparencyEnabled: !options.SimulateTransparencyOff && ui.AdvancedEffectsEnabled,
            AnimationsEnabled: !options.SimulateAnimationsOff && ui.AnimationsEnabled,
            HighContrast: options.SimulateHighContrast is not null || accessibility.HighContrast,
            EnergySaver: options.SimulateEnergySaver || energySaver,
            SystemPrefersDark: SystemColors.PrefersDark(ui),
            SystemColors: options.SimulateHighContrast is { } name ? SystemColors.Simulated(name) : SystemColors.FromOs(ui));
        Os = os;
        return os;
    }
}
