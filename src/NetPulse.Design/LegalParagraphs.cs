namespace NetPulse.Design;

/// <summary>
/// 긴 고지 원문을 문단 목록으로 나눈다 (M10 설계 5절: 넘으면 문단 단위 가상화 목록).
/// 빈 줄로 나누고, 한 문단이 <paramref name="maxChars"/>보다 길면 줄 경계에서 다시 나눈다 (한 줄이 그보다 길면 그 줄 하나).
/// 이어 붙이면(문단 사이 빈 줄) 원문의 줄들이 순서대로 모두 남는다. 앞뒤 빈 줄만 버린다.
/// </summary>
public static class LegalParagraphs
{
    /// <summary>이보다 긴 원문은 문단 목록으로 보인다 (글 상자 하나에 33만 자를 넣으면 첫 배치가 0.8초 걸렸다, M10 UI 시험 1차).</summary>
    public const int LongText = 20_000;

    public static IReadOnlyList<string> Split(string text, int maxChars = 4000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        var result = new List<string>();
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var paragraph = block.Trim('\n');
            if (paragraph.Length == 0) continue;
            if (paragraph.Length <= maxChars) { result.Add(paragraph); continue; }
            var piece = new System.Text.StringBuilder();
            foreach (var line in paragraph.Split('\n'))
            {
                if (piece.Length > 0 && piece.Length + 1 + line.Length > maxChars)
                {
                    result.Add(piece.ToString());
                    piece.Clear();
                }
                if (piece.Length > 0) piece.Append('\n');
                piece.Append(line);
            }
            if (piece.Length > 0) result.Add(piece.ToString());
        }
        return result;
    }
}
