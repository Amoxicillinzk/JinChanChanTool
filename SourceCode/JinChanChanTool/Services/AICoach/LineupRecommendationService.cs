using JinChanChanTool.DataClass;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public sealed class LineupRecommendationService
{
    private readonly ILineUpService _lineUpService;

    public LineupRecommendationService(ILineUpService lineUpService)
    {
        _lineUpService = lineUpService;
    }

    public List<LineupRecommendation> Recommend(GameStateSnapshot state, int top = 5)
    {
        int stageIndex = ResolveStageIndex(state);
        var result = new List<LineupRecommendation>();

        foreach (LineUp lineUp in _lineUpService.GetLineUps())
        {
            if (lineUp.SubLineUps == null || lineUp.SubLineUps.Length == 0) continue;
            int safeIndex = Math.Clamp(stageIndex, 0, lineUp.SubLineUps.Length - 1);
            var units = lineUp.SubLineUps[safeIndex].LineUpUnits ?? [];
            var heroSet = units.Select(u => u.HeroName).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var equipmentSet = units.SelectMany(u => u.EquipmentNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var matchedHeroes = state.ShopHeroes.Where(heroSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var matchedEquipments = state.Equipments.Concat(state.Emblems)
                .Where(equipmentSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            double heroScore = matchedHeroes.Count * 18.0;
            double equipmentScore = matchedEquipments.Count * 12.0;
            double stageBonus = units.Count switch
            {
                <= 5 when safeIndex == 0 => 8,
                <= 7 when safeIndex == 1 => 8,
                >= 7 when safeIndex == 2 => 8,
                _ => 0
            };

            double score = Math.Min(100, heroScore + equipmentScore + stageBonus);
            string reason = BuildReason(matchedHeroes, matchedEquipments, safeIndex);

            result.Add(new LineupRecommendation
            {
                Name = lineUp.LineUpName,
                Score = score,
                StageIndex = safeIndex,
                MatchedHeroes = matchedHeroes,
                MatchedEquipments = matchedEquipments,
                Reason = reason
            });
        }

        return result
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, top))
            .ToList();
    }

    private static int ResolveStageIndex(GameStateSnapshot state)
    {
        if (state.Level > 0)
        {
            if (state.Level <= 5) return 0;
            if (state.Level <= 7) return 1;
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(state.Stage))
        {
            if (state.Stage.StartsWith("2-", StringComparison.OrdinalIgnoreCase)) return 0;
            if (state.Stage.StartsWith("3-", StringComparison.OrdinalIgnoreCase)) return 1;
            if (state.Stage.StartsWith("4-", StringComparison.OrdinalIgnoreCase) ||
                state.Stage.StartsWith("5-", StringComparison.OrdinalIgnoreCase) ||
                state.Stage.StartsWith("6-", StringComparison.OrdinalIgnoreCase)) return 2;
        }

        return 0;
    }

    private static string BuildReason(List<string> heroes, List<string> equipments, int stageIndex)
    {
        string stage = stageIndex switch { 0 => "前期", 1 => "中期", _ => "后期" };
        var parts = new List<string> { $"按{stage}阵容匹配" };
        if (heroes.Count > 0) parts.Add($"商店命中：{string.Join("、", heroes)}");
        if (equipments.Count > 0) parts.Add($"装备命中：{string.Join("、", equipments)}");
        if (heroes.Count == 0 && equipments.Count == 0) parts.Add("暂无直接命中，作为备选路线");
        return string.Join("；", parts);
    }
}
