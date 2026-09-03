namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachSettings
{
    public int SettingsVersion { get; set; } = 3;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-5-mini";
    public bool AutoRefresh { get; set; } = true;
    public int RefreshIntervalMs { get; set; } = 1000;

    // V2.1：旧的 2D 装备图标模板会把左侧羁绊栏误判为装备，默认关闭。
    // 后续改成“悬停装备 -> OCR Tooltip 名称”后再重新启用。
    public bool AutoDetectEquipments { get; set; } = false;
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

    // V2.1：读取左侧“上场羁绊”面板，再结合 S18 HeroData 反推当前棋盘。
    public bool AutoDetectBoardTraits { get; set; } = true;
    public int BoardReferenceWidth { get; set; } = 2048;
    public int BoardReferenceHeight { get; set; } = 1152;
    public int BoardTraitX { get; set; } = 105;
    public int BoardTraitY { get; set; } = 268;
    public int BoardTraitWidth { get; set; } = 235;
    public int BoardTraitRowHeight { get; set; } = 58;
    public int BoardTraitStepY { get; set; } = 63;
    public int BoardTraitRowCount { get; set; } = 9;
    public int BoardLevelX { get; set; } = 350;
    public int BoardLevelY { get; set; } = 930;
    public int BoardLevelWidth { get; set; } = 135;
    public int BoardLevelHeight { get; set; } = 55;
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
