using NetPulse.Design.Settings;

namespace NetPulse.Design.Tests;

/// <summary>M10 (릴리스 준비) Design 시험: 버전 글 해석(A1), 사용 조건 동의 기록(A2 [2-4]).</summary>
public sealed class M10Tests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "netpulse-terms-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("1.0.0+762addc822e7e6865a456fe4b68330e3e0926119", "1.0.0", "762addc")]
    [InlineData("1.0.0+762ADDC822E7E6865A456FE4B68330E3E0926119", "1.0.0", "762addc")]
    [InlineData("1.0.0", "1.0.0", null)]
    [InlineData("1.0.0+local", "1.0.0", null)]          // 40자 16진수가 아니면 커밋 없음
    [InlineData("1.0.0+762addc", "1.0.0", null)]
    [InlineData("1.0.0+zz2addc822e7e6865a456fe4b68330e3e0926119", "1.0.0", null)]
    [InlineData("", "0.0.0", null)]
    [InlineData(null, "0.0.0", null)]
    [InlineData("+762addc822e7e6865a456fe4b68330e3e0926119", "0.0.0", "762addc")]
    public void InformationalVersionIsSplitIntoVersionAndShortCommit(string? text, string version, string? commit)
    {
        var (v, c) = AppVersionText.Parse(text);
        Assert.Equal(version, v);
        Assert.Equal(commit, c);
    }

    [Fact]
    public void ConsentIsMissingUntilAccepted()
    {
        Assert.False(TermsConsent.IsAccepted(folder));
        Assert.True(TermsConsent.TryAccept(folder, DateTimeOffset.UnixEpoch));
        Assert.True(TermsConsent.IsAccepted(folder));
        Assert.False(File.Exists(TermsConsent.PathIn(folder) + ".tmp"));
    }

    [Theory]
    [InlineData("{\"terms\":\"terms-0\"}")]      // 다른 판: 다시 묻는다
    [InlineData("{\"terms\":1}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void OtherOrBrokenConsentIsNotAccepted(string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(TermsConsent.PathIn(folder), content);
        Assert.False(TermsConsent.IsAccepted(folder));
    }

    [Fact]
    public void ConsentThatCannotBeWrittenIsReportedAndLeavesNoTempFile()
    {
        Directory.CreateDirectory(folder);
        // 동의 파일 자리에 폴더가 있으면 바꾸기가 실패한다 (쓰기 실패 대역)
        Directory.CreateDirectory(TermsConsent.PathIn(folder));
        Assert.False(TermsConsent.TryAccept(folder, DateTimeOffset.UnixEpoch));
        Assert.False(File.Exists(TermsConsent.PathIn(folder) + ".tmp"));
        Assert.False(TermsConsent.IsAccepted(folder));
    }

    [Fact]
    public void ConsentLivesApartFromTheSettingsFile()
    {
        Assert.True(TermsConsent.TryAccept(folder, DateTimeOffset.UnixEpoch));
        // 설정 파일이 새 판·손상이어도 동의 기록은 따로 읽힌다
        File.WriteAllText(Path.Combine(folder, SettingsFormat.FileName), "{\"version\":99}");
        Assert.Equal(SettingsLoadStatus.UnknownVersion, SettingsFormat.Load(folder).Status);
        Assert.True(TermsConsent.IsAccepted(folder));
    }

    [Fact]
    public void LongNoticeSplitsIntoParagraphsWithoutLosingLines()
    {
        string text = "\n\nA1\nA2\n\n\nB1\r\n\r\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => $"C{i:00}-xxxx")) + "\n\n" + new string('D', 50) + "\n";
        var parts = NetPulse.Design.LegalParagraphs.Split(text, maxChars: 40);
        Assert.Equal("A1\nA2", parts[0]);
        Assert.Equal("B1", parts[1]);
        Assert.All(parts.Where(p => !p.StartsWith('D')), p => Assert.True(p.Length <= 40, p));   // 긴 문단은 줄 경계에서
        Assert.Equal(new string('D', 50), parts[^1]);                                          // 한 줄이 더 길면 그 줄 하나
        var lines = string.Join("\n", parts).Split('\n');
        var expected = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();
        Assert.Equal(expected, lines);                                                         // 줄을 잃거나 바꾸지 않는다
        Assert.Empty(NetPulse.Design.LegalParagraphs.Split("\n\n"));
    }
}
