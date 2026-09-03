namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachSettings
{
    public int SettingsVersion { get; set; } = 8;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-5-mini";
    public bool AutoRefresh { get; set; } = true;
    public int RefreshIntervalMs { get; set; } = 1000;

    public bool UseOnlineMeta { get; set; } = true;
    public int OnlineMetaCacheMinutes { get; set; } = 30;
    public bool IncludeLowPickStrongComps { get; set; } = true;

    public bool GenerateLineUpsWithAi { get; set; } = true;
    public int LineupGenerationMaxComps { get; set; } = 50;
    public int LineupGenerationBatchSize { get; set; } = 8;
    public bool AutoApplyGeneratedLineups { get; set; } = true;

    public bool WinRateDecisionMode { get; set; } = true;

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

    public bool AutoDetectHud { get; set; } = true;
    public int HudRefreshIntervalMs { get; set; } = 1000;
    public int HudReferenceWidth { get; set; } = 2048;
    public int HudReferenceHeight { get; set; } = 1152;
    public int HudStageX { get; set; } = 775;
    public int HudStageY { get; set; } = 0;
    public int HudStageWidth { get; set; } = 135;
    public int HudStageHeight { get; set; } = 58;
    public int HudLevelX { get; set; } = 340;
    public int HudLevelY { get; set; } = 915;
    public int HudLevelWidth { get; set; } = 155;
    public int HudLevelHeight { get; set; } = 70;
    public int HudGoldX { get; set; } = 1035;
    public int HudGoldY { get; set; } = 915;
    public int HudGoldWidth { get; set; } = 135;
    public int HudGoldHeight { get; set; } = 70;
    public int HudSidebarX { get; set; } = 1770;
    public int HudSidebarY { get; set; } = 145;
    public int HudSidebarWidth { get; set; } = 278;
    public int HudSidebarHeight { get; set; } = 760;
    public int HudSelfHpOffsetX { get; set; } = 78;
    public int HudSelfHpOffsetY { get; set; } = 32;
    public int HudSelfHpWidth { get; set; } = 105;
    public int HudSelfHpHeight { get; set; } = 64;
}

public sealed class GameStateSnapshot
{
    public string[] ShopHeroes { get; set; } = [];
    public List<string> Equipments { get; set; } = [];
    public List<string> Augments { get; set; } = [];
    public List<string> Emblems { get; set; } = [];

    // 用户可选输入：备战席/已持有的关键牌。它们比单轮商店更有方向价值，但弱于已经上场的棋盘。
    public List<string> HeldHeroes { get; set; } = [];

    // 用户可选输入：明显被同行争抢的核心牌。reroll阵容会对此给予更强惩罚。
    public List<string> ContestedHeroes { get; set; } = [];

    public string Stage { get; set; } = "";
    public int Level { get; set; }
    public int Gold { get; set; }
    public int Hp { get; set; }
}

public sealed class LineupRecommendation
{
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public double Confidence { get; set; }
    public int StageIndex { get; set; }
    public List<string> MatchedHeroes { get; set; } = [];
    public List<string> MatchedEquipments { get; set; } = [];
    public List<string> MatchedAugments { get; set; } = [];
    public List<string> MatchedEmblems { get; set; } = [];
    public List<string> MatchedHeldHeroes { get; set; } = [];
    public List<string> ContestedCoreHeroes { get; set; } = [];

    public string Decision { get; set; } = "观察";
    public string RiskLevel { get; set; } = "中";
    public string NextAction { get; set; } = "保持经济，继续观察下一轮。";
    public string Warning { get; set; } = "";
    public string Reason { get; set; } = "";

    public string Source { get; set; } = "本地";
    public string MetaTier { get; set; } = "";
    public double MetaWinRate { get; set; }
    public double MetaTopFourRate { get; set; }
    public double MetaPickRate { get; set; }
    public double MetaAverageRank { get; set; }
    public List<string> MetaTags { get; set; } = [];
}
