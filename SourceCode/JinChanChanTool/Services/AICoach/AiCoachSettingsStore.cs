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

            if (settings.SettingsVersion < 5)
            {
                settings.UseOnlineMeta = true;
                settings.OnlineMetaCacheMinutes = 30;
                settings.IncludeLowPickStrongComps = true;
            }

            // V4：Meta 手动刷新后可直接用当前 AI 接口重建 LineUps.json。
            if (settings.SettingsVersion < 6)
            {
                settings.GenerateLineUpsWithAi = true;
                settings.LineupGenerationMaxComps = 50;
                settings.LineupGenerationBatchSize = 8;
                settings.AutoApplyGeneratedLineups = true;
                settings.SettingsVersion = 6;
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
        settings.SettingsVersion = Math.Max(settings.SettingsVersion, 6);
        settings.OnlineMetaCacheMinutes = Math.Clamp(settings.OnlineMetaCacheMinutes, 5, 240);
        settings.LineupGenerationMaxComps = Math.Clamp(settings.LineupGenerationMaxComps, 10, 120);
        settings.LineupGenerationBatchSize = Math.Clamp(settings.LineupGenerationBatchSize, 1, 15);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
