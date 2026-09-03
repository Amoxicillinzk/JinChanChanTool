using System.Text.Json.Serialization;

namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-5-mini";
    public bool AutoRefresh { get; set; } = true;
    public int RefreshIntervalMs { get; set; } = 1000;
}

public sealed class GameStateSnapshot
{
    public string[] ShopHeroes { get; set; } = [];
    public List<string> Equipments { get; set; } = [];
    public List<string> Augments { get; set; } = [];
    public List<string> Emblems { get; set; } = [];
    public string Stage { get; set; } = "";
    public int Level { get; set; }
    public int Gold { get; set; }
    public int Hp { get; set; }
}

public sealed class LineupRecommendation
{
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public int StageIndex { get; set; }
    public List<string> MatchedHeroes { get; set; } = [];
    public List<string> MatchedEquipments { get; set; } = [];
    public string Reason { get; set; } = "";
}
