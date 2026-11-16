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
    public void MemosAreLeftOutByDefaultAndPatternMaskedWhenIncluded()
    {
        // 경로는 구분 기호까지 가리므로 (G4 M8 1라운드 a1) 끝에 세미콜론으로 나눠 둔다
        string memo = "13:20:05에 끊김. 192.0.2.254 fe80::1%7 2001:db8::5 " + Mac + " me@example.org 010-9876-5432 "
            + @"https://router.example.net/admin 예시 통신사 가나 gw-home isp-a1 쪽 2026-11-13 13시; \\nas\photos; D:\문서\로그.txt";
        var rec = Typical(memo);
        var masked = Html(Build(rec));
        Assert.DoesNotContain("fe80", masked);
        Assert.DoesNotContain("13:20:05에", masked);

        var d = Build(rec, new ReportOptions(IncludeMemos: true));
        var text = d.Fields().Single(f => f.Field == "mark.memo").Value;
        foreach (var v in new[] { "192.0.2.254", "fe80::1", "2001:db8::5", Mac, "me@example.org", "010-9876-5432", @"\\nas", @"D:\", "router.example.net",
            "예시 통신사 가나", "gw-home", "isp-a1" })
            Assert.False(text.Contains(v, StringComparison.OrdinalIgnoreCase), "남은 값: " + v);
        Assert.StartsWith("13:20:05에 끊김.", text);                 // 시각은 남긴다 (콜론 둘)
        Assert.Contains("https://[가림]", text);
        Assert.Contains("2026-11-13 13시;", text);                   // 날짜는 전화번호 꼴이 아님
        Assert.Contains(d.Texts(), r => r.Text == ReportTexts.MemoNote);
        Assert.Empty(d.Guard!.Check(d));

        var unmasked = Build(rec, new ReportOptions(Masked: false, IncludeMemos: true));
        Assert.Equal(memo, unmasked.Fields().Single(f => f.Field == "mark.memo").Value);
    }

    [Fact]
    public void AMemoWithATargetIdThatDiffersFromNamesAndAddressesIsMasked()
    {
        var rec = Typical("isp-b2 쪽만 느림");
        var d = Build(rec, new ReportOptions(IncludeMemos: true));
        Assert.Equal("[가림] 쪽만 느림", d.Fields().Single(f => f.Field == "mark.memo").Value);
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

    // ---------------- G4 1라운드 a1: 메모 패턴의 빈틈 ----------------

    [Theory]
    [InlineData("192.0.2.23에 연결", "192.0.2.23")]
    [InlineData("공유기 주소는192.0.2.23입니다", "192.0.2.23")]
    [InlineData("::ffff:198.51.100.77 로 나감", "198.51.100.77")]
    [InlineData("링크https://router.example.net/x 확인", "router.example.net")]
    [InlineData(@"로그는 D:\Shared Data\Hong Gil-Dong\private.txt, 확인", "Gil-Dong")]
    [InlineData(@"\\nas box\사진 폴더\a.jpg; 끝", "사진 폴더")]
    [InlineData("내 메일은me@example.org입니다", "me@example.org")]
    // G4 M8 2라운드 a1: 문장 끝 점 앞 IPv6, 이름에 괄호·대괄호가 든 경로
    [InlineData("주소는 fe80::1.", "fe80::1")]
    [InlineData("끊긴 곳 2001:db8::1234. 다시", "db8::1234")]
    [InlineData(@"로그 D:\Home\Hong[work]\private.txt", "private.txt")]
    [InlineData(@"로그 D:\Home\Hong[work]\private.txt", "Hong")]
    [InlineData(@"\\nas\Shared (Sales)\private.txt", "private.txt")]
    [InlineData(@"\\nas\Shared (Sales)\private.txt", "Sales")]
    [InlineData(@"(D:\Data (2026)\a b.txt) 확인", "a b.txt")]
    // G4 M8 3라운드 a1·a2: 콜론·슬래시 뒤 전화번호, 이름에 쉼표·세미콜론이 든 경로 (따옴표 있음·없음)
    [InlineData("contact:010-9876-5432", "9876")]
    [InlineData("contact:+82-10-9876-5432", "9876")]
    [InlineData("010-9876-5432/02-3456-7890", "3456")]
    [InlineData("\"D:\\logs\\report;Alice Smith.txt\"", "Alice")]
    [InlineData("\"\\\\nas\\logs\\report,Alice Smith.txt\"", "Alice")]
    [InlineData(@"D:\logs\report;Alice Smith.txt 확인", "Alice")]
    [InlineData(@"\\nas\logs\report,Alice Smith.txt", "Alice")]
    // G4 M8 4라운드 a1·a2: 번호 안의 날짜 꼴, 날짜가 아닌 숫자 마디, 이름 안 "쉼표·세미콜론 + 공백", 이름 안 작은따옴표
    [InlineData("010-2026-11-13", "010-2026")]
    [InlineData("0800-12-34-56", "12-34")]
    [InlineData(@"D:\logs\report, Alice Smith.txt", "Alice")]
    [InlineData(@"\\nas\logs\report; Alice Smith.txt", "Alice")]
    [InlineData(@"D:\Home\O'Brien\x.txt", "Brien")]
    [InlineData(@"'D:\Home\O'Brien\x.txt' 확인", "Brien")]
    // G4 M8 5라운드 a1·a2: 공백으로 이어진 번호 안의 날짜 꼴, 슬래시 UNC (따옴표 있음·없음)
    [InlineData("010 2026-11-13", "010 2026")]
    [InlineData("02 2026-11-13", "02 2026")]
    [InlineData("+82 2026-11-13", "+82")]
    [InlineData("010 2026.11.13", "2026.11.13")]
    [InlineData("13:20:05 2026-11-13 010-1234-5678", "1234-5678")]
    [InlineData("//nas/logs/Alice Smith.txt", "Alice")]
    [InlineData("//nas/logs/Alice Smith.txt", "nas/logs")]
    [InlineData("\"//nas/logs/Alice Smith.txt\"", "Alice")]
    [InlineData("서버는 //nas-01 입니다", "nas-01")]
    public void MemoPatternsCatchValuesNextToKoreanTextAndPathsWithSpaces(string memo, string leaked)
    {
        var d = Build(Typical(memo), new ReportOptions(IncludeMemos: true));
        var text = d.Fields().Single(f => f.Field == "mark.memo").Value;
        Assert.DoesNotContain(leaked, text);
        Assert.Contains(ReportTexts.Masked, text);
        var rendered = ReportExport.Render(d);
        Assert.True(rendered.Ok, string.Join(", ", rendered.LeakKinds.Concat(rendered.Problems)));
        Assert.NotNull(rendered.Html);
        Assert.DoesNotContain(leaked, rendered.Html);
    }

    [Fact]
    public void AMacNextToKoreanTextIsMasked()
    {
        var memo = Mac + "에서 끊김";
        var text = Build(Typical(memo), new ReportOptions(IncludeMemos: true)).Fields().Single(f => f.Field == "mark.memo").Value;
        Assert.Equal("[가림]에서 끊김", text);
    }

    [Theory]
    [InlineData("13:20:05에 끊기고 2026-11-13 13시에 다시")]
    // G4 M8 2라운드 a2: 공백으로 붙은 시각·날짜를 전화번호로 합쳐 가리지 않는다
    [InlineData("13:20:05 2026-11-13")]
    [InlineData("2026-11-13 09:00 2026-11-14 09:00")]
    [InlineData("2026-01-05 08:00~2026-01-05 09:30 0.5초")]
    [InlineData("2026/11/13 09:00:15 2026.11.14 10:00")]
    // G4 M8 4라운드 a3: 메모에 사용자 영역 문자가 있어도 시각·날짜를 지킨다
    [InlineData("13:20:05 2026-11-13\uE000")]
    public void ClockTimesAndDatesStayInMemos(string memo)
    {
        var text = Build(Typical(memo), new ReportOptions(IncludeMemos: true)).Fields().Single(f => f.Field == "mark.memo").Value;
        Assert.Equal(memo, text);
    }

    [Theory]
    [InlineData("13:20 010-9876-5432 연락", "13:20 [가림] 연락")]
    [InlineData("2026-11-13 +82 10 9876 5432", "2026-11-13 [가림]")]
    [InlineData("13:20:05 010-9876-5432", "13:20:05 [가림]")]
    // G4 M8 5라운드 a1: 번호 앞뒤에 붙은 시각은 남기고, 번호에 붙은 날짜 꼴은 함께 가린다
    [InlineData("010-9876-5432 13:20 연락", "[가림] 13:20 연락")]
    [InlineData("13:20:05 010 2026-11-13", "13:20:05 [가림]")]
    [InlineData("문의:010-9876-5432, 2026-11-13", "문의:[가림], 2026-11-13")]
    public void PhoneNumbersNextToTimesAreStillMasked(string memo, string expected)
    {
        var text = Build(Typical(memo), new ReportOptions(IncludeMemos: true)).Fields().Single(f => f.Field == "mark.memo").Value;
        Assert.Equal(expected, text);
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

    [Fact]
    public void AUrlKeepsItsSchemeAndIsNotTakenForASlashUnc()
    {
        var text = Build(Typical("링크 https://router.example.net 확인"), new ReportOptions(IncludeMemos: true)).Fields().Single(f => f.Field == "mark.memo").Value;
        Assert.Equal("링크 https://[가림] 확인", text);
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
