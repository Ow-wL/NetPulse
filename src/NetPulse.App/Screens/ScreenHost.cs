using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetPulse.App.Design;
using NetPulse.App.Design.Controls;
using NetPulse.Core.Live;

namespace NetPulse.App.Screens;

/// <summary>화면들이 함께 쓰는 것 (M5 설계 4.2).</summary>
public sealed class ScreenContext
{
    public required Ui Ui { get; init; }
    public required MeasurementHost Host { get; init; }
    public required RecentRecords Recent { get; init; }
    public required ToastHost Toasts { get; init; }
    public required DialogHost Dialog { get; init; }
    public required StartupOptions Options { get; init; }
    public required AppLog Log { get; init; }
    /// <summary>화면 이동 (id, 이동을 일으킨 요소: 공통 요소 이동의 출발점).</summary>
    public required Action<string, FrameworkElement?> Navigate { get; init; }
    /// <summary>진단 값 다시 쓰기 요청.</summary>
    public required Action QueueDiag { get; init; }
    /// <summary>테마 원형 전환의 시작점.</summary>
    public required Action<FrameworkElement?> SetRevealOrigin { get; init; }
    public DateTimeOffset Now => Host.World.Now;
}

/// <summary>화면 하나 (M5 설계 4.5). 장면이 바뀌면 보이는 화면만 다시 그린다 (설계 4.3).</summary>
public abstract class Screen : Grid
{
    protected Screen(ScreenContext context, string id)
    {
        Context = context;
        Id = id;
    }

    protected ScreenContext Context { get; }
    protected Ui Ui => Context.Ui;
    public string Id { get; }

    /// <summary>들어올 때 차례로 등장할 요소 (카드).</summary>
    public abstract IReadOnlyList<UIElement> Parts { get; }

    /// <summary>공통 요소 이동의 도착점 (없으면 교차 페이드만).</summary>
    public virtual FrameworkElement? SharedTarget => null;

    /// <summary>호스트 장면이나 최근 측정이 바뀜 (UI 스레드). 보이는 화면에만 불린다.</summary>
    public abstract void Render(HostSnapshot snapshot);

    /// <summary>화면에 들어올 때.</summary>
    public virtual void Entered() { }

    /// <summary>Esc (메모 패널 닫기, 편집 화면 뒤로). 처리했으면 true.</summary>
    public virtual bool Escape() => false;

    /// <summary>진단 값 (UI 시험이 읽는다).</summary>
    public virtual object? Diag() => null;
}

/// <summary>
/// 화면 바꾸기 (M5 설계 4.3): 화면 객체는 처음 열 때 만들고 유지한다. 나가는 화면은 짧게 흐리고 들어오는 화면의 카드는 차례로 등장한다.
/// 홈의 "최근 2분" → 측정 화면의 타임라인은 공통 요소 이동.
/// </summary>
public sealed class ScreenHost : Grid
{
    private readonly Ui ui;
    private readonly Dictionary<string, Func<Screen>> factories = [];
    private readonly Dictionary<string, Screen> screens = [];

    public ScreenHost(Ui ui) => this.ui = ui;

    public Screen? Current { get; private set; }
    public string? CurrentId => Current?.Id;

    public void Register(string id, Func<Screen> create) => factories[id] = create;

    public Screen Get(string id)
    {
        if (!screens.TryGetValue(id, out var s)) screens[id] = s = factories[id]();
        return s;
    }

    public void Show(string id, HostSnapshot snapshot, FrameworkElement? from)
    {
        var next = Get(id);
        if (ReferenceEquals(next, Current)) return;
        var previous = Current;
        // 출발 요소의 경계는 나가는 화면을 빼기 전에 잰다
        Windows.Foundation.Rect? fromRect = from is not null && from.ActualWidth > 0 && from.XamlRoot is not null
            ? from.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, from.ActualWidth, from.ActualHeight)) : null;
        if (previous is not null) Children.Remove(previous);
        if (!Children.Contains(next)) Children.Add(next);
        Current = next;
        next.Render(snapshot);
        next.Entered();
        // 공통 요소 이동은 도착점이 배치된 뒤에만 (줄이기면 모션 도구가 페이드로 바꾼다)
        if (fromRect is { } rect && next.SharedTarget is { } target)
        {
            void Once(object? s, object e)
            {
                target.LayoutUpdated -= Once;
                ui.Motion.SharedMove(rect, target, this);
            }
            target.LayoutUpdated += Once;
            ui.Motion.CrossFade(null, next.Parts.Where(p => !ReferenceEquals(p, target)).ToArray());
        }
        else ui.Motion.CrossFade(null, next.Parts);
    }

    /// <summary>보이는 화면만 다시 그린다. 숨은 화면은 돌아올 때 Show가 최신 값으로 한 번 그린다.</summary>
    public void Render(HostSnapshot snapshot) => Current?.Render(snapshot);
}
