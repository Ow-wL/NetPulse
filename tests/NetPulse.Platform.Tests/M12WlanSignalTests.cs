using System.Buffers.Binary;
using NetPulse.Core.Evidence;

namespace NetPulse.Platform.Tests;

/// <summary>
/// Wi‑Fi 신호 읽기 해석 (M12 설계 C 읽기 [1-2]). 무선 하드웨어 없이 오류 코드·결과 바이트를 시험에서 만들어
/// <see cref="WlanSignal"/>가 상태·값으로 바르게 바꾸는지 본다. 노트북 실측(위치 안내가 뜨지 않는지 포함)은 인수 항목.
/// 쓰는 GUID는 시험용으로 만든 값이다 (이 PC의 어댑터가 아니다).
/// </summary>
public sealed class M12WlanSignalTests
{
    private const string WifiKey = "{0F8E2C1A-1B2C-4D3E-8F90-123456789ABC}";

    [Theory]
    [InlineData(0u, WlanSignal.ErrorKind.Ok, null)]
    [InlineData(5u, WlanSignal.ErrorKind.Denied, "access-denied")]
    [InlineData(5023u, WlanSignal.ErrorKind.NotConnected, "not-connected")]
    [InlineData(1u, WlanSignal.ErrorKind.Fallback, "opcode-unsupported")]
    [InlineData(50u, WlanSignal.ErrorKind.Fallback, "opcode-unsupported")]
    [InlineData(87u, WlanSignal.ErrorKind.Fallback, "opcode-unsupported")]
    [InlineData(1062u, WlanSignal.ErrorKind.Unsupported, "wlan-service-off")]
    [InlineData(2u, WlanSignal.ErrorKind.Unsupported, "not-wlan-interface")]
    [InlineData(1168u, WlanSignal.ErrorKind.Unsupported, "not-wlan-interface")]
    [InlineData(1234u, WlanSignal.ErrorKind.Failed, "wlan_1234")]
    public void Win32ErrorsMapToSignalMeaning(uint error, WlanSignal.ErrorKind kind, string? code) =>
        Assert.Equal((kind, code), WlanSignal.Classify(error));

    [Fact]
    public void LastMethodErrorsBecomeStates()
    {
        Assert.Equal(new SignalReading(SignalState.Denied, null, null, "access-denied", null), WlanSignal.FromError(5));
        Assert.Equal(new SignalReading(SignalState.NotConnected, null, null, "not-connected", null), WlanSignal.FromError(5023));
        // 두 방법 모두 opcode를 모르면 "지원 안 함" (설계 C 읽는 순서 3)
        Assert.Equal(new SignalReading(SignalState.Unsupported, null, null, "no-signal-opcode", null), WlanSignal.FromError(87));
        Assert.Equal(new SignalReading(SignalState.Unsupported, null, null, "wlan-service-off", null), WlanSignal.FromError(1062));
        Assert.Equal(new SignalReading(SignalState.Failed, null, null, "wlan_1234", null), WlanSignal.FromError(1234));
    }

    /// <summary>WLAN_REALTIME_CONNECTION_QUALITY 배치대로 만든 버퍼 (링크 정보 하나 = 16 + 4 + 126×2 바이트).</summary>
    private static byte[] Realtime(uint quality, uint links, int rssi, int length = 24 + 272)
    {
        var b = new byte[length];
        void Put(int offset, uint v) { if (offset + 4 <= length) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), v); }
        Put(0, 4);              // dot11PhyType (아무 값)
        Put(4, quality);        // ulLinkQuality
        Put(8, 866_000);        // ulRxRate
        Put(12, 650_000);       // ulTxRate
        Put(16, 0);             // bIsMLOConnection
        Put(20, links);         // ulNumLinks
        if (length > 24) b[24] = 1;   // ucLinkID
        Put(28, 5180);          // ulChannelCenterFrequencyMhz
        Put(32, 80);            // ulBandwidth
        Put(36, unchecked((uint)rssi));   // lRssi
        return b;
    }

    [Fact]
    public void RealtimeQualityAndFirstLinkRssiAreRead()
    {
        Assert.Equal(new SignalReading(SignalState.Ok, 72, -58, null, "realtime"), WlanSignal.ParseRealtime(Realtime(72, 1, -58)));
        // 품질 0%는 값이다
        Assert.Equal(new SignalReading(SignalState.Ok, 0, -91, null, "realtime"), WlanSignal.ParseRealtime(Realtime(0, 1, -91)));
    }

    [Fact]
    public void MissingLinksOrShortBuffersDropOnlyWhatIsMissing()
    {
        Assert.Equal(new SignalReading(SignalState.Ok, 55, null, null, "realtime"), WlanSignal.ParseRealtime(Realtime(55, 0, -60)));          // 링크 정보 없음
        Assert.Equal(new SignalReading(SignalState.Ok, 55, null, null, "realtime"), WlanSignal.ParseRealtime(Realtime(55, 1, -60, 36)));      // RSSI 필드 전에 끝남
        Assert.Equal(new SignalReading(SignalState.Ok, null, -60, null, "realtime"), WlanSignal.ParseRealtime(Realtime(255, 1, -60)));       // 품질 범위 밖
        Assert.Equal(new SignalReading(SignalState.Ok, 55, null, null, "realtime"), WlanSignal.ParseRealtime(Realtime(55, 1, 20)));          // RSSI 범위 밖
        Assert.Equal(SignalState.Failed, WlanSignal.ParseRealtime(Realtime(255, 0, 0)).State);                                               // 값이 하나도 없음
        Assert.Equal(new SignalReading(SignalState.Failed, null, null, "realtime-short", "realtime"), WlanSignal.ParseRealtime(new byte[20]));
    }

    [Fact]
    public void RssiOpcodeResultIsASignedDbmValue()
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, -72);
        Assert.Equal(new SignalReading(SignalState.Ok, null, -72, null, "rssi"), WlanSignal.ParseRssi(b));
        BinaryPrimitives.WriteInt32LittleEndian(b, 3);
        Assert.Equal(SignalState.Failed, WlanSignal.ParseRssi(b).State);
        Assert.Equal(SignalState.Failed, WlanSignal.ParseRssi(new byte[2]).State);
    }

    [Fact]
    public void AdapterKeyIsTheInterfaceGuid()
    {
        Assert.Equal(Guid.Parse(WifiKey), WlanSignal.InterfaceGuid(new AdapterInfo(WifiKey, 7, "wireless", false)));
        Assert.Null(WlanSignal.InterfaceGuid(new AdapterInfo("index:7", 7, "wireless", false)));   // 번호로만 아는 어댑터
    }

    [Fact]
    public void ReaderAnswersWithoutNativeCallsForNonGuidKeysOrAfterDispose()
    {
        // 하드웨어와 무관한 경로만: GUID가 아닌 Key, 닫은 뒤 읽기
        var reader = new WlanSignalReader();
        Assert.Equal(new SignalReading(SignalState.Unsupported, null, null, "not-wlan-interface", null), reader.Read(new AdapterInfo("index:7", 7, "wireless", false)));
        reader.Dispose();
        reader.Dispose();   // 두 번 닫아도 된다
        Assert.Equal(new SignalReading(SignalState.Failed, null, null, "closed", null), reader.Read(new AdapterInfo(WifiKey, 7, "wireless", false)));
    }
}
