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
        OnlineMetaSnapshot meta = OnlineMetaState.GetSnapshot();

        // V3：只要已有在线 Meta（包括最近一次缓存），就不再让人工维护的30套静态阵容主导推荐。
        if (meta.HasData)
            return RecommendOnline(state, meta, top);

        return RecommendLocalFallback(state, top);
    }

    private List<LineupRecommendation> RecommendOnline(GameStateSnapshot state, OnlineMetaSnapshot meta, int top)
    {
        LiveBoardSnapshot board = LiveBoardState.GetSnapshot();
        int stageIndex = ResolveStageIndex(state);
        var result = new List<LineupRecommendation>();

        foreach (OnlineMetaComp comp in meta.Comps)
        {
            var units = comp.Units.Where(u => !string.IsNullOrWhiteSpace(u.HeroName)).ToList();
            if (units.Count < 3) continue;

            var allHeroes = units.Select(u => u.HeroName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var coreHeroes = units
                .Where(u => u.EquipmentNames.Count(x => !string.IsNullOrWhiteSpace(x)) >= 1)
                .Select(u => u.HeroName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var flexHeroes = allHeroes.Where(x => !coreHeroes.Contains(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 在线详情偶尔拿不到装备时，不胡乱指定核心；此时所有英雄按普通权重处理。
            var boardCore = board.InferredHeroes.Where(coreHeroes.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var boardFlex = board.InferredHeroes.Where(flexHeroes.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shopCore = state.ShopHeroes.Where(coreHeroes.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shopFlex = state.ShopHeroes.Where(flexHeroes.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            (double traitCoverage, List<string> matchedTraits) = MatchBoardTraits(board.Traits, allHeroes);

            List<string> desiredEquipments = units
                .SelectMany(u => u.EquipmentNames)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            var equipmentSet = desiredEquipments.ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> owned = state.Equipments.Concat(state.Emblems)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            List<string> directMatches = owned.Where(equipmentSet.Contains).ToList();
            List<string> componentMatches = MatchComponents(owned, desiredEquipments, directMatches);

            // 局面匹配优先，在线统计作为先验。不会因为一个高胜率阵容与当前棋盘毫无关系就排第一。
            double boardScore = boardCore.Count * 20.0 + boardFlex.Count * 8.0;
            if (coreHeroes.Count == 0)
                boardScore = boardFlex.Count * 11.0;

            double traitScore = traitCoverage * 28.0;
            double shopScore = shopCore.Count * 5.0 + shopFlex.Count * 2.0;
            if (coreHeroes.Count == 0)
                shopScore = shopFlex.Count * 2.5;

            double itemScore = directMatches.Count * 5.5 + componentMatches.Count * 2.0;
            double metaScore = CalculateMetaStrength(comp);
            double strategyScore = CalculateStrategicFit(comp, state);

            double score = Math.Clamp(boardScore + traitScore + shopScore + itemScore + metaScore + strategyScore, 0, 100);

            var matchedHeroes = boardCore.Concat(boardFlex).Concat(shopCore).Concat(shopFlex)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var matchedEquipments = directMatches.Concat(componentMatches)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            result.Add(new LineupRecommendation
            {
                Name = comp.Name,
                Score = score,
                StageIndex = stageIndex,
                MatchedHeroes = matchedHeroes,
                MatchedEquipments = matchedEquipments,
                Source = comp.Source,
                MetaTier = comp.Tier,
                MetaWinRate = comp.WinRate,
                MetaTopFourRate = comp.TopFourRate,
                MetaPickRate = comp.PickRate,
                MetaAverageRank = comp.AverageRank,
                MetaTags = comp.Tags.ToList(),
                Reason = BuildOnlineReason(comp, board, boardCore, boardFlex, matchedTraits,
                    shopCore, shopFlex, directMatches, componentMatches, traitCoverage, strategyScore)
            });
        }

        return result
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.MetaAverageRank <= 0 ? 99 : x.MetaAverageRank)
            .ThenByDescending(x => x.MetaTopFourRate)
            .Take(Math.Max(1, top))
            .ToList();
    }

    private List<LineupRecommendation> RecommendLocalFallback(GameStateSnapshot state, int top)
    {
        int stageIndex = ResolveStageIndex(state);
        LiveBoardSnapshot board = LiveBoardState.GetSnapshot();
        var result = new List<LineupRecommendation>();

        foreach (LineUp lineUp in _lineUpService.GetLineUps())
        {
            if (lineUp.SubLineUps == null || lineUp.SubLineUps.Length == 0) continue;
            int safeIndex = Math.Clamp(stageIndex, 0, lineUp.SubLineUps.Length - 1);
            List<LineUpUnit> units = lineUp.SubLineUps[safeIndex].LineUpUnits ?? [];
            var heroSet = units.Select(u => u.HeroName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desiredEquipments = units.SelectMany(u => u.EquipmentNames ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var equipmentSet = desiredEquipments.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var matchedShopHeroes = state.ShopHeroes.Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var matchedBoardHeroes = board.InferredHeroes.Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            (double traitCoverage, List<string> matchedTraits) = MatchBoardTraits(board.Traits, heroSet);

            var owned = state.Equipments.Concat(state.Emblems).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var directMatches = owned.Where(equipmentSet.Contains).ToList();
            var componentMatches = MatchComponents(owned, desiredEquipments, directMatches);

            double score = Math.Min(100,
                matchedBoardHeroes.Count * 15.0 + traitCoverage * 38.0 + matchedShopHeroes.Count * 7.0 +
                directMatches.Count * 9.0 + componentMatches.Count * 3.5 + 4.0);

            result.Add(new LineupRecommendation
            {
                Name = lineUp.LineUpName,
                Score = score,
                StageIndex = safeIndex,
                Source = "本地阵容兜底",
                MatchedHeroes = matchedBoardHeroes.Concat(matchedShopHeroes)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MatchedEquipments = directMatches.Concat(componentMatches)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Reason = BuildFallbackReason(board, matchedBoardHeroes, matchedTraits, matchedShopHeroes,
                    directMatches, componentMatches, safeIndex, traitCoverage)
            });
        }

        return result.OrderByDescending(x => x.Score).ThenBy(x => x.Name).Take(Math.Max(1, top)).ToList();
    }

    private double CalculateMetaStrength(OnlineMetaComp comp)
    {
        double tier = comp.Tier switch
        {
            "S" => 15,
            "A" => 11,
            "B" => 7,
            "C" => 3,
            _ => 0
        };
        double avg = comp.AverageRank > 0 ? Math.Clamp((4.6 - comp.AverageRank) * 4.0, -4, 8) : 0;
        double win = Math.Clamp((comp.WinRate - 8.0) * 0.45, 0, 8);
        double top4 = Math.Clamp((comp.TopFourRate - 48.0) * 0.25, 0, 6);
        // 登场率仅作为样本可靠性微加分，不惩罚用户偏好的冷门强阵。
        double reliability = Math.Clamp(comp.PickRate * 0.40, 0, 4);
        return tier + avg + win + top4 + reliability;
    }

    private static double CalculateStrategicFit(OnlineMetaComp comp, GameStateSnapshot state)
    {
        string tags = string.Join(" ", comp.Tags).ToLowerInvariant();
        double score = 0;

        if (ContainsAny(tags, "5级d", "5级 d", "level 5", "5 reroll"))
            score += state.Level <= 5 ? 8 : state.Level == 6 ? 2 : -4;
        if (ContainsAny(tags, "6级d", "6级 d", "level 6", "6 reroll"))
            score += state.Level == 6 ? 8 : state.Level <= 5 ? 4 : state.Level == 7 ? 2 : -3;
        if (ContainsAny(tags, "7级d", "7级 d", "level 7", "7 reroll"))
            score += state.Level == 7 ? 8 : state.Level == 6 ? 4 : state.Level <= 5 ? 1 : -2;

        bool fast8 = ContainsAny(tags, "速8", "fast 8", "8级");
        bool fast9 = ContainsAny(tags, "速9", "fast 9", "9级");
        if (fast8)
        {
            if (state.Level >= 7 && state.Gold >= 25) score += 6;
            if (state.Hp is > 0 and < 55 && state.Gold < 20) score -= 7;
        }
        if (fast9)
        {
            if (state.Level >= 8 && state.Gold >= 30 && state.Hp >= 55) score += 8;
            if (state.Hp is > 0 and < 60 || state.Gold < 15) score -= 9;
        }

        return Math.Clamp(score, -12, 10);
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(text.Contains);

    private (double Coverage, List<string> MatchedTraits) MatchBoardTraits(
        Dictionary<string, int> observed,
        IEnumerable<string> heroNames)
    {
        if (observed.Count == 0 || _heroTraits.Count == 0) return (0, []);

        var lineupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string hero in heroNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
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
            if (available.TryGetValue(pair.Key, out int count)) available[pair.Key] = Math.Max(0, count - pair.Value);

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
                    if (!string.IsNullOrWhiteSpace(row.Name) && row.SyntheticPathway is { Length: > 0 })
                        _recipes[row.Name] = row.SyntheticPathway;
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
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        if (liveLevel > 0) return liveLevel <= 5 ? 0 : liveLevel <= 7 ? 1 : 2;
        if (state.Stage.StartsWith("2-")) return 0;
        if (state.Stage.StartsWith("3-")) return 1;
        return 2;
    }

    private static string BuildOnlineReason(
        OnlineMetaComp comp,
        LiveBoardSnapshot board,
        List<string> boardCore,
        List<string> boardFlex,
        List<string> matchedTraits,
        List<string> shopCore,
        List<string> shopFlex,
        List<string> directEquipments,
        List<string> components,
        double traitCoverage,
        double strategyScore)
    {
        var parts = new List<string>
        {
            $"{comp.Source} {comp.Tier}级",
            $"均次{comp.AverageRank:0.00}/前四{comp.TopFourRate:0.0}%/登顶{comp.WinRate:0.0}%/登场{comp.PickRate:0.00}%"
        };
        if (boardCore.Count > 0) parts.Add($"上场核心命中：{string.Join("、", boardCore)}");
        if (boardFlex.Count > 0) parts.Add($"上场挂件命中：{string.Join("、", boardFlex)}");
        if (matchedTraits.Count > 0) parts.Add($"羁绊覆盖{traitCoverage:P0}：{string.Join("、", matchedTraits)}");
        if (shopCore.Count > 0) parts.Add($"商店核心：{string.Join("、", shopCore)}");
        if (shopFlex.Count > 0) parts.Add($"商店可留：{string.Join("、", shopFlex)}");
        if (directEquipments.Count > 0) parts.Add($"装备命中：{string.Join("、", directEquipments)}");
        if (components.Count > 0) parts.Add($"散件可合：{string.Join("、", components)}");
        if (comp.Tags.Count > 0) parts.Add($"标签：{string.Join("/", comp.Tags.Take(4))}");
        if (strategyScore >= 4) parts.Add("当前等级/经济契合运营节奏");
        if (strategyScore <= -4) parts.Add("当前血量/经济与该运营节奏冲突");
        if (!board.HasBoardSignal && shopCore.Count == 0 && shopFlex.Count == 0)
            parts.Add("当前棋盘信号不足，主要按版本强度作为候选");
        return string.Join("；", parts);
    }

    private static string BuildFallbackReason(
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
        var parts = new List<string> { $"在线Meta不可用，按本地{stage}阵容兜底" };
        if (boardHeroes.Count > 0) parts.Add($"上场命中：{string.Join("、", boardHeroes)}");
        if (matchedTraits.Count > 0) parts.Add($"羁绊覆盖{traitCoverage:P0}：{string.Join("、", matchedTraits)}");
        if (shopHeroes.Count > 0) parts.Add($"商店命中：{string.Join("、", shopHeroes)}");
        if (directEquipments.Count > 0) parts.Add($"装备命中：{string.Join("、", directEquipments)}");
        if (components.Count > 0) parts.Add($"散件可合：{string.Join("、", components)}");
        return string.Join("；", parts);
    }
}
