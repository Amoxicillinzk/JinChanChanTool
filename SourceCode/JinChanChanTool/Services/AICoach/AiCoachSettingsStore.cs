using System.Text.Json;

namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachSettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AiCoachSettingsStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JinChanChanTool");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "ai-coach.json");
    }

    public AiCoachSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AiCoachSettings();
            AiCoachSettings settings = JsonSerializer.Deserialize<AiCoachSettings>(File.ReadAllText(_path), JsonOptions) ?? new AiCoachSettings();

            if (settings.SettingsVersion < 3)
            {
                settings.AutoDetectEquipments = false;
                settings.AutoDetectBoardTraits = true;
            }

            if (settings.SettingsVersion < 4)
            {
                settings.AutoDetectHud = true;
                settings.HudRefreshIntervalMs = 1000;
            }

            // V3：在线 Meta 成为推荐主数据源；固定 LineUps.json 只做断网兜底。
            if (settings.SettingsVersion < 5)
            {
                settings.UseOnlineMeta = true;
                settings.OnlineMetaCacheMinutes = 30;
                settings.IncludeLowPickStrongComps = true;
                settings.SettingsVersion = 5;
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new AiCoachSettings();
        }
    }

    public void Save(AiCoachSettings settings)
    {
        settings.SettingsVersion = Math.Max(settings.SettingsVersion, 5);
        settings.OnlineMetaCacheMinutes = Math.Clamp(settings.OnlineMetaCacheMinutes, 5, 240);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
