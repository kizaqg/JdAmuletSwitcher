using System.Runtime.InteropServices;

namespace JdAmulet.Native;

[StructLayout(LayoutKind.Sequential)]
public struct KBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    // MOUSEINPUT — самый большой член union, задаёт правильный размер INPUT (40 байт на x64)
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public InputUnion u;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public int ptX;
    public int ptY;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
}

public delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

internal static unsafe class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string WinMm = "winmm.dll";

    // --- хуки клавиатуры ---
    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;
    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_UP = 0x80;

    // --- сообщения ---
    public const uint WM_QUIT = 0x0012;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const uint PM_NOREMOVE = 0x0000;

    // --- SendInput ---
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint MAPVK_VK_TO_VSC_EX = 4;

    // --- окна ---
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint GA_ROOTOWNER = 3;
    public const uint GW_OWNER = 4;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    // --- WinEvents ---
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    // --- процессы / таймеры ---
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint INFINITE = 0xFFFFFFFF;

    [DllImport(User32, SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [DllImport(User32, SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport(User32)]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport(User32)]
    public static extern int GetMessageW(out MSG msg, nint hwnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport(User32)]
    public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport(User32)]
    public static extern bool TranslateMessage(in MSG msg);

    [DllImport(User32)]
    public static extern nint DispatchMessageW(in MSG msg);

    [DllImport(User32, SetLastError = true)]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport(Kernel32)]
    public static extern uint GetCurrentThreadId();

    [DllImport(Kernel32, CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport(User32, SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [DllImport(User32, SetLastError = true)]
    public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport(User32)]
    public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport(User32)]
    public static extern nint GetForegroundWindow();

    [DllImport(User32)]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport(User32)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport(User32)]
    public static extern bool IsIconic(nint hwnd);

    [DllImport(User32)]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport(User32)]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport(User32)]
    public static extern nint GetWindow(nint hwnd, uint cmd);

    [DllImport(User32)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport(User32)]
    public static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport(User32)]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport(User32)]
    public static extern bool ClientToScreen(nint hwnd, ref POINT point);

    [DllImport(User32)]
    public static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport(User32)]
    public static extern bool ShowWindow(nint hwnd, int nCmdShow);

    [DllImport(User32)]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc callback, uint idProcess, uint idThread, uint flags);

    [DllImport(User32)]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport(Kernel32)]
    public static extern bool CloseHandle(nint handle);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(nint hProcess, uint flags, char* exeName, ref uint size);

    [DllImport(WinMm)]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport(WinMm)]
    public static extern uint timeEndPeriod(uint uPeriod);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWaitableTimerExW(nint lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool SetWaitableTimer(nint hTimer, in long dueTime, int period, nint completionRoutine, nint argToCompletionRoutine, bool resume);

    [DllImport(Kernel32)]
    public static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
