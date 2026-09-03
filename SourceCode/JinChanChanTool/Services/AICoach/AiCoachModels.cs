namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachSettings
{
    public int SettingsVersion { get; set; } = 4;
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

    // V2.2：局面 HUD 实时读取。基准坐标来自用户 2048x1152 国服云顶实战截图。
    public bool AutoDetectHud { get; set; } = true;
    public int HudRefreshIntervalMs { get; set; } = 1000;
    public int HudReferenceWidth { get; set; } = 2048;
    public int HudReferenceHeight { get; set; } = 1152;

    // 顶部中央阶段，例如 2-1 / 3-2。
    public int HudStageX { get; set; } = 775;
    public int HudStageY { get; set; } = 0;
    public int HudStageWidth { get; set; } = 135;
    public int HudStageHeight { get; set; } = 58;

    // 左下等级，例如“3级”。
    public int HudLevelX { get; set; } = 340;
    public int HudLevelY { get; set; } = 915;
    public int HudLevelWidth { get; set; } = 155;
    public int HudLevelHeight { get; set; } = 70;

    // 底部中央金币，例如 6 / 50。
    public int HudGoldX { get; set; } = 1035;
    public int HudGoldY { get; set; } = 915;
    public int HudGoldWidth { get; set; } = 135;
    public int HudGoldHeight { get; set; } = 70;

    // 右侧玩家列表。先在此区域寻找当前玩家的大号金色头像框。
    public int HudSidebarX { get; set; } = 1770;
    public int HudSidebarY { get; set; } = 145;
    public int HudSidebarWidth { get; set; } = 278;
    public int HudSidebarHeight { get; set; } = 760;

    // 相对于玩家列表左边/当前玩家中心Y的血量 OCR 小框。
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
