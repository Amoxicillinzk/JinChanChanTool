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
            return JsonSerializer.Deserialize<AiCoachSettings>(File.ReadAllText(_path), JsonOptions) ?? new AiCoachSettings();
        }
        catch
        {
            return new AiCoachSettings();
        }
    }

    public void Save(AiCoachSettings settings)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
