using System.Diagnostics;
using System.Runtime.InteropServices;
using JdAmulet.Native;

namespace JdAmulet.Input;

public interface ILowLevelKeyHandler
{
    /// <summary>
    /// Вызывается в потоке хука. Должен вернуться за микросекунды.
    /// true — подавить событие (хук вернёт 1).
    /// </summary>
    bool OnKey(in KBDLLHOOKSTRUCT key, long timestamp);
}

/// <summary>
/// Глобальный WH_KEYBOARD_LL на собственном потоке с собственным циклом сообщений
/// (ThreadPriority.Highest): подвисания UI не влияют на хук и не приводят
/// к его снятию системой по LowLevelHooksTimeout.
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    private static ILowLevelKeyHandler? s_handler;
    private static nint s_hook;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private int _startError;
    private int _disposed;

    public KeyboardHook(ILowLevelKeyHandler handler)
    {
        if (Interlocked.CompareExchange(ref s_handler, handler, null) != null)
            throw new InvalidOperationException("Хук клавиатуры уже установлен.");

        _thread = new Thread(ThreadProc)
        {
            Name = "KeyboardHook",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        _ready.Wait();

        if (_startError != 0)
        {
            s_handler = null;
            throw new InvalidOperationException($"SetWindowsHookEx не удался, код ошибки {_startError}.");
        }
    }

    private void ThreadProc()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        // Гарантируем наличие очереди сообщений до установки хука
        NativeMethods.PeekMessageW(out _, 0, 0, 0, NativeMethods.PM_NOREMOVE);

        nint proc = (nint)(delegate* unmanaged<int, nint, nint, nint>)&HookProc;
        nint hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, proc,
            NativeMethods.GetModuleHandleW(null), 0);
        if (hook == 0)
        {
            _startError = Marshal.GetLastWin32Error();
            _ready.Set();
            return;
        }

        s_hook = hook;
        _ready.Set();

        while (NativeMethods.GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(in msg);
            NativeMethods.DispatchMessageW(in msg);
        }

        EmergencyUnhook();
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode == NativeMethods.HC_ACTION)
        {
            long ts = Stopwatch.GetTimestamp();
            try
            {
                ILowLevelKeyHandler? h = s_handler;
                if (h != null && h.OnKey(in *(KBDLLHOOKSTRUCT*)lParam, ts))
                    return 1;
            }
            catch
            {
                // Исключение не должно пересечь границу native-кода
            }
        }
        return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>Снятие хука из любого потока (аварийный путь). Идемпотентно.</summary>
    public static void EmergencyUnhook()
    {
        nint h = Interlocked.Exchange(ref s_hook, 0);
        if (h != 0) NativeMethods.UnhookWindowsHookEx(h);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_threadId != 0)
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, 0, 0);
        if (!_thread.Join(1000))
            EmergencyUnhook();
        s_handler = null;
        _ready.Dispose();
    }
}
