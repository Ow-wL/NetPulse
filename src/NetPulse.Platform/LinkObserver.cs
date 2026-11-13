using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NetPulse.Core.Evidence;

namespace NetPulse.Platform;

/// <summary>
/// 인터페이스 한 개의 상태 읽기 (GetIfEntry2, MIB_IF_ROW2). 구조체 전체를 정의하지 않고 필요한 필드만 정해진 위치에서 읽는다
/// (x64·x86 공통 배치, 크기 1352바이트). 위치는 통합 시험이 루프백·실제 어댑터로 확인한다.
/// </summary>
public static class IfEntry
{
    public const int RowSize = 1352;
    private const int OffIndex = 8, OffGuid = 12, OffDescription = 542, OffType = 1128, OffFlags = 1152, OffOper = 1156, OffMedia = 1164;
    private const uint ErrorFileNotFound = 2, ErrorAccessDenied = 5, ErrorNotFound = 1168;

    public sealed record Row(int Index, Guid Guid, string Description, int IfType, bool Hardware, int OperStatus, int MediaConnectState);

    /// <summary>읽는다. 결과 코드 0이 아니면 Row는 null.</summary>
    public static (Row? Row, uint Code) Read(int index)
    {
        var buffer = new byte[RowSize];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(OffIndex), index);
        uint result = GetIfEntry2(buffer);
        if (result != 0) return (null, result);
        ReadOnlySpan<byte> span = buffer;
        var guid = new Guid(span.Slice(OffGuid, 16));
        var desc = MemoryMarshal.Cast<byte, char>(span.Slice(OffDescription, 514));
        int len = desc.IndexOf((char)0);
        return (new Row(BinaryPrimitives.ReadInt32LittleEndian(span[OffIndex..]), guid, new string(len < 0 ? desc : desc[..len]),
            BinaryPrimitives.ReadInt32LittleEndian(span[OffType..]), (span[OffFlags] & 1) != 0,
            BinaryPrimitives.ReadInt32LittleEndian(span[OffOper..]), BinaryPrimitives.ReadInt32LittleEndian(span[OffMedia..])), 0);
    }

    /// <summary>상태 해석 (M4 설계 A2): MediaConnectState Disconnected(2) 또는 OperStatus Down(2)·NotPresent(6)·LowerLayerDown(7) → down, Connected(1) + Up(1) → up.</summary>
    public static LinkState StateOf(Row row)
    {
        if (row.MediaConnectState == 2 || row.OperStatus is 2 or 6 or 7) return LinkState.Down;
        if (row.MediaConnectState == 1 && row.OperStatus == 1) return LinkState.Up;
        if (row.IfType == 24 && row.OperStatus == 1) return LinkState.Up;   // 루프백은 매체 상태가 없다
        return LinkState.Unknown;
    }

    public static string KindOf(Row row) => row.IfType switch
    {
        6 => "wired", 71 => "wireless", 131 or 23 => "tunnel", 24 => "loopback", _ => "other",
    };

    /// <summary>가상 여부: 하드웨어 인터페이스가 아니거나 터널·PPP.</summary>
    public static bool VirtualOf(Row row) => !row.Hardware || row.IfType is 131 or 23;

    public static AdapterInfo InfoOf(Row row) => new(row.Guid.ToString("B").ToUpperInvariant(), row.Index, KindOf(row), VirtualOf(row));

    public static string CodeOf(uint code) => code switch
    {
        ErrorFileNotFound or ErrorNotFound => "not-found", ErrorAccessDenied => "access-denied", _ => "GetIfEntry2_" + code,
    };

    public static bool IsGone(uint code) => code is ErrorFileNotFound or ErrorNotFound;
    public static bool IsDenied(uint code) => code == ErrorAccessDenied;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfEntry2([In, Out] byte[] row);
}

/// <summary>
/// 인터페이스 상태 직접 관측 (M4 설계 A2): NotifyIpInterfaceChange 알림 + 회차마다 GetIfEntry2.
/// 읽은 결과는 상태 표본이다 (알림 처리 전에 끝난 다운·업은 못 볼 수 있음, 로그가 보완).
/// 콜백은 읽기와 sink 넣기만 한다. Dispose 뒤 도착한 콜백은 버린다.
/// </summary>
public sealed class LinkObserver : ILinkObserver
{
    private readonly object gate = new();
    private readonly HashSet<int> watched = [];
    private readonly Dictionary<string, AdapterInfo> adapters = [];
    private Action<LinkSample>? sink;
    private CallbackDelegate? callback;   // 등록이 끝날 때까지 살아 있어야 한다
    private IntPtr handle;
    private volatile bool disposed;

    public IReadOnlyList<AdapterInfo> Adapters { get { lock (gate) return adapters.Values.ToArray(); } }

    public (LinkCapability Capability, string? Code) Start(Action<LinkSample> sink)
    {
        this.sink = sink;
        callback = OnChange;
        uint result = NotifyIpInterfaceChange(2 /* AF_INET */, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, false, out handle);
        if (result == 0) return (LinkCapability.Supported, null);
        handle = IntPtr.Zero;
        return result == 5 ? (LinkCapability.Denied, "access-denied") : (LinkCapability.Unsupported, "NotifyIpInterfaceChange_" + result);
    }

    public void Watch(IReadOnlyCollection<int> interfaceIndexes)
    {
        lock (gate) foreach (var i in interfaceIndexes) watched.Add(i);
    }

    public void Poll()
    {
        int[] now;
        lock (gate) now = watched.ToArray();
        foreach (var index in now) Emit(index, fromPoll: true);
    }

    private void OnChange(IntPtr context, IntPtr row, int type)
    {
        if (disposed) return;
        try
        {
            if (row == IntPtr.Zero) { Poll(); return; }   // 어느 인터페이스인지 모르면 모두 읽는다
            int index = Marshal.ReadInt32(row, 16);   // MIB_IPINTERFACE_ROW.InterfaceIndex
            bool isWatched;
            lock (gate) isWatched = watched.Contains(index);
            if (isWatched) Emit(index, fromPoll: false);
        }
        catch (Exception) { /* 시스템 콜백을 깨지 않는다 */ }
    }

    private void Emit(int index, bool fromPoll)
    {
        if (disposed || sink is not { } s) return;
        var (row, code) = IfEntry.Read(index);
        AdapterInfo info;
        LinkState state;
        string? reason = null;
        if (row is not null)
        {
            info = IfEntry.InfoOf(row);
            state = IfEntry.StateOf(row);
            lock (gate) adapters[info.Key] = info;
        }
        else
        {
            lock (gate) info = adapters.Values.FirstOrDefault(a => a.InterfaceIndex == index) ?? new AdapterInfo("index:" + index, index, "other", false);
            state = IfEntry.IsGone(code) ? LinkState.Removed : LinkState.Unknown;
            reason = IfEntry.CodeOf(code);
            if (IfEntry.IsGone(code)) lock (gate) watched.Remove(index);   // 사라진 번호는 더 읽지 않는다 (다시 경로에 나오면 다시 감시)
        }
        s(new LinkSample(info, state, fromPoll, reason));
    }

    public void Dispose()
    {
        disposed = true;
        var h = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (h != IntPtr.Zero) CancelMibChangeNotify2(h);   // 진행 중인 콜백이 끝날 때까지 기다린다
        GC.KeepAlive(callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CallbackDelegate(IntPtr context, IntPtr row, int type);

    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyIpInterfaceChange(ushort family, IntPtr callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr handle);

    [DllImport("iphlpapi.dll")]
    private static extern uint CancelMibChangeNotify2(IntPtr handle);
}
