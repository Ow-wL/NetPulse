using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rect = Windows.Foundation.Rect;

namespace NetPulse.App.Design;

/// <summary>
/// 배치 검사 (M2 설계 5.8, M5 설계 7.4): 이름 있는 요소마다 경계와 보이는 영역(조상 스크롤·클립·창과의 교차),
/// 글자 잘림, 상호작용 요소 겹침. 전시 화면과 메인 창이 같은 검사를 쓴다.
/// </summary>
public static class LayoutProbe
{
    /// <param name="overlays">겹쳐 뜨는 층(알림 등): 그 안의 요소는 그 층 안의 요소끼리만 겹침을 본다 (내용 위에 뜨는 것이 설계이므로)</param>
    public static object Report(FrameworkElement root, DependencyObject? skip, IReadOnlyList<DependencyObject>? overlays = null)
    {
        var items = new List<object>();
        var interactive = new List<(string Id, Rect Bounds, int Layer)>();
        int layer = 0;
        var trimmed = new List<string>();
        var rootRect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        void Walk(DependencyObject node, Rect clip, bool inScroll)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            // 자동화에서 빠진 하위 트리(대화상자 뒤 화면, 장식)는 검사하지 않는다
            if (node is UIElement ue && !ReferenceEquals(node, root) && AutomationProperties.GetAccessibilityView(ue) == AccessibilityView.Raw) return;
            if (ReferenceEquals(node, skip)) return;   // 진단 출력용 글자는 화면 요소가 아니다
            if (node is FrameworkElement { Width: 0, Height: 0 }) return;   // 크기 0인 시험 전용 요소
            if (node is FrameworkElement fe && fe.ActualWidth > 0)
            {
                // 명시적 Clip도 보이는 영역을 줄인다 (G4 M2 1라운드 지적 6)
                if (fe.Clip is RectangleGeometry rg)
                    clip = Intersect(clip, fe.TransformToVisual(root).TransformBounds(rg.Rect));
                var bounds = fe.TransformToVisual(root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                var id = AutomationProperties.GetAutomationId(fe);
                if (fe is TextBlock { IsTextTrimmed: true } tb) trimmed.Add(id.Length > 0 ? id : tb.Text);
                if (id.Length > 0 && AutomationProperties.GetAccessibilityView(fe) != AccessibilityView.Raw)
                {
                    var visible = Intersect(bounds, clip);
                    items.Add(new { id, x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height, vw = visible.Width, vh = visible.Height, inScroll });
                    // 겹침은 보이는 부분끼리 본다 (스크롤로 가려진 요소가 머리 아래에 겹쳐 보이는 거짓 겹침을 빼려고)
                    if (fe is ButtonBase or ToggleButton or TextBox && visible.Width > 0 && visible.Height > 0) interactive.Add((id, visible, layer));
                }
                if (fe is ScrollViewer sv)
                {
                    clip = Intersect(clip, bounds);
                    inScroll = true;
                }
            }
            int saved = layer;
            if (overlays is not null && overlays.Any(o => ReferenceEquals(o, node))) layer = overlays.ToList().FindIndex(o => ReferenceEquals(o, node)) + 1;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), clip, inScroll);
            layer = saved;
        }
        Walk(root, rootRect, false);   // 오버레이(대화상자, 알림)까지 포함
        var overlaps = new List<string>();
        for (int a = 0; a < interactive.Count; a++)
            for (int b = a + 1; b < interactive.Count; b++)
            {
                if (interactive[a].Layer != interactive[b].Layer) continue;
                var r = Intersect(interactive[a].Bounds, interactive[b].Bounds);
                if (r.Width > 1 && r.Height > 1) overlaps.Add(interactive[a].Id + "|" + interactive[b].Id);
            }
        return new { root = new { w = root.ActualWidth, h = root.ActualHeight }, items, trimmed, overlaps };
    }

    public static Rect Intersect(Rect a, Rect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        double r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r <= x || btm <= y ? new Rect(x, y, 0, 0) : new Rect(x, y, r - x, btm - y);
    }

    public static string Id(object? element) => element is DependencyObject d ? AutomationProperties.GetAutomationId(d) is { Length: > 0 } id ? id : d.GetType().Name : "(none)";

    public static DependencyObject? FindById(DependencyObject node, string id)
    {
        if (AutomationProperties.GetAutomationId(node) == id) return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindById(VisualTreeHelper.GetChild(node, i), id) is { } found) return found;
        return null;
    }
}
