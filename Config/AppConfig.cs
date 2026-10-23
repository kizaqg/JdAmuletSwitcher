using System.Text.Json;
using System.Text.Json.Serialization;
using JdAmulet.Input;

namespace JdAmulet.Config;

public enum RepeatMode
{
    /// <summary>Повторы автоповтора при удержании подавляются.</summary>
    Ignore = 0,
    /// <summary>Каждый повтор отправляет скилл без повторного надевания амулета.</summary>
    SkillOnly = 1,
}

public sealed class BindingConfig
{
    public string Name { get; set; } = "Связка";
    public Keys SkillKey { get; set; }
    public Keys AmuletKey { get; set; }
    public int DelayMs { get; set; } = 20;
    public Keys Hotkey { get; set; }
    public bool Enabled { get; set; } = true;

    public BindingConfig Clone() => (BindingConfig)MemberwiseClone();
}

public sealed class MainAmuletConfig
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = "Основной";
    public Keys AmuletKey { get; set; } = Keys.D6;
    public Keys Hotkey { get; set; } = Keys.Control | Keys.D6;

    public MainAmuletConfig Clone() => (MainAmuletConfig)MemberwiseClone();
}

public sealed class Profile
{
    public string Name { get; set; } = "Основной";
    public List<BindingConfig> Bindings { get; set; } = new();
    public MainAmuletConfig MainAmulet { get; set; } = new();

    public Profile Clone(string name) => new()
    {
        Name = name,
        Bindings = Bindings.Select(b => b.Clone()).ToList(),
        MainAmulet = MainAmulet.Clone(),
    };
}

public sealed class OverlayConfig
{
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = 0.85;
    public int OffsetX { get; set; } = 160;
    public bool Locked { get; set; }
}

public sealed class AppConfig
{
    public const int MaxDelayMs = 200;

    public string ProcessName { get; set; } = "ElementClient.exe";
    public InputMode InputMode { get; set; } = InputMode.SendInput;
    public Keys ToggleKey { get; set; } = Keys.Pause;
    public RepeatMode RepeatMode { get; set; } = RepeatMode.Ignore;
    public bool AlwaysEquip { get; set; }
    public bool StartMinimized { get; set; }
    public bool LogEnabled { get; set; } = true;
    public string ActiveProfile { get; set; } = "Основной";
    public List<Profile> Profiles { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();

    public Profile GetActiveProfile()
    {
        Profile? p = Profiles.FirstOrDefault(x => x.Name == ActiveProfile) ?? Profiles.FirstOrDefault();
        if (p == null)
        {
            p = CreateDefaultProfile("Основной");
            Profiles.Add(p);
        }
        ActiveProfile = p.Name;
        return p;
    }

    public static Profile CreateDefaultProfile(string name)
    {
        var p = new Profile { Name = name };
        Keys[] skills = { Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5 };
        Keys[] amulets = { Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.D0 };
        for (int i = 0; i < 5; i++)
        {
            p.Bindings.Add(new BindingConfig
            {
                Name = $"Связка {i + 1}",
                SkillKey = skills[i],
                AmuletKey = amulets[i],
                DelayMs = 20,
                Hotkey = Keys.Control | (Keys.D1 + i),
            });
        }
        p.MainAmulet = new MainAmuletConfig { Enabled = false, AmuletKey = Keys.D5, Hotkey = Keys.Control | Keys.D6 };
        return p;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ProcessName)) ProcessName = "ElementClient.exe";
        Profiles ??= new();
        Overlay ??= new();
        Overlay.Opacity = Math.Clamp(Overlay.Opacity, 0.2, 1.0);
        Overlay.OffsetX = Math.Max(0, Overlay.OffsetX);
        foreach (Profile p in Profiles)
        {
            p.Bindings ??= new();
            p.MainAmulet ??= new();
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Профиль" : p.Name.Trim();
            foreach (BindingConfig b in p.Bindings)
            {
                b.DelayMs = Math.Clamp(b.DelayMs, 0, MaxDelayMs);
                b.Name ??= "Связка";
            }
        }
        GetActiveProfile();
    }
}

public static class ConfigStore
{
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "JdAmuletSwitcher.json");

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load(out string? error)
    {
        error = null;
        AppConfig? cfg = null;
        try
        {
            if (File.Exists(FilePath))
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), s_options);
        }
        catch (Exception ex)
        {
            error = $"Не удалось прочитать конфиг ({ex.Message}); загружены настройки по умолчанию.";
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { /* резервная копия не критична */ }
        }

        cfg ??= new AppConfig();
        if (cfg.Profiles.Count == 0)
            cfg.Profiles.Add(AppConfig.CreateDefaultProfile("Основной"));
        cfg.Normalize();
        return cfg;
    }

    public static void Save(AppConfig cfg)
    {
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, s_options));
        File.Move(tmp, FilePath, true);
    }
}
