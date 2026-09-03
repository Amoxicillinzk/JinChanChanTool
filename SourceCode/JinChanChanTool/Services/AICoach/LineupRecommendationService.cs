using System.Text.Json;
using JinChanChanTool.DataClass;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public sealed class LineupRecommendationService
{
    private readonly ILineUpService _lineUpService;
    private readonly Dictionary<string, string[]> _recipes = new(StringComparer.OrdinalIgnoreCase);
    private bool _recipesLoaded;

    private sealed class EquipmentJsonRow
    {
        public string Name { get; set; } = "";
        public string EquipmentType { get; set; } = "";
        public string[]? SyntheticPathway { get; set; }
    }

    public LineupRecommendationService(ILineUpService lineUpService)
    {
        _lineUpService = lineUpService;
    }

    public List<LineupRecommendation> Recommend(GameStateSnapshot state, int top = 5)
    {
        EnsureRecipesLoaded();
        int stageIndex = ResolveStageIndex(state);
        var result = new List<LineupRecommendation>();

        foreach (LineUp lineUp in _lineUpService.GetLineUps())
        {
            if (lineUp.SubLineUps == null || lineUp.SubLineUps.Length == 0) continue;
            int safeIndex = Math.Clamp(stageIndex, 0, lineUp.SubLineUps.Length - 1);
            var units = lineUp.SubLineUps[safeIndex].LineUpUnits ?? [];
            var heroSet = units.Select(u => u.HeroName).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desiredEquipments = units.SelectMany(u => u.EquipmentNames ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var equipmentSet = desiredEquipments.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var matchedHeroes = state.ShopHeroes.Where(heroSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var owned = state.Equipments.Concat(state.Emblems).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var directMatches = owned.Where(equipmentSet.Contains).ToList();
            var componentMatches = MatchComponents(owned, desiredEquipments, directMatches);
            var matchedEquipments = directMatches
                .Concat(componentMatches)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            double heroScore = matchedHeroes.Count * 18.0;
            double directEquipmentScore = directMatches.Count * 12.0;
            double componentScore = componentMatches.Count * 4.5;
            double stageBonus = units.Count switch
            {
                <= 5 when safeIndex == 0 => 8,
                <= 7 when safeIndex == 1 => 8,
                >= 7 when safeIndex == 2 => 8,
                _ => 0
            };

            double score = Math.Min(100, heroScore + directEquipmentScore + componentScore + stageBonus);
            string reason = BuildReason(matchedHeroes, directMatches, componentMatches, safeIndex);

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

    private List<string> MatchComponents(List<string> owned, List<string> desired, List<string> directMatches)
    {
        var directCounts = directMatches.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var available = owned.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var matches = new List<string>();

        // 已经拥有的成装先从可用池里扣掉，避免同时当作散件使用。
        foreach (var pair in directCounts)
        {
            if (available.TryGetValue(pair.Key, out int count))
                available[pair.Key] = Math.Max(0, count - pair.Value);
        }

        foreach (string target in desired)
        {
            if (directCounts.TryGetValue(target, out int direct) && direct > 0)
            {
                directCounts[target] = direct - 1;
                continue;
            }
            if (!_recipes.TryGetValue(target, out string[]? components) || components.Length == 0) continue;

            foreach (string component in components)
            {
                if (available.TryGetValue(component, out int count) && count > 0)
                {
                    available[component] = count - 1;
                    matches.Add($"{component}→{target}");
                }
            }
        }
        return matches;
    }

    private void EnsureRecipesLoaded()
    {
        if (_recipesLoaded) return;
        _recipesLoaded = true;
        try
        {
            string root = Path.Combine(Application.StartupPath, "Resources", "HeroDatas");
            if (!Directory.Exists(root)) return;
            string? season = Directory.GetDirectories(root)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "S18", StringComparison.OrdinalIgnoreCase))
                ?? Directory.GetDirectories(root).FirstOrDefault();
            if (season == null) return;
            string path = Path.Combine(season, "Equipment.json");
            if (!File.Exists(path)) return;
            var rows = JsonSerializer.Deserialize<List<EquipmentJsonRow>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.Name) && row.SyntheticPathway is { Length: > 0 })
                    _recipes[row.Name] = row.SyntheticPathway;
            }
        }
        catch { }
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

    private static string BuildReason(List<string> heroes, List<string> directEquipments, List<string> components, int stageIndex)
    {
        string stage = stageIndex switch { 0 => "前期", 1 => "中期", _ => "后期" };
        var parts = new List<string> { $"按{stage}阵容匹配" };
        if (heroes.Count > 0) parts.Add($"商店命中：{string.Join("、", heroes)}");
        if (directEquipments.Count > 0) parts.Add($"成装/纹章命中：{string.Join("、", directEquipments)}");
        if (components.Count > 0) parts.Add($"散件可合：{string.Join("、", components)}");
        if (heroes.Count == 0 && directEquipments.Count == 0 && components.Count == 0) parts.Add("暂无直接命中，作为备选路线");
        return string.Join("；", parts);
    }
}
