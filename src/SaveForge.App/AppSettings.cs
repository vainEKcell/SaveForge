using System.Text.Json;

namespace SaveForge.App;

/// <summary>
/// 极简的本地设置（目前只有"上次选择的游戏根目录"）。
/// 存放在 %APPDATA%\SaveForge\settings.json，不写注册表。
/// </summary>
internal static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SaveForge", "settings.json");

    /// <summary>上次选择的游戏根目录。null 表示未设置。</summary>
    public static string? GameRoot { get; set; }

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;

            using var stream = File.OpenRead(SettingsPath);
            using var json = JsonDocument.Parse(stream);

            if (json.RootElement.TryGetProperty("gameRoot", out var root) &&
                root.ValueKind == JsonValueKind.String)
            {
                string? value = root.GetString();
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                    GameRoot = value;
            }
        }
        catch
        {
            // 设置损坏不影响主流程
        }
    }

    public static void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (directory is not null) Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(
                new { gameRoot = GameRoot },
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 忽略：设置写不进去也不该阻断用户操作
        }
    }
}
