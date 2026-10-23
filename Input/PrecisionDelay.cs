using System.Diagnostics;
using JdAmulet.Native;

namespace JdAmulet.Input;

/// <summary>
/// Точная пауза: высокоточный waitable-таймер (Win10 1803+) спит почти до цели,
/// последние 1–2 мс добиваются spin-ожиданием по Stopwatch.
/// Используется только потоком ввода. Thread.Sleep/Task.Delay не применяются.
/// </summary>
public sealed class PrecisionDelay : IDisposable
{
    private nint _timer;
    private readonly long _spinTicks;

    public bool HighResolution { get; }
    public string Mode => HighResolution ? "высокоточный таймер" : (_timer != 0 ? "waitable-таймер + spin" : "spin");

    public PrecisionDelay()
    {
        _timer = NativeMethods.CreateWaitableTimerExW(0, null,
            NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);
        HighResolution = _timer != 0;
        if (_timer == 0)
        {
            // Старые Windows: обычный таймер, его точность ~1 мс за счёт timeBeginPeriod(1)
            _timer = NativeMethods.CreateWaitableTimerExW(0, null, 0, NativeMethods.TIMER_ALL_ACCESS);
        }
        double spinMs = HighResolution ? 1.0 : 2.0;
        _spinTicks = (long)(Stopwatch.Frequency * spinMs / 1000.0);
    }

    public static long MsToTicks(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void Wait(double ms) => WaitUntil(Stopwatch.GetTimestamp() + MsToTicks(ms));

    public void WaitUntil(long targetTimestamp)
    {
        long remaining = targetTimestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;

        long sleepTicks = remaining - _spinTicks;
        if (sleepTicks > 0 && _timer != 0)
        {
            // Отрицательное значение = относительное время в единицах 100 нс
            long due = -(long)(sleepTicks * 10_000_000.0 / Stopwatch.Frequency);
            if (due < 0 && NativeMethods.SetWaitableTimer(_timer, in due, 0, 0, 0, false))
                NativeMethods.WaitForSingleObject(_timer, NativeMethods.INFINITE);
        }

        while (Stopwatch.GetTimestamp() < targetTimestamp)
            Thread.SpinWait(20);
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            NativeMethods.CloseHandle(_timer);
            _timer = 0;
        }
    }
}
