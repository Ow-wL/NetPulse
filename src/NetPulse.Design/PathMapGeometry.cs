using NetPulse.Design.Tokens;

namespace NetPulse.Design;

/// <summary>
/// 경로 맵 선의 끝점 (M11 C3). 노드 칸은 원과 그 아래 이름·설명 글 중 넓은 쪽의 폭이라, 설명 글(예: "2개 중 2개 응답 · 21 ms")이
/// 원보다 넓으면 원 양옆에 빈 곳이 생기고, 칸 경계에서 끝나던 선이 인터넷 원에 닿지 않았다.
/// 선의 여백을 그 빈 곳만큼 줄여(음수 여백) 선이 원 가장자리에서 늘 <see cref="Space.S2"/> 떨어진 곳에서 시작하고 끝나게 한다.
/// </summary>
public static class PathMapGeometry
{
    /// <summary>노드 칸 안에서 원 바깥의 한쪽 빈 폭 (원은 칸 가운데).</summary>
    public static double Overhang(double nodeWidth) => Math.Max(0, (nodeWidth - Size.MapNode) / 2);

    /// <summary>두 노드 사이 선의 왼쪽·오른쪽 여백 (노드 칸 폭으로 계산, 음수면 노드 칸 안으로 들어간다).</summary>
    public static (double Left, double Right) LinkMargins(double leftNodeWidth, double rightNodeWidth) =>
        (Space.S2 - Overhang(leftNodeWidth), Space.S2 - Overhang(rightNodeWidth));
}
