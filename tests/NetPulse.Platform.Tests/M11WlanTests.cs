using NetPulse.Core.Evidence;

namespace NetPulse.Platform.Tests;

/// <summary>
/// 무선 링크 근거 해석 (M11 설계 B3 계약). 실제 Wi‑Fi 로그 없이 이벤트 XML·어댑터 목록을 가짜로 넣어
/// <see cref="LinkLogReader"/>가 쓰는 해석 함수가 지금 동작 그대로인지 본다. 하드웨어 실측은 인수 항목 8.
/// 쓰는 GUID는 시험용으로 만든 값이다 (이 PC의 어댑터가 아니다).
/// </summary>
public sealed class M11WlanTests
{
    private const string WifiKey = "{0f8e2c1a-1b2c-4d3e-8f90-123456789abc}";
    private const string WiredKey = "{aa11bb22-cc33-4d44-8e55-66778899aabb}";
    private static readonly IReadOnlyList<AdapterInfo> Adapters = [new(WifiKey, 7, "wireless", false), new(WiredKey, 3, "wired", false)];
    private static readonly DateTimeOffset At = new(2026, 11, 13, 4, 20, 0, TimeSpan.Zero);

    private static string EventXml(string? interfaceGuid, bool includeData = true) =>
        "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>8001</EventID></System><EventData>"
        + "<Data Name=\"Adapter\">시험 무선 어댑터</Data>"
        + (includeData ? $"<Data Name=\"InterfaceGuid\">{interfaceGuid}</Data>" : "")
        + "<Data Name=\"SSID\">시험망</Data></EventData></Event>";

    [Theory]
    [InlineData(8001, "up")]
    [InlineData(8003, "down")]
    public void MatchingGuidIsALinkedEventForThatAdapter(int id, string state)
    {
        // 이벤트 XML의 GUID는 대소문자·중괄호가 달라도 같은 GUID로 본다
        var guid = LinkCatalog.EventData(EventXml("{0F8E2C1A-1B2C-4D3E-8F90-123456789ABC}"), "InterfaceGuid");
        var e = LinkCatalog.ParseWlan(id, guid, At, Adapters);
        Assert.NotNull(e);
        Assert.Equal(WifiKey, e.AdapterKey);
        Assert.Equal(LinkMatch.Linked, e.Match);
        Assert.Equal(state, LinkEvidenceBuilder.Text(e.State));
        Assert.Equal(At, e.Wall);
    }

    [Theory]
    [InlineData("{11111111-2222-4333-8444-555555555555}")]   // 측정 어댑터가 아닌 GUID
    [InlineData("무선 어댑터")]                                // GUID가 아닌 글
    [InlineData("")]                                           // 빈 글 (데이터는 있음)
    public void PresentButUnmatchedValueIsAnUnlinkedEvent(string value)
    {
        var guid = LinkCatalog.EventData(EventXml(value), "InterfaceGuid");
        Assert.Equal(value, guid);
        var e = LinkCatalog.ParseWlan(8003, guid, At, Adapters);
        Assert.NotNull(e);
        Assert.Null(e.AdapterKey);
        Assert.Equal(LinkMatch.Unlinked, e.Match);
        Assert.Equal(LinkState.Down, e.State);
    }

    [Fact]
    public void MissingDataUnreadableXmlOrUnknownIdIsNotParsed()
    {
        Assert.Null(LinkCatalog.EventData(EventXml(null, includeData: false), "InterfaceGuid"));   // 그 이름의 데이터 없음
        Assert.Null(LinkCatalog.EventData("<Event><EventData>", "InterfaceGuid"));                  // XML을 읽지 못함
        Assert.Null(LinkCatalog.ParseWlan(8001, null, At, Adapters));
        Assert.Null(LinkCatalog.ParseWlan(8002, WifiKey, At, Adapters));                            // 해석표에 없는 Id
        Assert.Null(LinkCatalog.ParseWlan(27, WifiKey, At, Adapters));                              // 유선 드라이버 Id는 무선 해석이 아니다
    }

    [Fact]
    public void WirelessSupportFollowsWhetherTheWlanLogIsReadable()
    {
        Assert.Equal((LinkCapability.Supported, (string?)null), LinkCatalog.WirelessSupport(true));
        Assert.Equal((LinkCapability.Unsupported, (string?)"wlan-log-unavailable"), LinkCatalog.WirelessSupport(false));   // 권한 예외도 여기로 합쳐진다
    }

    [Fact]
    public void WlanQueryAsksOnlyForConnectAndDisconnectInTheWindow()
    {
        var q = LinkCatalog.WlanQuery(At, At.AddMinutes(5));
        Assert.Contains("EventID=8003", q, StringComparison.Ordinal);
        Assert.Contains("EventID=8001", q, StringComparison.Ordinal);
        Assert.Contains("@SystemTime>='2026-11-13T04:20:00.000Z'", q, StringComparison.Ordinal);
        Assert.Contains("@SystemTime<='2026-11-13T04:25:00.000Z'", q, StringComparison.Ordinal);
    }

    [Fact]
    public void GuidComparisonNeedsTwoValidGuids()
    {
        Assert.True(LinkCatalog.SameGuid(WifiKey, "0f8e2c1a-1b2c-4d3e-8f90-123456789abc"));
        Assert.False(LinkCatalog.SameGuid(WifiKey, WiredKey));
        Assert.False(LinkCatalog.SameGuid("어댑터", "어댑터"));          // 같은 글이라도 GUID가 아니면 같다고 하지 않는다
    }
}
