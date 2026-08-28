using static JinChanChanTool.Services.AutoSetCoordinates.CoordinateCalculationService;

namespace JinChanChanTool.DataClass.StaticData
{
    /// <summary>
    /// 云顶之弈旧客户端 UI 的基准坐标模板，对应 League of Legends 进程。
    /// </summary>
    public static class LegacyTftCoordinateTemplates
    {
        public static readonly Size BaseResolution = new Size(1920, 1080);

        public static readonly AnchorProfile ExperienceButton = new(-595, -120, 170, 50);
        public static readonly AnchorProfile RefreshButton = new(-597.5, -47.5, 175, 55);

        public static readonly AnchorProfile CardSlot1_Name = new(-415, -25, 120, 30);
        public static readonly AnchorProfile CardSlot2_Name = new(-217.5, -25, 115, 30);
        public static readonly AnchorProfile CardSlot3_Name = new(-10, -25, 130, 30);
        public static readonly AnchorProfile CardSlot4_Name = new(185, -25, 120, 30);
        public static readonly AnchorProfile CardSlot5_Name = new(390, -25, 130, 30);

        public static readonly AnchorProfile CardSlot1_Click = new(-384, -80.5, 191, 141);
        public static readonly AnchorProfile CardSlot2_Click = new(-183, -80.5, 191, 141);
        public static readonly AnchorProfile CardSlot3_Click = new(18, -80.5, 191, 141);
        public static readonly AnchorProfile CardSlot4_Click = new(220, -80.5, 191, 141);
        public static readonly AnchorProfile CardSlot5_Click = new(422, -80.5, 191, 141);

        public static readonly AnchorProfile GoldAmount = new(22.5, -185, 65, 30);
    }
}
