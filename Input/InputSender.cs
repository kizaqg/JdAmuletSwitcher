using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using JdAmulet.Native;

namespace JdAmulet.Input;

public enum InputMode
{
    SendInput = 0,
    PostMessage = 1,
}

/// <summary>Одно событие клавиатуры: нажатие или отпускание.</summary>
public readonly record struct KeyAction(KeyStroke Key, bool Up)
{
    public static KeyAction Down(KeyStroke k) => new(k, false);
    public static KeyAction Release(KeyStroke k) => new(k, true);
}

public interface IInputJobHandler<TJob> where TJob : struct
{
    /// <summary>Выполняется в выделенном потоке ввода.</summary>
    void Execute(in TJob job);

    void OnError(Exception ex);
}

/// <summary>
/// Выделенный поток ввода (ThreadPriority.Highest). Ждёт задания на AutoResetEvent, без опроса.
/// Каждый вызов SendInput атомарен; при частичной вставке затронутые клавиши отпускаются.
/// </summary>
public sealed unsafe class InputSender<TJob> : IDisposable where TJob : struct
{
    /// <summary>Метка синтетических событий в dwExtraInfo: хук их пропускает.</summary>
    public const nint Marker = 0x4A44414D; // "JDAM"

    private const byte VkUnassigned = 0xE8; // «пустая» клавиша: гасит активацию меню при отпускании Alt

    private readonly ConcurrentQueue<TJob> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly IInputJobHandler<TJob> _handler;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile int _mode;

    public PrecisionDelay Delay { get; } = new();
    public int LastError { get; private set; }

    public InputMode Mode
    {
        get => (InputMode)_mode;
        set => _mode = (int)value;
    }

    public InputSender(IInputJobHandler<TJob> handler)
    {
        _handler = handler;
        _thread = new Thread(Run)
        {
            Name = "InputSender",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>Постановка задания из любого потока (в т.ч. из колбэка хука).</summary>
    public void Post(in TJob job)
    {
        _queue.Enqueue(job);
        _signal.Set();
    }

    private void Run()
    {
        while (true)
        {
            _signal.WaitOne();
            if (_stop) break;
            while (_queue.TryDequeue(out TJob job))
            {
                try { _handler.Execute(in job); }
                catch (Exception ex) { _handler.OnError(ex); }
                if (_stop) return;
            }
        }
    }

    /// <summary>
    /// Отправляет последовательность событий клавиатуры. В режиме SendInput — одним вызовом
    /// (вставка атомарна): [отпускание модификаторов] события [возврат модификаторов].
    /// </summary>
    public bool Send(ReadOnlySpan<KeyAction> actions, int releaseMods, int restoreMods, nint targetHwnd)
    {
        if (Mode == InputMode.PostMessage)
            return PostActions(actions, targetHwnd);

        INPUT* buf = stackalloc INPUT[32];
        int n = 0;

        releaseMods &= ModBits.Releasable;
        if (releaseMods != 0)
        {
            if ((releaseMods & ModBits.AnyAlt) != 0)
            {
                AddVk(buf, ref n, VkUnassigned, false);
                AddVk(buf, ref n, VkUnassigned, true);
            }
            for (int bit = 1; bit <= ModBits.RAlt; bit <<= 1)
                if ((releaseMods & bit) != 0) AddScan(buf, ref n, ModBits.Scan(bit), true);
        }

        int limit = Math.Min(actions.Length, 16);
        for (int i = 0; i < limit; i++)
            if (!actions[i].Key.IsEmpty) AddScan(buf, ref n, actions[i].Key.Scan, actions[i].Up);

        // Alt не возвращаем: иначе физическое отпускание Alt откроет системное меню окна
        restoreMods &= ModBits.Releasable & ~ModBits.AnyAlt;
        for (int bit = 1; bit <= ModBits.RShift; bit <<= 1)
            if ((restoreMods & bit) != 0) AddScan(buf, ref n, ModBits.Scan(bit), false);

        if (n == 0) return true;

        uint sent = NativeMethods.SendInput((uint)n, buf, sizeof(INPUT));
        if (sent == (uint)n) return true;

        LastError = Marshal.GetLastWin32Error();
        // Часть событий могла пройти: гарантированно отпускаем всё, что могло нажаться
        Span<ushort> ups = stackalloc ushort[16];
        int u = 0;
        for (int i = 0; i < limit; i++)
            if (!actions[i].Key.IsEmpty) ups[u++] = actions[i].Key.Scan;
        ReleaseKeys(ups[..u]);
        return false;
    }

    private bool PostActions(ReadOnlySpan<KeyAction> actions, nint hwnd)
    {
        if (hwnd == 0) { LastError = 1400; return false; } // ERROR_INVALID_WINDOW_HANDLE
        bool ok = true;
        foreach (KeyAction a in actions)
        {
            if (a.Key.IsEmpty) continue;
            uint l = 1u | ((uint)(a.Key.Scan & 0xFF) << 16) | (a.Key.Extended ? 1u << 24 : 0u);
            if (a.Up) l |= 0xC0000000u; // предыдущее состояние = нажата, переход = отпускание
            ok &= NativeMethods.PostMessageW(hwnd, a.Up ? NativeMethods.WM_KEYUP : NativeMethods.WM_KEYDOWN, a.Key.Vk, (nint)l);
        }
        if (!ok) LastError = Marshal.GetLastWin32Error();
        return ok;
    }

    private static void AddScan(INPUT* buf, ref int n, ushort scan, bool up)
    {
        ref INPUT i = ref buf[n++];
        i.type = NativeMethods.INPUT_KEYBOARD;
        i.u.ki.wVk = 0;
        i.u.ki.wScan = (ushort)(scan & 0xFF);
        i.u.ki.dwFlags = NativeMethods.KEYEVENTF_SCANCODE
                         | ((scan & 0x100) != 0 ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0)
                         | (up ? NativeMethods.KEYEVENTF_KEYUP : 0);
        i.u.ki.time = 0;
        i.u.ki.dwExtraInfo = Marker;
    }

    private static void AddVk(INPUT* buf, ref int n, byte vk, bool up)
    {
        ref INPUT i = ref buf[n++];
        i.type = NativeMethods.INPUT_KEYBOARD;
        i.u.ki.wVk = vk;
        i.u.ki.wScan = 0;
        i.u.ki.dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0;
        i.u.ki.time = 0;
        i.u.ki.dwExtraInfo = Marker;
    }

    /// <summary>Аварийное отпускание клавиш (из любого потока). Лишний key-up безвреден.</summary>
    public static void ReleaseKeys(ReadOnlySpan<ushort> scans)
    {
        if (scans.IsEmpty) return;
        int count = Math.Min(scans.Length, 64);
        INPUT* buf = stackalloc INPUT[count];
        int n = 0;
        for (int i = 0; i < count; i++)
            if (scans[i] != 0) AddScan(buf, ref n, scans[i], true);
        if (n > 0) NativeMethods.SendInput((uint)n, buf, sizeof(INPUT));
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _signal.Set();
        _thread.Join(1000);
        Delay.Dispose();
    }
}
