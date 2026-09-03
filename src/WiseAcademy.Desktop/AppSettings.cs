using System.Text.Json;
using System.Text.Json.Serialization;
using WiseAcademy.Desktop.Core;

namespace WiseAcademy.Desktop;

/// <summary>Carrega / grava o appsettings.json ao lado do executável.</summary>
internal static class AppSettings
{
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        AppConfig cfg;
        try
        {
            cfg = File.Exists(Path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Opts) ?? new AppConfig()
                : new AppConfig();
        }
        catch { cfg = new AppConfig(); }

        if (string.IsNullOrWhiteSpace(cfg.DeviceName))
            cfg.DeviceName = Environment.MachineName;
        return cfg;
    }

    public static void Save(AppConfig cfg)
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(cfg, Opts)); }
        catch { /* pasta só-de-leitura — mantém em memória */ }
    }
}
