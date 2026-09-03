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
        if (meta.HasData) return RecommendOnline(state, meta, top);
        return RecommendLocalFallback(state, top);
    }

    private List<LineupRecommendation> RecommendOnline(GameStateSnapshot state, OnlineMetaSnapshot meta, int top)
    {
        LiveBoardSnapshot board = LiveBoardState.GetSnapshot();
        int stageIndex = ResolveStageIndex(state);
        Dictionary<string, LineUp> localByName = BuildLocalLineupMap();
        string currentSelectedName = GetCurrentSelectedLineupName();
        var result = new List<LineupRecommendation>();
        double freshness = WinRateDecisionEngine.MetaFreshnessAdjustment(meta);
        string freshnessWarning = WinRateDecisionEngine.FreshnessWarning(meta);

        foreach (OnlineMetaComp comp in meta.Comps)
        {
            List<OnlineMetaUnit> finalUnits = comp.Units
                .Where(u => !string.IsNullOrWhiteSpace(u.HeroName))
                .ToList();
            if (finalUnits.Count < 3) continue;

            var finalCore = finalUnits
                .Where(u => u.EquipmentNames.Count(x => !string.IsNullOrWhiteSpace(x)) >= 1)
                .Select(u => u.HeroName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<LineUpUnit> stageUnits = GetStageUnits(localByName, comp.Name, stageIndex);
            bool stageTemplateFallback = stageUnits.Count < 3;
            if (stageTemplateFallback)
            {
                stageUnits = finalUnits.Select(u => new LineUpUnit
                {
                    HeroName = u.HeroName,
                    EquipmentNames = u.EquipmentNames.ToArray(),
                    Position = (u.PositionRow, u.PositionColumn)
                }).ToList();
            }

            var stageHeroes = stageUnits.Select(u => u.HeroName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stageCore = stageUnits
                .Where(u => (u.EquipmentNames ?? []).Any(x => !string.IsNullOrWhiteSpace(x)) || finalCore.Contains(u.HeroName))
                .Select(u => u.HeroName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stageFlex = stageHeroes.Where(x => !stageCore.Contains(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<string> boardCore = board.InferredHeroes.Where(stageCore.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> boardFlex = board.InferredHeroes.Where(stageFlex.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> shopCore = state.ShopHeroes.Where(stageCore.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> shopFlex = state.ShopHeroes.Where(stageFlex.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> heldCore = state.HeldHeroes.Where(stageCore.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> heldFlex = state.HeldHeroes.Where(stageFlex.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> contestedCore = state.ContestedHeroes
                .Where(h => stageCore.Contains(h) || finalCore.Contains(h))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            int heldCoreCopies = SumCopies(state.HeldHeroCounts, heldCore);
            int heldFlexCopies = SumCopies(state.HeldHeroCounts, heldFlex);
            int contestedCoreCopies = SumCopies(state.ContestedHeroCounts, contestedCore);

            (double traitCoverage, List<string> matchedTraits) = MatchBoardTraits(board.Traits, stageHeroes);
            List<string> targetTraits = GetTraits(stageHeroes);

            List<string> desiredEquipments = stageUnits
                .SelectMany(u => u.EquipmentNames ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            if (desiredEquipments.Count < 3)
            {
                desiredEquipments.AddRange(finalUnits.SelectMany(u => u.EquipmentNames)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            var equipmentSet = desiredEquipments.ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> owned = state.Equipments.Concat(state.Emblems)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            List<string> directMatches = owned.Where(equipmentSet.Contains).ToList();
            List<string> componentMatches = MatchComponents(owned, desiredEquipments, directMatches);

            WinRateDecisionEngine.EmblemFit emblemFit = WinRateDecisionEngine.ScoreEmblems(state.Emblems, targetTraits);
            WinRateDecisionEngine.AugmentFit augmentFit = WinRateDecisionEngine.ScoreAugments(
                comp, state.Augments, targetTraits, desiredEquipments);

            bool reroll = IsRerollComp(comp);
            double urgency = state.Hp switch
            {
                <= 0 => 1.0,
                <= 35 => 1.35,
                <= 55 => 1.18,
                _ => 1.0
            };

            double boardScore = Math.Min(30,
                (boardCore.Count * 11.0 + boardFlex.Count * 5.0) * urgency);
            if (stageCore.Count == 0)
                boardScore = Math.Min(26, boardFlex.Count * 7.5 * urgency);

            double traitScore = Math.Min(18, traitCoverage * 18.0);
            double shopScore = Math.Min(10, shopCore.Count * 3.5 + shopFlex.Count * 1.3);

            // 对D牌阵，实际持有张数比“是否有这张牌”重要得多；普通运营阵保持温和权重。
            double heldScore = reroll
                ? Math.Min(20, heldCore.Count * 1.5 + heldCoreCopies * 1.8 + heldFlexCopies * 0.35)
                : Math.Min(12, heldCore.Count * 3.0 + heldCoreCopies * 0.8 + heldFlexCopies * 0.25);

            double itemScore = Math.Min(13, directMatches.Count * 4.0 + componentMatches.Count * 1.6);
            double metaScore = WinRateDecisionEngine.CalculateMetaStrength(comp);
            double strategyScore = WinRateDecisionEngine.CalculateStrategicFit(comp, state);
            double transitionPenalty = WinRateDecisionEngine.CalculateTransitionPenalty(board, stageHeroes, state);

            double contestPenalty = contestedCore.Count == 0
                ? 0
                : reroll
                    ? Math.Min(32, contestedCore.Count * 3.0 + contestedCoreCopies * 2.2)
                    : Math.Min(14, contestedCore.Count * 2.5 + contestedCoreCopies * 0.9);

            // 核心张数明显领先时允许继续追；同行库存明显领先时再额外惩罚。
            double copyRaceAdjustment = 0;
            if (reroll)
            {
                if (heldCoreCopies >= 7 && contestedCoreCopies <= 3) copyRaceAdjustment += 7;
                else if (heldCoreCopies >= 5 && contestedCoreCopies <= heldCoreCopies) copyRaceAdjustment += 4;

                if (contestedCoreCopies >= heldCoreCopies + 5 && heldCoreCopies <= 4) copyRaceAdjustment -= 9;
                else if (contestedCoreCopies >= heldCoreCopies + 3 && heldCoreCopies <= 3) copyRaceAdjustment -= 5;
            }

            bool sameAsCurrent = !string.IsNullOrWhiteSpace(currentSelectedName) &&
                NormalizeName(currentSelectedName) == NormalizeName(comp.Name);
            bool hasRouteEvidence = sameAsCurrent &&
                (boardCore.Count + boardFlex.Count + heldCore.Count + heldFlex.Count > 0 ||
                 directMatches.Count + componentMatches.Count > 0 ||
                 emblemFit.Matches.Count > 0);
            double continuityBonus = hasRouteEvidence && stageIndex >= 1 && augmentFit.Score > -15 && strategyScore > -12
                ? stageIndex == 1 ? 4.0 : 6.0
                : 0;

            string contestWarning = BuildContestWarning(
                reroll, heldCore, contestedCore, heldCoreCopies, contestedCoreCopies, state);
            string templateWarning = stageTemplateFallback
                ? "当前阵容缺少对应阶段模板，暂用Meta终局单位估算；不要仅凭该结果锁阵。"
                : "";
            string reliabilityWarning = WinRateDecisionEngine.MetaReliabilityWarning(comp);
            string warning = JoinWarnings(augmentFit.Warning, contestWarning, templateWarning, reliabilityWarning, freshnessWarning);

            double rawScore =
                4 + boardScore + traitScore + shopScore + heldScore + itemScore +
                emblemFit.Score + augmentFit.Score + metaScore + strategyScore + freshness + continuityBonus + copyRaceAdjustment -
                transitionPenalty - contestPenalty;
            double score = V41ScoreCalibration.NormalizeFitScore(rawScore);

            List<string> matchedHeroes = boardCore.Concat(boardFlex).Concat(shopCore).Concat(shopFlex)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> matchedHeld = heldCore.Concat(heldFlex)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> matchedEquipments = directMatches.Concat(componentMatches)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            double confidence = WinRateDecisionEngine.CalculateConfidence(
                state, board, meta, matchedHeroes.Count + matchedHeld.Count, matchedEquipments.Count);
            confidence += V41ScoreCalibration.MetaConfidenceAdjustment(comp);
            if (stageTemplateFallback) confidence -= 10;
            if (state.HeldHeroes.Count > 0) confidence += heldCoreCopies >= 5 ? 7 : 4;
            if (state.ContestedHeroes.Count > 0) confidence += contestedCoreCopies >= 4 ? 6 : 4;
            confidence = Math.Clamp(confidence, 15, 100);

            string risk = WinRateDecisionEngine.ClassifyRisk(score, confidence, strategyScore, warning);
            string action = WinRateDecisionEngine.BuildNextAction(comp, state, stageIndex);
            action = ApplyCopyRaceAction(action, reroll, heldCoreCopies, contestedCoreCopies, state);

            result.Add(new LineupRecommendation
            {
                Name = comp.Name,
                Score = score,
                Confidence = confidence,
                StageIndex = stageIndex,
                MatchedHeroes = matchedHeroes,
                MatchedHeldHeroes = matchedHeld,
                ContestedCoreHeroes = contestedCore,
                MatchedHeldCopies = heldCoreCopies,
                ContestedCoreCopies = contestedCoreCopies,
                MatchedEquipments = matchedEquipments,
                MatchedAugments = augmentFit.Matches,
                MatchedEmblems = emblemFit.Matches,
                RiskLevel = risk,
                NextAction = action,
                Warning = warning,
                Source = comp.Source,
                MetaTier = comp.Tier,
                MetaWinRate = comp.WinRate,
                MetaTopFourRate = comp.TopFourRate,
                MetaPickRate = comp.PickRate,
                MetaAverageRank = comp.AverageRank,
                MetaTags = comp.Tags.ToList(),
                Reason = BuildOnlineReason(
                    comp, stageIndex, boardCore, boardFlex, matchedTraits, shopCore, shopFlex,
                    heldCore, heldFlex, contestedCore, heldCoreCopies, contestedCoreCopies,
                    directMatches, componentMatches, augmentFit.Matches, emblemFit.Matches,
                    traitCoverage, strategyScore, transitionPenalty, contestPenalty,
                    copyRaceAdjustment, continuityBonus, warning, action, state)
            });
        }

        List<LineupRecommendation> ordered = result
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Confidence)
            .ThenBy(x => x.MetaAverageRank <= 0 ? 99 : x.MetaAverageRank)
            .ThenByDescending(x => x.MetaTopFourRate)
            .Take(Math.Max(1, top))
            .ToList();

        if (stageIndex >= 1 && !string.IsNullOrWhiteSpace(currentSelectedName) && ordered.Count > 1)
        {
            int currentIndex = ordered.FindIndex(x =>
                NormalizeName(x.Name) == NormalizeName(currentSelectedName));
            if (currentIndex is > 0 and <= 2)
            {
                LineupRecommendation current = ordered[currentIndex];
                bool hasContinuityEvidence = current.Reason.Contains("当前已走该路线", StringComparison.OrdinalIgnoreCase);
                if (hasContinuityEvidence && ordered[0].Score - current.Score < 4.5 && current.RiskLevel != "高")
                {
                    ordered.RemoveAt(currentIndex);
                    ordered.Insert(0, current);
                    current.Reason += "；V4.1防抖：与新候选差距很小，保持当前路线以避免高成本无意义转阵";
                }
            }
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            double margin = i == 0
                ? ordered[i].Score - (ordered.Count > 1 ? ordered[1].Score : 0)
                : ordered[0].Score - ordered[i].Score;
            ordered[i].Decision = WinRateDecisionEngine.ClassifyDecision(
                ordered[i].Score, ordered[i].Confidence, margin, ordered[i].RiskLevel, i);
            ordered[i].Reason += $"；V4.1决策：{ordered[i].Decision}，风险{ordered[i].RiskLevel}，置信度{ordered[i].Confidence:0}%";
        }

        return ordered;
    }

    private List<LineupRecommendation> RecommendLocalFallback(GameStateSnapshot state, int top)
    {
        int stageIndex = ResolveStageIndex(state);
        LiveBoardSnapshot board = LiveBoardState.GetSnapshot();
        string currentSelectedName = GetCurrentSelectedLineupName();
        var result = new List<LineupRecommendation>();

        foreach (LineUp lineUp in _lineUpService.GetLineUps())
        {
            if (lineUp.SubLineUps == null || lineUp.SubLineUps.Length == 0) continue;
            int safeIndex = Math.Clamp(stageIndex, 0, lineUp.SubLineUps.Length - 1);
            List<LineUpUnit> units = lineUp.SubLineUps[safeIndex].LineUpUnits ?? [];
            var heroSet = units.Select(u => u.HeroName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (heroSet.Count < 3) continue;

            List<string> desiredEquipments = units.SelectMany(u => u.EquipmentNames ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var equipmentSet = desiredEquipments.ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> matchedShopHeroes = state.ShopHeroes.Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> matchedBoardHeroes = board.InferredHeroes.Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> matchedHeldHeroes = state.HeldHeroes.Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> contestedHeroes = state.ContestedHeroes.Where(heroSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int heldCopies = SumCopies(state.HeldHeroCounts, matchedHeldHeroes);
            int contestedCopies = SumCopies(state.ContestedHeroCounts, contestedHeroes);

            (double traitCoverage, List<string> matchedTraits) = MatchBoardTraits(board.Traits, heroSet);
            List<string> owned = state.Equipments.Concat(state.Emblems)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            List<string> directMatches = owned.Where(equipmentSet.Contains).ToList();
            List<string> componentMatches = MatchComponents(owned, desiredEquipments, directMatches);
            List<string> targetTraits = GetTraits(heroSet);
            WinRateDecisionEngine.EmblemFit emblemFit = WinRateDecisionEngine.ScoreEmblems(state.Emblems, targetTraits);

            double transitionPenalty = WinRateDecisionEngine.CalculateTransitionPenalty(board, heroSet, state);
            bool sameAsCurrent = NormalizeName(lineUp.LineUpName) == NormalizeName(currentSelectedName);
            bool hasRouteEvidence = sameAsCurrent &&
                (matchedBoardHeroes.Count + matchedHeldHeroes.Count > 0 ||
                 directMatches.Count + componentMatches.Count > 0 ||
                 emblemFit.Matches.Count > 0);
            double continuityBonus = stageIndex >= 1 && hasRouteEvidence ? 4 : 0;
            double contestPenalty = Math.Min(14, contestedHeroes.Count * 2.5 + contestedCopies * 0.9);
            double heldScore = Math.Min(12, matchedHeldHeroes.Count * 2.5 + heldCopies * 0.8);
            double rawScore =
                8 + matchedBoardHeroes.Count * 11.0 + traitCoverage * 24.0 + matchedShopHeroes.Count * 3.0 +
                heldScore + directMatches.Count * 5.0 + componentMatches.Count * 2.0 +
                emblemFit.Score + continuityBonus - transitionPenalty - contestPenalty;
            double score = V41ScoreCalibration.NormalizeFitScore(rawScore);
            double confidence = Math.Clamp(25 + (board.HasBoardSignal ? 30 : 0) +
                (state.Equipments.Count > 0 ? 15 : 0) + (state.Level > 0 ? 10 : 0) +
                (state.HeldHeroes.Count > 0 ? 5 : 0) + (state.ContestedHeroes.Count > 0 ? 5 : 0), 20, 85);
            string action = stageIndex switch
            {
                0 => "在线Meta不可用：先用本地前期模板保血，不要提前锁死高费阵容。",
                1 => "在线Meta不可用：按本地中期骨架补质量，保持经济与血量平衡。",
                _ => "在线Meta不可用：按本地后期阵容补核心两星，并尽快恢复在线Meta。"
            };

            string warning = contestedHeroes.Count > 0
                ? $"本地目标棋子被同行争抢：{FormatCountedHeroes(state.ContestedHeroCounts, contestedHeroes)}。"
                : "当前未使用在线Meta，建议在安全时手动刷新Meta阵容。";

            result.Add(new LineupRecommendation
            {
                Name = lineUp.LineUpName,
                Score = score,
                Confidence = confidence,
                StageIndex = safeIndex,
                Source = "本地阵容兜底",
                MatchedHeroes = matchedBoardHeroes.Concat(matchedShopHeroes)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MatchedHeldHeroes = matchedHeldHeroes,
                ContestedCoreHeroes = contestedHeroes,
                MatchedHeldCopies = heldCopies,
                ContestedCoreCopies = contestedCopies,
                MatchedEquipments = directMatches.Concat(componentMatches)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MatchedEmblems = emblemFit.Matches,
                RiskLevel = contestedCopies >= 8 ? "高" : "中",
                NextAction = action,
                Warning = warning,
                Reason = BuildFallbackReason(matchedBoardHeroes, matchedTraits, matchedShopHeroes,
                    matchedHeldHeroes, contestedHeroes, heldCopies, contestedCopies,
                    directMatches, componentMatches, emblemFit.Matches,
                    safeIndex, traitCoverage, action, state)
            });
        }

        List<LineupRecommendation> ordered = result
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Confidence)
            .Take(Math.Max(1, top)).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Decision = i == 0 && ordered[i].Score >= 58 && ordered[i].Confidence >= 50 && ordered[i].RiskLevel != "高"
                ? "主推"
                : i <= 1 ? "观察" : "备选";
            ordered[i].Reason += $"；V4.1决策：{ordered[i].Decision}，置信度{ordered[i].Confidence:0}%";
        }
        return ordered;
    }

    private static string BuildContestWarning(
        bool reroll,
        List<string> heldCore,
        List<string> contestedCore,
        int heldCoreCopies,
        int contestedCoreCopies,
        GameStateSnapshot state)
    {
        if (contestedCore.Count == 0) return "";
        string contestedText = FormatCountedHeroes(state.ContestedHeroCounts, contestedCore);
        if (!reroll)
            return $"核心牌被同行争抢：{contestedText}；预计搜牌成本上升。";

        string heldText = heldCore.Count == 0 ? "0张" : FormatCountedHeroes(state.HeldHeroCounts, heldCore);
        if (contestedCoreCopies >= heldCoreCopies + 5 && heldCoreCopies <= 4)
            return $"追三竞争明显不利：你持有{heldText}，同行可见约{contestedText}；除非装备/强化极度绑定，否则应准备转阵。";
        if (heldCoreCopies >= 6 && heldCoreCopies >= contestedCoreCopies + 2)
            return $"存在同行但你数量领先：你持有{heldText}，同行约{contestedText}；可继续追，但不要无底线D空。";
        return $"追三核心被同行争抢：你持有{heldText}，同行约{contestedText}；下一轮继续比较数量优势。";
    }

    private static string ApplyCopyRaceAction(
        string action,
        bool reroll,
        int heldCoreCopies,
        int contestedCoreCopies,
        GameStateSnapshot state)
    {
        if (!reroll) return action;

        if (heldCoreCopies >= 7 && contestedCoreCopies <= 3)
            return action + " 核心已接近三星，优先完成该核心后再转经济。";

        if (contestedCoreCopies >= heldCoreCopies + 5 && heldCoreCopies <= 4)
            return action + " 同行核心库存明显领先：本轮不要继续无上限追三，优先评估转阵。";

        if (heldCoreCopies >= 5)
            return action + $" 当前已持有核心约{heldCoreCopies}张，转阵前必须计算放弃这些牌的真实成本。";

        return action;
    }

    private Dictionary<string, LineUp> BuildLocalLineupMap()
    {
        return _lineUpService.GetLineUps()
            .Where(x => !string.IsNullOrWhiteSpace(x.LineUpName))
            .GroupBy(x => NormalizeName(x.LineUpName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    private string GetCurrentSelectedLineupName()
    {
        try { return _lineUpService.GetCurrentLineUp()?.LineUpName ?? ""; }
        catch { return ""; }
    }

    private static List<LineUpUnit> GetStageUnits(Dictionary<string, LineUp> map, string compName, int stageIndex)
    {
        if (!map.TryGetValue(NormalizeName(compName), out LineUp? lineUp) ||
            lineUp.SubLineUps == null || lineUp.SubLineUps.Length == 0)
            return [];
        int safe = Math.Clamp(stageIndex, 0, lineUp.SubLineUps.Length - 1);
        return lineUp.SubLineUps[safe].LineUpUnits ?? [];
    }

    private List<string> GetTraits(IEnumerable<string> heroNames)
    {
        return heroNames
            .Where(_heroTraits.ContainsKey)
            .SelectMany(h => _heroTraits[h])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
            if (available.TryGetValue(pair.Key, out int count))
                available[pair.Key] = Math.Max(0, count - pair.Value);

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
        => V41ScoreCalibration.ResolveStageIndex(state);

    private static bool IsRerollComp(OnlineMetaComp comp)
    {
        string tags = string.Join(" ", comp.Tags).ToLowerInvariant();
        return tags.Contains("reroll") || tags.Contains("d牌") || tags.Contains("刷新") ||
               tags.Contains("5级d") || tags.Contains("6级d") || tags.Contains("7级d") ||
               tags.Contains("level 5") || tags.Contains("level 6") || tags.Contains("level 7");
    }

    private static int SumCopies(Dictionary<string, int> counts, IEnumerable<string> heroes)
    {
        int total = 0;
        foreach (string hero in heroes.Distinct(StringComparer.OrdinalIgnoreCase))
            total += counts.TryGetValue(hero, out int count) ? Math.Max(1, count) : 1;
        return total;
    }

    private static string FormatCountedHeroes(Dictionary<string, int> counts, IEnumerable<string> heroes)
    {
        List<string> values = heroes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(hero => counts.TryGetValue(hero, out int count) && count > 1 ? $"{hero}×{count}" : hero)
            .ToList();
        return string.Join("、", values);
    }

    private static string BuildOnlineReason(
        OnlineMetaComp comp,
        int stageIndex,
        List<string> boardCore,
        List<string> boardFlex,
        List<string> matchedTraits,
        List<string> shopCore,
        List<string> shopFlex,
        List<string> heldCore,
        List<string> heldFlex,
        List<string> contestedCore,
        int heldCoreCopies,
        int contestedCoreCopies,
        List<string> directEquipments,
        List<string> components,
        List<string> augmentMatches,
        List<string> emblemMatches,
        double traitCoverage,
        double strategyScore,
        double transitionPenalty,
        double contestPenalty,
        double copyRaceAdjustment,
        double continuityBonus,
        string warning,
        string action,
        GameStateSnapshot state)
    {
        string stage = stageIndex switch { 0 => "前期", 1 => "中期", _ => "后期" };
        var parts = new List<string>
        {
            $"{comp.Source} {comp.Tier}级",
            $"均次{comp.AverageRank:0.00}/前四{comp.TopFourRate:0.0}%/登顶{comp.WinRate:0.0}%/登场{comp.PickRate:0.00}%",
            $"按本地{stage}模板评估"
        };
        if (boardCore.Count > 0) parts.Add($"上场核心：{string.Join("、", boardCore)}");
        if (boardFlex.Count > 0) parts.Add($"上场过渡：{string.Join("、", boardFlex)}");
        if (matchedTraits.Count > 0) parts.Add($"羁绊覆盖{traitCoverage:P0}：{string.Join("、", matchedTraits)}");
        if (heldCore.Count > 0) parts.Add($"持有核心：{FormatCountedHeroes(state.HeldHeroCounts, heldCore)}（合计{heldCoreCopies}张）");
        if (heldFlex.Count > 0) parts.Add($"持有过渡：{FormatCountedHeroes(state.HeldHeroCounts, heldFlex)}");
        if (shopCore.Count > 0) parts.Add($"商店核心：{string.Join("、", shopCore)}");
        if (shopFlex.Count > 0) parts.Add($"商店可留：{string.Join("、", shopFlex)}");
        if (directEquipments.Count > 0) parts.Add($"装备命中：{string.Join("、", directEquipments)}");
        if (components.Count > 0) parts.Add($"散件可合：{string.Join("、", components)}");
        if (augmentMatches.Count > 0) parts.Add($"强化适配：{string.Join("、", augmentMatches)}");
        if (emblemMatches.Count > 0) parts.Add($"纹章适配：{string.Join("、", emblemMatches)}");
        if (contestedCore.Count > 0) parts.Add($"同行争抢：{FormatCountedHeroes(state.ContestedHeroCounts, contestedCore)}（约{contestedCoreCopies}张，-{contestPenalty:0.0}）");
        if (copyRaceAdjustment != 0) parts.Add($"追三星数量竞争调整{copyRaceAdjustment:+0.0;-0.0}");
        if (continuityBonus > 0) parts.Add($"当前已走该路线，减少无意义转阵(+{continuityBonus:0})");
        if (comp.Tags.Count > 0) parts.Add($"运营标签：{string.Join("/", comp.Tags.Take(4))}");
        if (strategyScore >= 5) parts.Add("当前等级/经济/血量契合该运营节奏");
        if (strategyScore <= -5) parts.Add("当前局面与该运营节奏冲突");
        if (transitionPenalty >= 5) parts.Add($"转阵成本偏高(-{transitionPenalty:0.0})");
        if (!string.IsNullOrWhiteSpace(warning)) parts.Add($"警告：{warning}");
        parts.Add($"下一步：{action}");
        return string.Join("；", parts);
    }

    private static string BuildFallbackReason(
        List<string> boardHeroes,
        List<string> matchedTraits,
        List<string> shopHeroes,
        List<string> heldHeroes,
        List<string> contestedHeroes,
        int heldCopies,
        int contestedCopies,
        List<string> directEquipments,
        List<string> components,
        List<string> emblemMatches,
        int stageIndex,
        double traitCoverage,
        string action,
        GameStateSnapshot state)
    {
        string stage = stageIndex switch { 0 => "前期", 1 => "中期", _ => "后期" };
        var parts = new List<string> { $"在线Meta不可用，按本地{stage}阵容兜底" };
        if (boardHeroes.Count > 0) parts.Add($"上场命中：{string.Join("、", boardHeroes)}");
        if (matchedTraits.Count > 0) parts.Add($"羁绊覆盖{traitCoverage:P0}：{string.Join("、", matchedTraits)}");
        if (heldHeroes.Count > 0) parts.Add($"持有：{FormatCountedHeroes(state.HeldHeroCounts, heldHeroes)}（{heldCopies}张）");
        if (shopHeroes.Count > 0) parts.Add($"商店命中：{string.Join("、", shopHeroes)}");
        if (directEquipments.Count > 0) parts.Add($"装备命中：{string.Join("、", directEquipments)}");
        if (components.Count > 0) parts.Add($"散件可合：{string.Join("、", components)}");
        if (emblemMatches.Count > 0) parts.Add($"纹章适配：{string.Join("、", emblemMatches)}");
        if (contestedHeroes.Count > 0) parts.Add($"同行争抢：{FormatCountedHeroes(state.ContestedHeroCounts, contestedHeroes)}（约{contestedCopies}张）");
        parts.Add($"下一步：{action}");
        return string.Join("；", parts);
    }

    private static string JoinWarnings(params string[] values)
        => string.Join("；", values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());

    private static string NormalizeName(string name)
    {
        return new string((name ?? "")
            .Where(c => char.IsLetterOrDigit(c) || c >= 0x4e00)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
