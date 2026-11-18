using System.Runtime.InteropServices;

namespace NetPulse.App.Resident;

/// <summary>트레이 메뉴 항목.</summary>
public sealed record TrayMenuItem(int Id, string Text, bool Enabled = true, bool IsDefault = false, bool Separator = false);

/// <summary>
/// 트레이 아이콘과 수신 창 (M9 설계 A3 [1-1, 1-10]).
/// <list type="bullet">
/// <item>수신 창: UI 스레드에서 만드는 숨은 최상위 창(<c>WS_POPUP</c>, <c>WS_EX_TOOLWINDOW</c>). 메시지 전용 창은 방송 메시지
/// (<c>TaskbarCreated</c>)를 받지 못해 쓰지 않는다. 트레이 콜백·<c>TaskbarCreated</c>·세션 종료 메시지를 받는다.</item>
/// <item>등록: <c>NIM_ADD</c>(<c>NIF_MESSAGE|NIF_ICON|NIF_TIP|NIF_SHOWTIP</c>, uID 1) → <c>NIM_SETVERSION</c>(v4). 탐색기가 다시 뜨면 다시.</item>
/// <item>콜백(v4): <c>LOWORD(lParam)</c> = 사건, <c>HIWORD(lParam)</c> = 아이콘 Id, <c>wParam</c> = 화면 좌표.</item>
/// <item>메뉴: <c>TrackPopupMenuEx(TPM_RETURNCMD)</c>로 고른 명령 Id를 받는다 (0 = 취소).</item>
/// </list>
/// 콜백은 UI 스레드에서 바로 불린다. 오래 걸리는 일은 받는 쪽이 디스패처로 넘긴다 (세션 종료만 예외, A6).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const string ClassName = "NetPulse.Tray";
    public const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;

    private readonly WndProc proc;   // 대리자를 붙잡는다 (GC가 거두면 창 프로시저가 사라진다)
    private readonly IntPtr hwnd;
    private readonly uint taskbarCreated;
    private IntPtr icon;
    private string tip = "NetPulse";
    private bool added;
    private bool disposed;

    public TrayIcon()
    {
        proc = WindowProc;
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref wc);   // 이미 등록돼 있으면 실패해도 같은 클래스를 쓴다 (프로세스에 하나)
        hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, $"{ClassName}.{Environment.ProcessId}", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        icon = LoadAppIcon();
    }

    /// <summary>수신 창 핸들 (시험이 찾는 창).</summary>
    public IntPtr Handle => hwnd;

    /// <summary>아이콘이 지금 알림 영역에 있는가.</summary>
    public bool Added => added;

    /// <summary>왼쪽 클릭·키보드 선택 (열기).</summary>
    public event Action? Selected;
    /// <summary>오른쪽 클릭·메뉴 키 (화면 좌표).</summary>
    public event Action<int, int>? MenuRequested;
    public event Action? BalloonClicked;
    /// <summary>탐색기가 다시 떴고 다시 넣기 결과 (true = 성공).</summary>
    public event Action<bool>? TaskbarRestarted;
    public event Action? QueryEndSession;
    /// <summary>세션 종료 확정(true)·취소(false). 받는 쪽은 이 호출 안에서 끝내야 한다 (A6).</summary>
    public event Action<bool>? EndSession;

    /// <summary>아이콘을 넣는다. 실패하면 false (트레이 사용 불가).</summary>
    public bool Add(string tooltip)
    {
        tip = Trim(tooltip);
        if (hwnd == IntPtr.Zero || icon == IntPtr.Zero) return false;
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        added = Shell_NotifyIconW(NIM_ADD, ref d);
        if (!added) return false;
        var v = Data(0);
        v.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref v);
        return true;
    }

    public void SetTooltip(string text)
    {
        text = Trim(text);
        if (text == tip) return;
        tip = text;
        if (!added) return;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    public string Tooltip => tip;

    /// <summary>풍선 (호출 성공 여부만 안다, 실제 표시는 Windows가 정한다).</summary>
    public bool ShowBalloon(string title, string body, bool warning)
    {
        if (!added) return false;
        var d = Data(NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = body.Length > 255 ? body[..255] : body;
        d.dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME;
        return Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    /// <summary>메뉴를 띄우고 고른 명령 Id (0 = 취소).</summary>
    public int ShowMenu(int x, int y, IReadOnlyList<TrayMenuItem> items)
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var i in items)
            {
                if (i.Separator) { AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null); continue; }
                AppendMenuW(menu, MF_STRING | (i.Enabled ? 0u : MF_GRAYED), (UIntPtr)(uint)i.Id, i.Text);
                if (i.IsDefault) SetMenuDefaultItem(menu, (uint)i.Id, 0);
            }
            SetForegroundWindow(hwnd);   // 메뉴 밖을 누르면 닫히게 (알려진 Win32 메뉴 규칙)
            uint align = GetSystemMetrics(Metric.MenuDropAlignment) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | align, x, y, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }
        finally { DestroyMenu(menu); }
    }

    public void Remove()
    {
        if (!added) return;
        var d = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref d);
        added = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Remove();
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        icon = IntPtr.Zero;
    }

    private IntPtr WindowProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            uint ev = (uint)((long)lParam & 0xFFFF);
            uint id = (uint)(((long)lParam >> 16) & 0xFFFF);
            if (id != IconId) return IntPtr.Zero;
            int x = (short)((long)wParam & 0xFFFF), y = (short)(((long)wParam >> 16) & 0xFFFF);
            switch (ev)
            {
                case NIN_SELECT or NIN_KEYSELECT: Selected?.Invoke(); break;
                case WM_CONTEXTMENU: MenuRequested?.Invoke(x, y); break;
                case NIN_BALLOONUSERCLICK: BalloonClicked?.Invoke(); break;
            }
            return IntPtr.Zero;
        }
        if (msg == taskbarCreated && taskbarCreated != 0)
        {
            added = false;
            TaskbarRestarted?.Invoke(Add(tip));
            return IntPtr.Zero;
        }
        if (msg == WM_QUERYENDSESSION)
        {
            QueryEndSession?.Invoke();
            return (IntPtr)1;   // 거부하지 않는다
        }
        if (msg == WM_ENDSESSION)
        {
            EndSession?.Invoke(wParam != IntPtr.Zero);
            return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wParam, lParam);
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tip,
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Trim(string s) => s.Length > 127 ? s[..127] : s;

    /// <summary>실행 파일의 앱 아이콘(리소스 32512)을 작은 아이콘 크기로. 없으면 옆의 Assets\NetPulse.ico.</summary>
    private static IntPtr LoadAppIcon()
    {
        int cx = GetSystemMetrics(Metric.CxSmallIcon), cy = GetSystemMetrics(Metric.CySmallIcon);
        var module = GetModuleHandleW(null);
        try { if (LoadIconWithScaleDown(module, (IntPtr)32512, cx, cy, out var h) == 0 && h != IntPtr.Zero) return h; }
        catch (EntryPointNotFoundException) { }   // 공용 컨트롤 6판이 아닌 경우
        var res = LoadImageW(module, (IntPtr)32512, IMAGE_ICON, cx, cy, 0);
        if (res != IntPtr.Zero) return res;
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "NetPulse.ico");
        return File.Exists(file) ? LoadImageW(IntPtr.Zero, file, IMAGE_ICON, cx, cy, LR_LOADFROMFILE) : IntPtr.Zero;
    }

    // ---------------- Win32 ----------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NULL = 0x0000, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_CONTEXTMENU = 0x007B, WM_APP = 0x8000;
    private const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_INFO = 0x01, NIIF_WARNING = 0x02, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_SEPARATOR = 0x800;
    private const uint TPM_LEFTALIGN = 0x0, TPM_RIGHTALIGN = 0x8, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    private enum Metric { MenuDropAlignment = 40, CxSmallIcon = 49, CySmallIcon = 50 }
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(Metric index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);
    [DllImport("comctl32.dll")] private static extern int LoadIconWithScaleDown(IntPtr instance, IntPtr name, int cx, int cy, out IntPtr icon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
