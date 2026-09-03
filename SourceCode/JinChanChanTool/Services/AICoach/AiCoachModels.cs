using System.Text.Json.Serialization;

namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-5-mini";
    public bool AutoRefresh { get; set; } = true;
    public int RefreshIntervalMs { get; set; } = 1000;

    // V2 装备栏自动识别。默认值来自 2048x1152 实战截图，运行时会按游戏屏幕分辨率等比缩放。
    public bool AutoDetectEquipments { get; set; } = true;
    public int InventoryReferenceWidth { get; set; } = 2048;
    public int InventoryReferenceHeight { get; set; } = 1152;
    public int InventorySlotX { get; set; } = 8;
    public int InventorySlotY { get; set; } = 279;
    public int InventorySlotWidth { get; set; } = 50;
    public int InventorySlotHeight { get; set; } = 50;
    public int InventorySlotStepY { get; set; } = 58;
    public int InventorySlotCount { get; set; } = 10;
    public double InventoryMatchThreshold { get; set; } = 0.78;
    public double InventoryEmptyMeanThreshold { get; set; } = 24.0;
    public double InventoryEmptyStdThreshold { get; set; } = 26.0;
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
