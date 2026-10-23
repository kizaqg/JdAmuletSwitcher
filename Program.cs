using System.Runtime.CompilerServices;
using JdAmulet.Config;
using JdAmulet.Engine;
using JdAmulet.Input;
using JdAmulet.Native;
using JdAmulet.UI;

namespace JdAmulet;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\JdAmuletSwitcher.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("JD Amulet Switcher уже запущен (значок в трее).", "JD Amulet Switcher",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Emergency.Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Emergency.Report(e.ExceptionObject as Exception, fatal: true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Emergency.Cleanup();

        NativeMethods.timeBeginPeriod(1);
        Emergency.TimerPeriodActive = 1;

        // Прогрев JIT для пути хука и потока ввода (если exe собран без ReadyToRun)
        RuntimeHelpers.PrepareMethod(typeof(MacroEngine).GetMethod(nameof(MacroEngine.OnKey))!.MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(MacroEngine).GetMethod(nameof(MacroEngine.Execute))!.MethodHandle);

        try
        {
            AppConfig config = ConfigStore.Load(out string? loadError);
            var form = new MainForm(config, loadError);
            var context = new ApplicationContext();
            form.FormClosed += (_, _) => context.ExitThread();
            if (!config.StartMinimized) form.Show();
            Application.Run(context);
        }
        finally
        {
            Emergency.Cleanup();
        }
    }
}

/// <summary>
/// Аварийная очистка: снятие хука, отпускание клавиш, timeEndPeriod.
/// Идемпотентна, вызывается из finally, ProcessExit и UnhandledException.
/// </summary>
internal static class Emergency
{
    private static int s_cleaned;
    public static int TimerPeriodActive;
    public static volatile Action? ReleaseKeys;

    public static void Cleanup()
    {
        if (Interlocked.Exchange(ref s_cleaned, 1) != 0) return;
        try { KeyboardHook.EmergencyUnhook(); } catch { /* best effort */ }
        if (Interlocked.Exchange(ref TimerPeriodActive, 0) != 0)
        {
            try { NativeMethods.timeEndPeriod(1); } catch { /* best effort */ }
        }
    }

    public static void Report(Exception? ex, bool fatal)
    {
        if (fatal)
        {
            // Сначала гарантируем, что ни одна клавиша не останется «зажатой»
            try { KeyboardHook.EmergencyUnhook(); } catch { }
            try { ReleaseKeys?.Invoke(); } catch { }
            Cleanup();
        }

        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "JdAmuletSwitcher.crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] fatal={fatal}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }

        if (!fatal)
        {
            MessageBox.Show("Ошибка: " + ex?.Message + "\nПодробности в JdAmuletSwitcher.crash.log.",
                "JD Amulet Switcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
