using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetPulse.Design.Tokens;
using Rect = Windows.Foundation.Rect;

namespace NetPulse.App.Design.Showcase;

/// <summary>
/// 진단과 자체 시험 (M2 설계 5.3, 5.8, 7.1).
/// - diag.state: 지금 모드, 재질, 모션 수, 열린 오버레이, 초점 요소 (UI 테스트가 읽음)
/// - --selftest focus: 키 입력 없이 FocusManager로 계산한 탭 순서
/// - --selftest dialog: 대화상자 초점 가둠과 Esc 경로, 닫은 뒤 초점 복귀
/// - --selftest layout: 이름 있는 요소의 잘림·넘침·겹침, 글자 잘림
/// 결과는 app-log.jsonl의 "selftest" 기록.
/// </summary>
public sealed partial class ShowcaseWindow
{
    private void QueueDiag()
    {
        if (diagQueued) return;
        diagQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { diagQueued = false; WriteDiag(); });
    }

    private void WriteDiag()
    {
        var mode = ui.Effects.Mode;
        var focused = root.XamlRoot is { } xr ? FocusManager.GetFocusedElement(xr) as DependencyObject : null;
        var state = new
        {
            theme = mode.Theme.ToString().ToLowerInvariant(),
            material = mode.Material.ToString().ToLowerInvariant(),
            glassAcrylic = ui.Glass.IsAcrylic,
            lightsVisible = mode.LightsVisible,
            lightsMoving = mode.LightsMoving,
            problem = ui.Lights.Problem,
            aurora = ui.Lights.AuroraOn,
            reduceMotion = ui.Motion.Reduced,
            frozen = ui.Motion.Frozen,
            simulated = options.AnySimulation,
            active = ui.Motion.ActiveByKind.ToDictionary(k => k.Key.ToString().ToLowerInvariant(), k => k.Value),
            created = ui.Motion.CreatedByKind.ToDictionary(k => k.Key.ToString().ToLowerInvariant(), k => k.Value),
            section = rail.Current,
            dialogOpen = dialog.IsOpen,
            toasts = toasts.Count,
            focused = focused is null ? null : AutomationProperties.GetAutomationId(focused),
            uiScale = options.UiScale,
            reasons = mode.Reasons,
        };
        diag.Text = JsonSerializer.Serialize(state);
    }

    private void RunSelfTest()
    {
        switch (options.SelfTest)
        {
            case "focus": log.Write("selftest", new { kind = "focus", order = FocusOrder(root, ScopeLimit) }); break;
            case "dialog": DialogSelfTest(); break;
            case "layout": log.Write("selftest", new { kind = "layout", report = LayoutReport() }); break;
            default: log.Write("selftest", new { kind = options.SelfTest, error = "unknown" }); break;
        }
    }

    private static int ScopeLimit => 80;

    /// <summary>탭 순서: 첫 초점 가능 요소부터 초점을 옮겨 가며 FindNextElement(다음)를 따라간다. 처음으로 돌아오면 멈춘다.</summary>
    private static List<string> FocusOrder(UIElement scope, int limit)
    {
        var order = new List<string>();
        var first = FocusManager.FindFirstFocusableElement(scope);
        var opts = new FindNextElementOptions { SearchRoot = scope };
        var current = first;
        for (int i = 0; i < limit && current is Control c; i++)
        {
            order.Add(Id(c));
            c.Focus(FocusState.Keyboard);
            var next = FocusManager.FindNextElement(FocusNavigationDirection.Next, opts);
            if (next is null || ReferenceEquals(next, first)) break;
            current = next;
        }
        return order;
    }

    private async void DialogSelfTest()
    {
        rail.Select("dialog");
        await Task.Delay(MotionTokens.Slow);
        var opener = FindById(root, "dialog.open") as Control;
        if (opener is null) { log.Write("selftest", new { kind = "dialog", error = "no-opener" }); return; }
        opener.Focus(FocusState.Keyboard);
        OpenDialog(opener, FocusState.Keyboard);
        await Task.Delay(MotionTokens.Slow);
        var visited = new List<string?>();
        var xr = root.XamlRoot;
        visited.Add(Id(FocusManager.GetFocusedElement(xr)));
        for (int i = 0; i < 10; i++)
        {
            await FocusManager.TryMoveFocusAsync(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = xr.Content });
            visited.Add(Id(FocusManager.GetFocusedElement(xr)));
        }
        bool trapped = visited.All(v => v is "dialog.confirm" or "dialog.cancel");
        dialog.CloseFromEscape();
        await Task.Delay(MotionTokens.Fast);
        var after = Id(FocusManager.GetFocusedElement(xr));
        log.Write("selftest", new { kind = "dialog", visited, trapped, focusAfterClose = after, closed = !dialog.IsOpen });
    }

    /// <summary>이름 있는 요소마다 경계, 보이는 영역(조상 스크롤·창과의 교차), 스크롤 안 여부. 글자 잘림, 상호작용 요소 겹침.</summary>
    private object LayoutReport()
    {
        var items = new List<object>();
        var interactive = new List<(string Id, Rect Bounds)>();
        var trimmed = new List<string>();
        var rootRect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        void Walk(DependencyObject node, Rect clip, bool inScroll)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is FrameworkElement fe && fe.ActualWidth > 0)
            {
                var bounds = fe.TransformToVisual(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                var id = AutomationProperties.GetAutomationId(fe);
                if (fe is TextBlock { IsTextTrimmed: true } tb) trimmed.Add(id.Length > 0 ? id : tb.Text);
                if (id.Length > 0 && AutomationProperties.GetAccessibilityView(fe) != AccessibilityView.Raw)
                {
                    var visible = Intersect(bounds, clip);
                    items.Add(new { id, x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height, vw = visible.Width, vh = visible.Height, inScroll });
                    if (fe is ButtonBase or ToggleButton) interactive.Add((id, bounds));
                }
                if (fe is ScrollViewer sv)
                {
                    clip = Intersect(clip, bounds);
                    inScroll = true;
                }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), clip, inScroll);
        }
        Walk(appGrid, rootRect, false);
        var overlaps = new List<string>();
        for (int a = 0; a < interactive.Count; a++)
            for (int b = a + 1; b < interactive.Count; b++)
            {
                var r = Intersect(interactive[a].Bounds, interactive[b].Bounds);
                if (r.Width > 1 && r.Height > 1) overlaps.Add(interactive[a].Id + "|" + interactive[b].Id);
            }
        return new { root = new { w = root.ActualWidth, h = root.ActualHeight }, items, trimmed, overlaps };
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        double r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r <= x || btm <= y ? new Rect(x, y, 0, 0) : new Rect(x, y, r - x, btm - y);
    }

    private static string Id(object? element) => element is DependencyObject d ? AutomationProperties.GetAutomationId(d) is { Length: > 0 } id ? id : d.GetType().Name : "(none)";

    private static DependencyObject? FindById(DependencyObject node, string id)
    {
        if (AutomationProperties.GetAutomationId(node) == id) return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindById(VisualTreeHelper.GetChild(node, i), id) is { } found) return found;
        return null;
    }
}
