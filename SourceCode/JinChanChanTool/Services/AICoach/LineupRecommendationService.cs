using System.Text.Json;
using JinChanChanTool.DataClass;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public sealed class LineupRecommendationService
{
    private readonly ILineUpService _lineUpService;
    private readonly Dictionary<string, string[]> _recipes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _heroTraits = new(StringComparer.OrdinalIgnoreCase);
    private bool _metadataLoaded;

    private sealed class EquipmentJsonRow
    {
        public string Name { get; set; } = "";
        public string EquipmentType { get; set; } = "";
        public string[]? SyntheticPathway { get; set; }
    }

    private sealed class HeroJsonRow
    {
        public string HeroName { get; set; } = "";
        public string[] Profession { get; set; } = [];
        public string[] Peculiarity { get; set; } = [];
    }

    public LineupRecommendationService(ILineUpService lineUpService)
    {
        _lineUpService = lineUpService;
    }

    public List<LineupRecommendation> Recommend(GameStateSnapshot state, int top = 5)
    {
        EnsureMetadataLoaded();
        int stageIndex = ResolveStageIndex(state);
        LiveBoardSnapshot board = LiveBoardState.GetSnapshot();
        var result = new List<LineupRecommendation>();

        foreach (LineUp lineUp in _lineUpService.GetLineUps())
        {
            if (lineUp.SubLineUps == null || lineUp.SubLineUps.Length == 0) continue;
            int safeIndex = Math.Clamp(stageIndex, 0, lineUp.SubLineUps.Length - 1);
            var units = lineUp.SubLineUps[safeIndex].LineUpUnits ?? [];
            var heroSet = units.Select(u => u.HeroName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desiredEquipments = units.SelectMany(u => u.EquipmentNames ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var equipmentSet = desiredEquipments.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var matchedShopHeroes = state.ShopHeroes
                .Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var matchedBoardHeroes = board.InferredHeroes
                .Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            (double traitCoverage, List<string> matchedTraits) = MatchBoardTraits(board.Traits, units);

            var owned = state.Equipments.Concat(state.Emblems).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var directMatches = owned.Where(equipmentSet.Contains).ToList();
            var componentMatches = MatchComponents(owned, desiredEquipments, directMatches);
            var matchedEquipments = directMatches
                .Concat(componentMatches)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // V2.1：上场棋盘 > 当前商店。商店是瞬时信号，只用于微调。
            double boardHeroScore = matchedBoardHeroes.Count * 15.0;
            double boardTraitScore = traitCoverage * 38.0;
            double shopHeroScore = matchedShopHeroes.Count * 7.0;
            double directEquipmentScore = directMatches.Count * 9.0;
            double componentScore = componentMatches.Count * 3.5;
            double stageBonus = units.Count switch
            {
                <= 5 when safeIndex == 0 => 4,
                <= 7 when safeIndex == 1 => 4,
                >= 7 when safeIndex == 2 => 4,
                _ => 0
            };

            double score = Math.Min(100,
                boardHeroScore + boardTraitScore + shopHeroScore +
                directEquipmentScore + componentScore + stageBonus);

            string reason = BuildReason(
                board,
                matchedBoardHeroes,
                matchedTraits,
                matchedShopHeroes,
                directMatches,
                componentMatches,
                safeIndex,
                traitCoverage);

            result.Add(new LineupRecommendation
            {
                Name = lineUp.LineUpName,
                Score = score,
                StageIndex = safeIndex,
                MatchedHeroes = matchedBoardHeroes.Concat(matchedShopHeroes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
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

    private (double Coverage, List<string> MatchedTraits) MatchBoardTraits(
        Dictionary<string, int> observed,
        IEnumerable<LineUpUnit> units)
    {
        if (observed.Count == 0 || _heroTraits.Count == 0) return (0, []);

        var lineupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string hero in units.Select(x => x.HeroName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_heroTraits.TryGetValue(hero, out string[]? traits)) continue;
            foreach (string trait in traits)
                lineupCounts[trait] = lineupCounts.GetValueOrDefault(trait) + 1;
        }

        double total = 0;
        double matched = 0;
        var details = new List<string>();
        foreach (var pair in observed)
        {
            int target = Math.Max(0, pair.Value);
            int available = lineupCounts.GetValueOrDefault(pair.Key);
            int hit = Math.Min(target, available);
            total += target;
            matched += hit;
            if (hit > 0) details.Add($"{pair.Key}{hit}/{target}");
        }

        return (total <= 0 ? 0 : matched / total, details);
    }

    private List<string> MatchComponents(List<string> owned, List<string> desired, List<string> directMatches)
    {
        var directCounts = directMatches.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var available = owned.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var matches = new List<string>();

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

    private void EnsureMetadataLoaded()
    {
        if (_metadataLoaded) return;
        _metadataLoaded = true;
        try
        {
            string root = Path.Combine(Application.StartupPath, "Resources", "HeroDatas");
            if (!Directory.Exists(root)) return;
            string? season = Directory.GetDirectories(root)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "S18", StringComparison.OrdinalIgnoreCase))
                ?? Directory.GetDirectories(root).FirstOrDefault();
            if (season == null) return;

            string equipmentPath = Path.Combine(season, "Equipment.json");
            if (File.Exists(equipmentPath))
            {
                var rows = JsonSerializer.Deserialize<List<EquipmentJsonRow>>(File.ReadAllText(equipmentPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                foreach (var row in rows)
                {
                    if (!string.IsNullOrWhiteSpace(row.Name) && row.SyntheticPathway is { Length: > 0 })
                        _recipes[row.Name] = row.SyntheticPathway;
                }
            }

            string heroPath = Path.Combine(season, "HeroData.json");
            if (File.Exists(heroPath))
            {
                var heroes = JsonSerializer.Deserialize<List<HeroJsonRow>>(File.ReadAllText(heroPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                foreach (var hero in heroes)
                {
                    if (string.IsNullOrWhiteSpace(hero.HeroName)) continue;
                    _heroTraits[hero.HeroName] = hero.Profession.Concat(hero.Peculiarity)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
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

        int liveLevel = LiveBoardState.GetSnapshot().InferredLevel;
        if (liveLevel > 0)
        {
            if (liveLevel <= 5) return 0;
            if (liveLevel <= 7) return 1;
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

    private static string BuildReason(
        LiveBoardSnapshot board,
        List<string> boardHeroes,
        List<string> matchedTraits,
        List<string> shopHeroes,
        List<string> directEquipments,
        List<string> components,
        int stageIndex,
        double traitCoverage)
    {
        string stage = stageIndex switch { 0 => "前期", 1 => "中期", _ => "后期" };
        var parts = new List<string> { $"按{stage}阵容匹配" };

        if (board.InferredHeroes.Count > 0)
            parts.Add($"上场反推：{string.Join("、", board.InferredHeroes)}");
        else if (board.Traits.Count > 0)
            parts.Add($"上场羁绊：{string.Join("、", board.Traits.Select(x => $"{x.Key}{x.Value}"))}");

        if (boardHeroes.Count > 0) parts.Add($"上场棋子命中：{string.Join("、", boardHeroes)}");
        if (matchedTraits.Count > 0) parts.Add($"羁绊覆盖{traitCoverage:P0}：{string.Join("、", matchedTraits)}");
        if (shopHeroes.Count > 0) parts.Add($"商店命中：{string.Join("、", shopHeroes)}");
        if (directEquipments.Count > 0) parts.Add($"成装/纹章命中：{string.Join("、", directEquipments)}");
        if (components.Count > 0) parts.Add($"散件可合：{string.Join("、", components)}");
        if (boardHeroes.Count == 0 && matchedTraits.Count == 0 && shopHeroes.Count == 0 && directEquipments.Count == 0 && components.Count == 0)
            parts.Add("暂无直接命中，作为备选路线");
        return string.Join("；", parts);
    }
}
