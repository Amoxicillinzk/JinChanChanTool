using JinChanChanTool.DataClass;
using JinChanChanTool.Services.LineupCrawling;
using JinChanChanTool.Services.RecommendedEquipment;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// 复用项目原有的 MetaTFT JSON 数据链路。该链路直接提供当前版本统计、阵容、装备、标签与站位，
/// 比抓取第三方网页 HTML 更稳定，也便于后续增加 OP.GG Provider。
/// </summary>
public sealed class MetaTftOnlineMetaProvider : IOnlineMetaProvider
{
    public string Name => "MetaTFT实时";

    public async Task<List<OnlineMetaComp>> FetchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var gameData = new DynamicGameDataService();
        await gameData.InitializeAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var crawler = new LineupCrawlingService(gameData, "S18");
        List<RecommendedLineUp> raw = await crawler.GetRecommendedLineUpsAsync(null!);
        cancellationToken.ThrowIfCancellationRequested();

        return raw
            .Where(x => x.LineUpUnits is { Count: >= 3 })
            .Where(x => !string.IsNullOrWhiteSpace(x.LineUpName))
            .Select(x => new OnlineMetaComp
            {
                Name = x.LineUpName.Trim(),
                Tier = x.GetTierDisplayText(),
                WinRate = x.WinRate,
                AverageRank = x.AverageRank,
                PickRate = x.PickRate,
                TopFourRate = x.TopFourRate,
                Tags = x.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList() ?? [],
                Source = Name,
                Units = x.LineUpUnits
                    .Where(u => !string.IsNullOrWhiteSpace(u.HeroName))
                    .Select(u => new OnlineMetaUnit
                    {
                        HeroName = u.HeroName.Trim(),
                        EquipmentNames = (u.EquipmentNames ?? ["", "", ""])
                            .Take(3).Concat(Enumerable.Repeat("", 3)).Take(3).ToArray()
                    })
                    .ToList()
            })
            .Where(x => x.Units.Count >= 3)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => TierOrder(x.Tier)).ThenByDescending(x => x.WinRate).First())
            .OrderBy(x => TierOrder(x.Tier))
            .ThenBy(x => x.AverageRank <= 0 ? 99 : x.AverageRank)
            .ThenByDescending(x => x.WinRate)
            .ToList();
    }

    private static int TierOrder(string tier) => tier switch
    {
        "S" => 0,
        "A" => 1,
        "B" => 2,
        "C" => 3,
        "D" => 4,
        _ => 5
    };
}
