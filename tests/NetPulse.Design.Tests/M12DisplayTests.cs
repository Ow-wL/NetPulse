using NetPulse.Design.Tokens;

namespace NetPulse.Design.Tests;

/// <summary>M12 설계 D: M11 시각 검토(C3) 표시 결함 중 계산으로 확인할 수 있는 것 (경로 맵 선 끝점, 포트 칸 폭).</summary>
public sealed class M12DisplayTests
{
    [Theory]
    [InlineData(60, 60)]     // 노드 칸이 원과 같은 폭 (이 PC, 공유기)
    [InlineData(60, 128)]    // 오른쪽 노드의 설명 글이 넓음 (인터넷 "2개 중 2개 응답 · 21 ms")
    [InlineData(96, 60)]
    [InlineData(150, 210)]
    public void PathMapLineEndsSameGapFromBothCircles(double leftNode, double rightNode)
    {
        // 칸 배치: [왼쪽 노드 칸][선 칸][오른쪽 노드 칸], 원은 노드 칸 가운데. 선 칸은 0 ~ 100이라 둔다.
        const double lineColumnStart = 0, lineColumnEnd = 100;
        var (left, right) = PathMapGeometry.LinkMargins(leftNode, rightNode);
        double leftCircleEdge = lineColumnStart - (leftNode - Size.MapNode) / 2;
        double rightCircleEdge = lineColumnEnd + (rightNode - Size.MapNode) / 2;
        double lineStart = lineColumnStart + left, lineEnd = lineColumnEnd - right;
        Assert.Equal(Space.S2, lineStart - leftCircleEdge, 6);
        Assert.Equal(Space.S2, rightCircleEdge - lineEnd, 6);
    }

    [Fact]
    public void PathMapNodeNeverNarrowerThanCircle() => Assert.Equal(0, PathMapGeometry.Overhang(Size.MapNode - 10));

    [Fact]
    public void PortFieldFitsFiveDigitsWithClearButton()
    {
        // 포트 칸: 5자리("70000") + 지우기 단추 + 좌우 안쪽 여백 + 초점 테두리. FieldS(96)로는 초점이 있을 때 잘렸다 (M11 C3)
        double width = Size.NumberField(5);
        double needed = 5 * Typography.Body.Size * Typography.DigitWidth + Size.ClearButton + Space.S3 * 2 + Size.FocusOuter * 2;
        Assert.True(width >= needed, $"{width} < {needed}");
        Assert.True(width > Size.FieldS);
        Assert.True(width < Size.FieldM, "보통 칸보다 좁게 (셋째 줄이 150%에서 넘치지 않게)");
    }
}
