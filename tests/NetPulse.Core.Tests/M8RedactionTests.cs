using NetPulse.Core.Records;
using NetPulse.Core.Report;
using static NetPulse.Core.Tests.M8Records;

namespace NetPulse.Core.Tests;

/// <summary>가리기와 안전망 (M8 설계 3.3, 구현 중 결정 1·2, 7.1 Redactor).</summary>
public sealed class M8RedactionTests
{
    /// <summary>원본 식별 값: 대상 주소·이름·ID, 사업자 이름, 측정 ID 꼬리, 이름 속의 전화·이메일·UNC.</summary>
    private static readonly string[] Originals =
    [
        "192.0.2.254", "198.51.100.7", "203.0.113.9", Router.Name, ExtA.Name, ExtB.Name, "예시 통신사 가나", "Example Telecom B",
        Tail, "010-1234-5678", "user@example.com", @"\\fileserver", "gw-home", "isp-a1", "isp-b2",
    ];

    private static string Html(ReportDocument d) => ReportHtml.Write(d);

    /// <summary>MAC 꼴 (소스에 글자 그대로 두지 않는다: 공개 전 검사가 MAC 꼴을 모두 거름).</summary>
    private static readonly string Mac = string.Join("-", "aa", "bb", "cc", "dd", "ee", "ff");

    [Fact]
    public void AMaskedReportHasNoOriginalIdentityValueAnywhere()
    {
        var rec = Typical();
        var a = Analyze(rec) with { Links = FourStates() };
        var d = Build(rec, a: a, diagnoses: [Diagnosis(W0.AddMinutes(3))]);
        var html = Html(d);
        foreach (var v in Originals) Assert.False(html.Contains(v, StringComparison.OrdinalIgnoreCase), "남은 값: " + v);
        Assert.Contains("로컬 대상 1", html);
        Assert.Contains("외부 대상 2", html);
        Assert.Contains("사업자 A", html);
        Assert.Contains("사업자 B", html);
        Assert.Contains("유선 어댑터 1", html);
        Assert.Contains("무선 어댑터 1", html);
        Assert.Equal("20261113T042000Z", d.Fields().First(f => f.Field == "condition.id").Value);
        Assert.DoesNotContain(d.Fields(), f => f.Field == "condition.target.address");
        Assert.Empty(d.Guard!.Check(d));
        Assert.True(ReportExport.Render(d).Ok);
    }

    [Fact]
    public void AnUnmaskedReportKeepsNamesAndAddressesButNeverTargetIds()
    {
        var rec = Typical();
        var d = Build(rec, new ReportOptions(Masked: false));
        var html = Html(d);
        Assert.Contains("192.0.2.254", html);
        Assert.Contains("예시 통신사 가나", html);
        Assert.Contains(Markup.Text(ExtB.Name), html);
        Assert.Contains(Id, html);
        foreach (var id in new[] { "gw-home", "isp-a1", "isp-b2" }) Assert.DoesNotContain(id, html);
        Assert.Contains(d.Texts(), r => r.Text == ReportTexts.UnmaskedNote);
        Assert.Equal(["192.0.2.254", "198.51.100.7", "203.0.113.9"], d.Fields().Where(f => f.Field == "condition.target.address").Select(f => f.Value));
    }

    [Fact]
    public void MaskedReportsLeaveMemosOutEvenWhenAsked()
    {
        // 가린 리포트에는 메모를 넣지 않는다 (G4 M8 1~6라운드 뒤 사용자 결정): 메모 넣기를 켜도 표시 시각만, 메모 글·메모 출처 조각 없음
        string memo = "13:20:05에 끊김. 192.0.2.254 " + Mac + " me@example.org 010-9876-5432 https://router.example.net/admin 예시 통신사 가나 gw-home isp-a1 " + @"\nas\photos D:\문서\로그.txt";
        var rec = Typical(memo);
        foreach (var options in new[] { new ReportOptions(), new ReportOptions(IncludeMemos: true) })
        {
            var d = Build(rec, options);
            Assert.False(d.Options.Memos);
            Assert.DoesNotContain(d.Fields(), f => f.Field == "mark.memo");
            Assert.Single(d.Fields(), f => f.Field == "mark.time");
            Assert.DoesNotContain(d.Texts(), r => r.Origin == TextOrigin.Memo);
            Assert.Contains(d.Texts(), r => r.Text == ReportTexts.MaskedNoMemos);
            var rendered = ReportExport.Render(d);
            Assert.True(rendered.Ok);
            foreach (var v in new[] { "13:20:05에", "192.0.2.254", Mac, "me@example.org", "010-9876-5432", "router.example.net", "photos", "로그.txt" })
                Assert.False(rendered.Html!.Contains(v, StringComparison.OrdinalIgnoreCase), "남은 값: " + v);
        }

        // 가리기를 끈 문서에서 켜면 적은 그대로
        var unmasked = Build(rec, new ReportOptions(Masked: false, IncludeMemos: true));
        Assert.True(unmasked.Options.Memos);
        Assert.Equal(memo, unmasked.Fields().Single(f => f.Field == "mark.memo").Value);
        Assert.Contains(unmasked.Texts(), r => r.Text == "증상 메모가 적은 그대로 들어 있습니다.");
        Assert.DoesNotContain(Build(rec, new ReportOptions(Masked: false)).Fields(), f => f.Field == "mark.memo");
    }

    [Fact]
    public void TheSafetyNetSeparatesGeneratedPhrasesFromRecordAndMemoText()
    {
        // 이름이 고정 문구의 낱말과 같은 기록: 생성 문구의 "공유기"는 통과, 메모에 남은 이름은 걸림
        var plainRouter = Router with { Name = "공유기" };
        var rec = Record(Rounds(Ok(120), Ok(120), Ok(120)), Routes().Append(Mark(5000, "x")), targets: [plainRouter, ExtA, ExtB]);
        var d = Build(rec, new ReportOptions(IncludeMemos: true));
        Assert.Contains(d.Texts(), r => r.Origin == TextOrigin.Generated && r.Text.Contains("공유기", StringComparison.Ordinal));
        Assert.Empty(d.Guard!.Check(d));

        var leaked = d with { Lead = [.. d.Lead, ReportRun.Memo("어제 공유기 재부팅")] };
        Assert.Equal(["대상 이름"], d.Guard.Check(leaked));
        var address = d with { Lead = [.. d.Lead, ReportRun.Gen("주소 198.51.100.7")] };   // 생성 문구라도 주소는 검사
        Assert.Equal(["대상 주소"], d.Guard.Check(address));
    }

    [Fact]
    public void ShortValuesMatchOnlyOnWordBoundaries()
    {
        Assert.True(Redactor.Contains("ab 쪽", "ab"));
        Assert.False(Redactor.Contains("cabin", "ab"));
        Assert.True(Redactor.Contains("ＡＢ", "ab"));          // 전각·대소문자 정리
        Assert.True(Redactor.Contains("xabcdx", "abcd"));      // 4글자부터는 부분 일치
        Assert.True(Redactor.Contains("  Example Telecom B ", " example telecom b"));
    }

    [Fact]
    public void TheSafetyNetChecksTitlesFigureTextAndAttributes()
    {
        var d = Build(Typical());
        Assert.Equal(["대상 주소"], d.Guard!.Check(d with { Title = "리포트 192.0.2.254" }));
        var figureSection = d.Sections.Single(s => s.Id == "timeline");
        var fig = (ReportFigure)figureSection.Blocks[0];
        var withDesc = d with { Sections = [.. d.Sections.Select(s => s.Id == "timeline" ? s with { Blocks = [new ReportFigure(fig.Figure with { Description = "예시 통신사 가나 회선" })] } : s)] };
        Assert.Equal(["사업자 이름"], d.Guard.Check(withDesc));
        var html = ReportHtml.Write(d).Replace("class=\"timeline\"", $"class=\"timeline\" data-x=\"{Tail}\"", StringComparison.Ordinal);
        Assert.Equal(["측정 ID"], d.Guard.CheckAttributes(html));
    }

    [Fact]
    public void ALeakStopsTheExportWithTheKindOnly()
    {
        var d = Build(Typical());
        var r = ReportExport.Render(d, ReportFault.Leak);
        Assert.False(r.Ok);
        Assert.Null(r.Html);
        Assert.Equal(["대상 주소"], r.LeakKinds);
        var unmasked = Build(Typical(), new ReportOptions(Masked: false));
        Assert.True(ReportExport.Render(unmasked).Ok);
    }

    [Fact]
    public void AVersionHoldingAFullWidthIdentityIsDropped()
    {
        // G4 M8 3라운드 a3: 사업자 이름 "１．０"(전각)은 숫자 버전 "1.0"과 같은 값으로 본다
        var rec = Typical();
        var h = rec.Header!;
        rec = rec with { Header = h with { AppVersion = "1.0+X", Targets = h.Targets.Select(t => t.Id == "isp-a1" ? t with { Provider = "１．０" } : t).ToArray() } };
        Assert.Equal("NetPulse 알 수 없음", Build(rec).Fields().Single(f => f.Field == "condition.appVersion").Value);
    }

    // ---------------- a4: 앱 버전 ----------------

    [Theory]
    [InlineData("1.0", "NetPulse 1.0")]
    [InlineData("0.5.0+abc", "NetPulse 0.5.0")]
    [InlineData("예시 통신사 가나", "NetPulse 알 수 없음")]
    [InlineData("1.0-isp-a1", "NetPulse 1.0")]
    [InlineData("1.0 C:\\x", "NetPulse 1.0")]
    // G4 M8 2라운드 a3: 숫자 버전만 쓰고, 그 안에 주소 같은 식별 값이 있으면 버린다
    [InlineData("192.0.2.254", "NetPulse 알 수 없음")]
    [InlineData("1.2.3.4.5", "NetPulse 알 수 없음")]
    [InlineData("+X", "NetPulse 알 수 없음")]
    public void TheRecordedAppVersionIsUsedOnlyWhenItLooksLikeAVersion(string version, string expected)
    {
        var rec = Typical();
        rec = rec with { Header = rec.Header! with { AppVersion = version } };
        Assert.Equal(expected, Build(rec).Fields().Single(f => f.Field == "condition.appVersion").Value);
    }
}
