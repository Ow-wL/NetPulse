using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NetPulse.Core.Evidence;

namespace NetPulse.Platform;

/// <summary>
/// wlanapi 결과 해석 (M12 설계 C 읽기 [1-2]). 하드웨어 없이 시험할 수 있게 순수 함수로 둔다.
/// 구조체 배치는 Windows SDK 10.0.26100 wlanapi.h의 WLAN_REALTIME_CONNECTION_QUALITY를 따른다 (x64·x86 공통, 모두 4바이트 정렬 필드):
/// 0 dot11PhyType, 4 ulLinkQuality(0~100), 8 ulRxRate, 12 ulTxRate, 16 bIsMLOConnection, 20 ulNumLinks,
/// 24 linksInfo[0] = ucLinkID(1 + 채움 3), +4 ulChannelCenterFrequencyMhz, +8 ulBandwidth, +12 lRssi(dBm), +16 WLAN_RATE_SET.
/// 그래서 링크가 하나 이상이면 첫 링크의 RSSI는 36에 있다.
/// </summary>
public static class WlanSignal
{
    public const uint OpcodeRealtimeConnectionQuality = 19;      // wlan_intf_opcode_realtime_connection_quality
    public const uint OpcodeRssi = 0x10000102;                    // wlan_intf_opcode_rssi
    public const string MethodRealtime = "realtime", MethodRssi = "rssi";
    private const int OffQuality = 4, OffNumLinks = 20, OffFirstRssi = 36, HeaderSize = 24;

    /// <summary>오류 코드의 뜻. Fallback은 그 opcode를 OS·드라이버가 모르는 경우 (다음 방법을 쓴다).</summary>
    public enum ErrorKind { Ok, Denied, NotConnected, Fallback, Unsupported, Failed }

    /// <summary>
    /// Win32 오류 → 뜻 (M12 설계 C). 0 정상, 5 접근 거부 → 권한 없음, 5023 ERROR_INVALID_STATE → 연결 안 됨,
    /// 1·50·87(잘못된 함수·지원 안 함·잘못된 인수: 옛 OS가 opcode를 모름) → 다음 방법, 1062 서비스 꺼짐 → 지원 안 함,
    /// 2·1168(찾지 못함) → 무선 인터페이스가 아님(지원 안 함), 그 밖 → 읽기 실패 "wlan_n".
    /// </summary>
    public static (ErrorKind Kind, string? Code) Classify(uint error) => error switch
    {
        0 => (ErrorKind.Ok, null),
        5 => (ErrorKind.Denied, "access-denied"),
        5023 => (ErrorKind.NotConnected, "not-connected"),
        1 or 50 or 87 => (ErrorKind.Fallback, "opcode-unsupported"),
        1062 => (ErrorKind.Unsupported, "wlan-service-off"),
        2 or 1168 => (ErrorKind.Unsupported, "not-wlan-interface"),
        _ => (ErrorKind.Failed, "wlan_" + error),
    };

    /// <summary>
    /// 마지막으로 시도한 방법의 오류를 읽기 결과로. 더 쓸 방법이 없으므로 Fallback(opcode를 모름)은 "지원 안 함"("no-signal-opcode")이다 (설계 C 읽는 순서 3).
    /// </summary>
    public static SignalReading FromError(uint error)
    {
        var (kind, code) = Classify(error);
        return kind switch
        {
            ErrorKind.Denied => new SignalReading(SignalState.Denied, null, null, code, null),
            ErrorKind.NotConnected => new SignalReading(SignalState.NotConnected, null, null, code, null),
            ErrorKind.Unsupported => new SignalReading(SignalState.Unsupported, null, null, code, null),
            ErrorKind.Fallback => new SignalReading(SignalState.Unsupported, null, null, "no-signal-opcode", null),
            ErrorKind.Ok => new SignalReading(SignalState.Failed, null, null, "no-data", null),
            _ => new SignalReading(SignalState.Failed, null, null, code, null),
        };
    }

    /// <summary>
    /// WLAN_REALTIME_CONNECTION_QUALITY 바이트 → 읽기 결과. 품질은 0~100만 값으로 본다(0도 값). RSSI는 링크가 하나 이상이고
    /// 버퍼가 그 필드까지 있을 때만 읽는다(-127~0 dBm). 둘 다 없거나 머리보다 짧으면 읽기 실패.
    /// </summary>
    public static SignalReading ParseRealtime(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) return new SignalReading(SignalState.Failed, null, null, "realtime-short", MethodRealtime);
        uint quality = BinaryPrimitives.ReadUInt32LittleEndian(data[OffQuality..]);
        uint links = BinaryPrimitives.ReadUInt32LittleEndian(data[OffNumLinks..]);
        int? q = quality <= 100 ? (int)quality : null;
        int? rssi = null;
        if (links >= 1 && data.Length >= OffFirstRssi + 4)
        {
            int r = BinaryPrimitives.ReadInt32LittleEndian(data[OffFirstRssi..]);
            if (r is >= SignalReading.MinRssi and <= SignalReading.MaxRssi) rssi = r;
        }
        if (q is null && rssi is null) return new SignalReading(SignalState.Failed, null, null, "realtime-no-value", MethodRealtime);
        return new SignalReading(SignalState.Ok, q, rssi, null, MethodRealtime);
    }

    /// <summary>wlan_intf_opcode_rssi 결과(LONG dBm) → 읽기 결과.</summary>
    public static SignalReading ParseRssi(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return new SignalReading(SignalState.Failed, null, null, "rssi-short", MethodRssi);
        int r = BinaryPrimitives.ReadInt32LittleEndian(data);
        return r is >= SignalReading.MinRssi and <= SignalReading.MaxRssi
            ? new SignalReading(SignalState.Ok, null, r, null, MethodRssi)
            : new SignalReading(SignalState.Failed, null, null, "rssi-no-value", MethodRssi);
    }

    /// <summary>어댑터 Key("{GUID}")를 wlanapi 인터페이스 GUID로. GUID가 아니면(번호로만 아는 어댑터) null.</summary>
    public static Guid? InterfaceGuid(AdapterInfo adapter) => Guid.TryParse(adapter.Key, out var g) ? g : null;
}

/// <summary>
/// Wi‑Fi 신호 읽기 (M12 설계 C 읽기). 위치 권한이 필요 없는 호출만 쓴다:
/// WlanOpenHandle → WlanQueryInterface(실시간 연결 품질, 모르면 RSSI) → WlanFreeMemory, 끝에 WlanCloseHandle.
/// <c>wlan_intf_opcode_current_connection</c>·네트워크 목록·BSS 목록·스캔은 부르지 않는다 (위치 안내를 띄우고 SSID·BSSID를 준다).
/// 핸들은 처음 읽을 때 한 번 열고 Dispose에서 닫는다. 실시간 opcode를 모르는 인터페이스는 기억해 다음부터 RSSI만 부른다.
/// 엔진 회차 한 곳에서 부르지만, 닫기와 겹치지 않게 잠금 안에서 읽는다.
/// </summary>
public sealed class WlanSignalReader : ISignalSource
{
    private const uint ClientVersion = 2;   // Windows Vista 이후 API
    private readonly object gate = new();
    private readonly HashSet<Guid> rssiOnly = [];
    private IntPtr handle;
    private bool opened, disposed;
    private uint openError;

    public SignalReading Read(AdapterInfo adapter)
    {
        if (WlanSignal.InterfaceGuid(adapter) is not Guid guid) return new SignalReading(SignalState.Unsupported, null, null, "not-wlan-interface", null);
        lock (gate)
        {
            if (disposed) return new SignalReading(SignalState.Failed, null, null, "closed", null);
            if (!opened)
            {
                opened = true;
                try { openError = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { openError = 1062; }   // wlanapi가 없는 판 = 무선 서비스 없음
                if (openError != 0) handle = IntPtr.Zero;
            }
            if (openError != 0) return WlanSignal.FromError(openError);

            if (!rssiOnly.Contains(guid))
            {
                var (code, bytes) = Query(guid, WlanSignal.OpcodeRealtimeConnectionQuality);
                if (code == 0) return WlanSignal.ParseRealtime(bytes);
                if (WlanSignal.Classify(code).Kind != WlanSignal.ErrorKind.Fallback) return WlanSignal.FromError(code);
                rssiOnly.Add(guid);   // 이 OS·드라이버는 실시간 opcode를 모른다
            }
            var (rssiCode, rssiBytes) = Query(guid, WlanSignal.OpcodeRssi);
            return rssiCode == 0 ? WlanSignal.ParseRssi(rssiBytes) : WlanSignal.FromError(rssiCode);
        }
    }

    /// <summary>잠금 안: opcode 하나를 묻고 결과 바이트를 복사한 뒤 메모리를 돌려준다.</summary>
    private (uint Code, byte[] Bytes) Query(Guid guid, uint opcode)
    {
        uint result = WlanQueryInterface(handle, ref guid, opcode, IntPtr.Zero, out uint size, out IntPtr data, out _);
        try
        {
            if (result != 0) return (result, []);
            if (data == IntPtr.Zero) return (0, []);
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            return (0, bytes);
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
            handle = IntPtr.Zero;
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, uint opcode, IntPtr reserved,
        out uint dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
}
