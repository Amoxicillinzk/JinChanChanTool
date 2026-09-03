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

            // V2 -> V2.1 migration：旧版装备识别实际上截到了羁绊栏，必须强制关闭，避免继续污染推荐分。
            if (settings.SettingsVersion < 3)
            {
                settings.SettingsVersion = 3;
                settings.AutoDetectEquipments = false;
                settings.AutoDetectBoardTraits = true;
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
        settings.SettingsVersion = Math.Max(settings.SettingsVersion, 3);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
