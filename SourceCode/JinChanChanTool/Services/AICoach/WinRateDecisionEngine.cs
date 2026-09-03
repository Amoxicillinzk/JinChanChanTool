namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// V4.1 的确定性运营决策层。
/// 目标不是预测“必赢”，而是在信息不完整时优先减少最常见的掉分错误：
/// 低血量仍贪人口、D牌阵错过等级窗口、缺少专属强化却硬玩、纹章方向错误、Meta缓存过旧。
/// </summary>
public static class WinRateDecisionEngine
{
    public sealed record AugmentFit(double Score, List<string> Matches, string Warning);
    public sealed record EmblemFit(double Score, List<string> Matches);

    public static double CalculateMetaStrength(OnlineMetaComp comp)
    {
        double tier = comp.Tier switch
        {
            "S" => 9.0,
            "A" => 7.0,
            "B" => 4.5,
            "C" => 2.0,
            _ => 0
        };
        double avg = comp.AverageRank > 0 ? Math.Clamp((4.55 - comp.AverageRank) * 3.0, -3.0, 6.0) : 0;
        double win = comp.WinRate > 0 ? Math.Clamp((comp.WinRate - 10.0) * 0.28, -1.5, 4.5) : 0;
        double top4 = comp.TopFourRate > 0 ? Math.Clamp((comp.TopFourRate - 50.0) * 0.18, -2.0, 4.0) : 0;

        double reliability = comp.PickRate switch
        {
            <= 0 => -3.5,
            < 0.05 => -3.0,
            < 0.12 => -1.8,
            < 0.30 => -0.5,
            >= 2.0 => 2.0,
            >= 1.0 => 1.2,
            >= 0.5 => 0.6,
            _ => 0
        };
        return Math.Clamp(tier + avg + win + top4 + reliability, 0, 22);
    }

    public static string MetaReliabilityWarning(OnlineMetaComp comp)
    {
        if (comp.PickRate <= 0) return "Meta登场率未知，统计样本可靠性不足。";
        if (comp.PickRate < 0.05) return $"Meta登场率仅{comp.PickRate:0.00}%，属于极低样本冷门阵容；可观察但不要仅凭统计锁阵。";
        if (comp.PickRate < 0.12) return $"Meta登场率仅{comp.PickRate:0.00}%，样本偏低；需要更强的棋盘/装备证据再主推。";
        return "";
    }

    public static double MetaFreshnessAdjustment(OnlineMetaSnapshot meta)
    {
        if (meta.UpdatedAt == DateTime.MinValue) return -8;
        double hours = Math.Max(0, (DateTime.Now - meta.UpdatedAt).TotalHours);
        if (hours <= 8) return 3;
        if (hours <= 24) return 1;
        if (hours <= 48) return -3;
        if (hours <= 96) return -7;
        return -12;
    }

    public static double CalculateStrategicFit(OnlineMetaComp comp, GameStateSnapshot state)
    {
        string tags = string.Join(" ", comp.Tags).ToLowerInvariant();
        double score = 0;
        bool levelKnown = state.Level > 0;
        bool hpKnown = IsHpKnown(state);
        bool goldKnown = IsGoldKnown(state);
        int stageMajor = ResolveStageMajor(state);
        bool reroll5 = ContainsAny(tags, "5级d", "5级 d", "level 5", "5 reroll");
        bool reroll6 = ContainsAny(tags, "6级d", "6级 d", "level 6", "6 reroll");
        bool reroll7 = ContainsAny(tags, "7级d", "7级 d", "level 7", "7 reroll", "7/8级d");
        bool fast8 = ContainsAny(tags, "速8", "fast 8", "8级");
        bool fast9 = ContainsAny(tags, "速9", "fast 9", "9级");

        if (levelKnown && reroll5)
        {
            score += state.Level <= 5 ? 10 : state.Level == 6 ? 1 : -8;
            if (goldKnown && state.Level <= 5 && state.Gold >= 35) score += 3;
            if (stageMajor >= 5) score -= 8;
            else if (stageMajor >= 4) score -= 4;
        }
        if (levelKnown && reroll6)
        {
            score += state.Level == 6 ? 10 : state.Level <= 5 ? 4 : state.Level == 7 ? 1 : -6;
            if (goldKnown && state.Level == 6 && state.Gold >= 30) score += 3;
            if (stageMajor >= 5) score -= 6;
            else if (stageMajor >= 4) score -= 2;
        }
        if (levelKnown && reroll7)
        {
            score += state.Level == 7 ? 10 : state.Level == 6 ? 5 : state.Level <= 5 ? 1 : state.Level == 8 ? 0 : -5;
            if (goldKnown && state.Level is 7 or 8 && state.Gold >= 30) score += 3;
            if (stageMajor >= 6) score -= 6;
            else if (stageMajor >= 5) score -= 3;
        }

        if (fast8 && levelKnown)
        {
            if (hpKnown && goldKnown && state.Level >= 7 && state.Gold >= 30 && state.Hp >= 55) score += 8;
            else if (hpKnown && goldKnown && state.Level <= 6 && state.Hp >= 70 && state.Gold >= 30) score += 4;
            if (hpKnown && state.Hp < 50 && state.Level < 8) score -= 8;
            if (goldKnown && state.Gold < 15 && state.Level < 8) score -= 4;
            if (stageMajor >= 5 && state.Level < 8) score -= 5;
        }
        if (fast9 && levelKnown)
        {
            if (hpKnown && goldKnown && state.Level >= 8 && state.Gold >= 35 && state.Hp >= 60) score += 10;
            else if (hpKnown && goldKnown && state.Level >= 7 && state.Gold >= 50 && state.Hp >= 75) score += 5;
            if (hpKnown && state.Hp < 60) score -= 11;
            if (goldKnown && state.Gold < 25 && state.Level < 9) score -= 7;
            if (stageMajor >= 6 && state.Level < 9) score -= 4;
        }

        if (hpKnown && state.Hp <= 35 && (fast8 || fast9)) score -= 5;
        return Math.Clamp(score, -22, 14);
    }

    public static AugmentFit ScoreAugments(
        OnlineMetaComp comp,
        IReadOnlyCollection<string> augments,
        IReadOnlyCollection<string> targetTraits,
        IReadOnlyCollection<string> desiredEquipment)
    {
        if (augments.Count == 0)
        {
            if (LooksLikeUnrivaledComp(comp.Name))
                return new AugmentFit(-18, [], "专属强化未确认：该阵容疑似依赖“Unrivaled/宿敌”；在确认拿到前不应锁定。");
            return new AugmentFit(0, [], "");
        }

        string tags = string.Join(" ", comp.Tags).ToLowerInvariant();
        bool reroll = ContainsAny(tags, "reroll", "d牌", "刷新");
        bool fast = ContainsAny(tags, "fast 8", "fast 9", "速8", "速9", "8级", "9级");
        bool apLean = desiredEquipment.Any(IsApItem);
        bool adLean = desiredEquipment.Any(IsAdItem);
        bool tankLean = desiredEquipment.Any(IsTankItem);

        double score = 0;
        var matches = new List<string>();
        string warning = "";

        bool unrivaledPresent = augments.Any(a => ContainsAny(a.ToLowerInvariant(), "unrivaled", "宿敌"));
        if (LooksLikeUnrivaledComp(comp.Name))
        {
            if (unrivaledPresent)
            {
                score += 22;
                matches.Add(augments.First(a => ContainsAny(a.ToLowerInvariant(), "unrivaled", "宿敌")) + "（专属启动）");
            }
            else
            {
                score -= 38;
                warning = "缺少 Unrivaled/宿敌 专属强化：这套阵容不应锁定。";
            }
        }

        foreach (string augment in augments.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            string a = augment.Trim();
            string lower = a.ToLowerInvariant();
            double local = 0;

            foreach (string trait in targetTraits.Where(x => x.Length >= 2))
            {
                if (a.Contains(trait, StringComparison.OrdinalIgnoreCase))
                {
                    local += 5;
                    break;
                }
            }

            if (reroll && ContainsAny(lower, "刷新", "d牌", "商店", "门票", "复制", "roll", "reroll", "army", "grab bag"))
                local += 4.5;
            if (fast && ContainsAny(lower, "经济", "金币", "利息", "经验", "升级", "生日", "投资", "study", "epoch", "level", "late game", "upward"))
                local += 4.5;
            if (apLean && ContainsAny(lower, "珠光", "法强", "魔法", "施法", "法杖", "wand", "magic", "lotus"))
                local += 3;
            if (adLean && ContainsAny(lower, "攻击", "攻速", "暴击", "弓", "剑", "处决", "bow", "sword", "blade"))
                local += 3;
            if (tankLean && ContainsAny(lower, "护盾", "治疗", "生命", "抗性", "防御", "tank", "shield"))
                local += 2;
            if (ContainsAny(lower, "装备", "组件", "锻造", "神器", "forge", "item", "grab bag"))
                local += 1.5;

            if (local > 0)
            {
                score += Math.Min(local, 7);
                matches.Add(a);
            }
        }

        return new AugmentFit(Math.Clamp(score, -40, 18), matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), warning);
    }

    public static EmblemFit ScoreEmblems(IReadOnlyCollection<string> emblems, IReadOnlyCollection<string> targetTraits)
    {
        if (emblems.Count == 0 || targetTraits.Count == 0) return new EmblemFit(0, []);
        double score = 0;
        var matches = new List<string>();
        foreach (string emblem in emblems.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            string baseName = NormalizeEmblemName(emblem);
            if (baseName.Length < 2) continue;
            string? hit = targetTraits.FirstOrDefault(t =>
                t.Contains(baseName, StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (hit == null) continue;
            score += 8;
            matches.Add($"{emblem}→{hit}");
        }
        return new EmblemFit(Math.Min(score, 16), matches);
    }

    public static double CalculateTransitionPenalty(
        LiveBoardSnapshot board,
        IReadOnlyCollection<string> targetHeroes,
        GameStateSnapshot state)
    {
        if (board.InferredHeroes.Count == 0) return 0;
        int offPath = board.InferredHeroes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(h => !targetHeroes.Contains(h, StringComparer.OrdinalIgnoreCase));
        if (offPath == 0) return 0;

        bool hpKnown = IsHpKnown(state);
        double each = !hpKnown
            ? 1.5
            : state.Hp <= 35
                ? 4.0
                : state.Hp <= 55
                    ? 3.0
                    : 1.8;
        if (state.Level >= 7) each += 0.7;
        return Math.Min(16, offPath * each);
    }

    public static double CalculateConfidence(
        GameStateSnapshot state,
        LiveBoardSnapshot board,
        OnlineMetaSnapshot meta,
        int matchedHeroCount,
        int matchedItemCount)
    {
        double value = 22;
        if (board.InferredHeroes.Count > 0) value += 22;
        if (board.Traits.Count > 0) value += 18;
        if (state.Level > 0 || !string.IsNullOrWhiteSpace(state.Stage)) value += 10;
        if (IsHpKnown(state)) value += 6;
        if (IsGoldKnown(state)) value += 5;
        if (state.Equipments.Count + state.Emblems.Count > 0) value += 9;
        if (state.ShopHeroes.Length > 0) value += 3;
        value += Math.Min(5, matchedHeroCount * 1.5);
        value += Math.Min(4, matchedItemCount);
        if ((DateTime.Now - meta.UpdatedAt).TotalHours > 48) value -= 12;
        return Math.Clamp(value, 15, 100);
    }

    public static string BuildNextAction(OnlineMetaComp comp, GameStateSnapshot state, int stageIndex)
    {
        string tags = string.Join(" ", comp.Tags).ToLowerInvariant();
        bool d5 = ContainsAny(tags, "5级d", "level 5", "5 reroll");
        bool d6 = ContainsAny(tags, "6级d", "level 6", "6 reroll");
        bool d7 = ContainsAny(tags, "7级d", "7/8级d", "level 7", "7 reroll");
        bool fast8 = ContainsAny(tags, "速8", "fast 8", "8级");
        bool fast9 = ContainsAny(tags, "速9", "fast 9", "9级");
        bool goldKnown = IsGoldKnown(state);
        bool hpKnown = IsHpKnown(state);
        int stageMajor = ResolveStageMajor(state);

        if (hpKnown && state.Hp <= 30)
        {
            if (!goldKnown) return "危险血线：金币未知，先上最强战力并合通用成装；不要贪人口或长期经济。";
            if (state.Gold >= 20) return "危险血线：本轮优先花钱提升即时战力，D到能稳住再停，不再贪长期经济。";
            return "危险血线：优先上最强战力和成装，暂停高成本转阵。";
        }

        if (state.Level <= 0)
            return "等级尚未可靠识别：先确认等级并按当前最强战力保血；暂不执行精确D牌或Fast8/9指令。";

        if (d5)
        {
            if (state.Level < 5) return "不要主动多升人口；攒钱到5级进入D牌窗口。";
            if (state.Level == 5)
            {
                if (!goldKnown) return "5级D牌窗口已到，但金币未知：先确认经济，再决定慢D还是止血搜牌。";
                if (stageMajor >= 5) return "5级D牌已严重超时：不要再守50利息；本轮完成核心质量，仍差很多就停止追三并准备提人口。";
                if (stageMajor >= 4 || hpKnown && state.Hp < 55)
                    return "5级D牌进入时间压力：优先D到20~30完成关键两星/接近三星，再根据血量恢复经济；不要机械卡50。";
                return state.Gold >= 50 ? "5级卡50利息慢D核心三星；血量跌破55或进入4阶段后停止机械守50。" : "5级先攒经济，除非掉血严重否则不要把钱D空。";
            }
            return "已错过最佳5级D牌窗口；只有核心张数明显领先时继续，否则准备转阵/提人口。";
        }
        if (d6)
        {
            if (state.Level < 6) return "升到6级后进入主D窗口，途中只做高性价比补强。";
            if (state.Level == 6)
            {
                if (!goldKnown) return "6级主D窗口已到，但金币未知：先确认经济；不要因为未知值直接把钱搜空。";
                if (stageMajor >= 5) return "6级D牌已经超时：本轮优先把主C/主坦质量做出来，不能继续卡50慢D；质量仍不足就转提人口。";
                if (stageMajor >= 4 || hpKnown && state.Hp < 55)
                    return "6级主D进入压力窗口：可D到20~30稳住主C主坦，再恢复经济；不要为了利息连续掉大血。";
                return state.Gold >= 50 ? "6级卡利息慢D；进入4阶段或血量跌破55后改为主动提质量。" : "6级先补经济，下一轮集中D牌。";
            }
            return "已经高于6级窗口；检查核心数量，不够就降低追三星执念并转向提人口。";
        }
        if (d7)
        {
            if (state.Level < 7) return "优先到7级再启动D牌，不要在低等级提前把钱花光。";
            if (state.Level is 7 or 8)
            {
                if (!goldKnown) return "当前处于7级D牌窗口，但金币未知：先确认经济，再决定慢D或止血大搜。";
                if (stageMajor >= 6) return "7级D牌已严重拖后：本轮把剩余经济转成战力；核心仍差很多就放弃无底线追三。";
                if (stageMajor >= 5 || hpKnown && state.Hp < 50)
                    return "7级D牌进入后期压力：优先D到20左右完成两星/关键三星进度，再判断是否继续；不要卡50等死。";
                return state.Gold >= 40 ? "当前就是7级D牌窗口：卡30~50利息追核心；血量跌破50后改为主动止血。" : "保持20~30金币底线，小幅D牌补两星后继续恢复经济。";
            }
        }
        if (fast9)
        {
            if (!hpKnown) return "血量未知或HUD血量已过期：不要机械速9；先确认血量并判断8级棋盘能否稳定作战。";
            if (!goldKnown) return "金币未知或HUD金币已过期：暂不执行速9；先确认经济，8级只做必要补强并维持血量。";
            if (state.Level >= 9) return "已到9级：把经济转成高费两星与完整前排，不再存无效金币。";
            if (stageMajor >= 6 && state.Level < 9) return "已进入6阶段仍未9级：先确保8级棋盘质量；只有能稳定存活且经济够用才继续冲9。";
            if (state.Hp >= 65 && state.Gold >= 45) return "保持连胜/血量，优先攒钱拉9；8级只小D止血。";
            if (state.Hp < 55) return "暂缓速9：先在8级D出稳定两星四费/前排，血线稳定后再考虑9。";
            return "以经济和血量为门槛冲9，不要为了9级牺牲两回合以上战力。";
        }
        if (fast8)
        {
            if (!hpKnown) return "血量未知或HUD血量已过期：先确认血量；当前保持经济但不要为了拉8连续牺牲战力。";
            if (!goldKnown) return "金币未知或HUD金币已过期：先确认经济；当前只买能直接增强棋盘的牌，不机械拉8或大D。";
            if (stageMajor >= 5 && state.Level < 8) return "已进入5阶段仍未8级：停止标准速8脚本，先把当前经济转成即时战力，再决定是否还能拉8。";
            if (state.Level < 7) return "保持经济，以拉7/8为主，当前只买能直接增强棋盘的牌。";
            if (state.Level == 7 && state.Hp < 50) return "7级先D一轮止血，形成两星前排/核心后再拉8。";
            if (state.Level < 8) return state.Gold >= 35 ? "经济允许：优先拉8进入主D窗口。" : "先恢复到30~40金币，再拉8集中搜核心。";
            return state.Gold >= 30 ? "8级集中D核心两星；阵容稳定后再存钱考虑9。" : "8级小D补质量，至少保留10~20金币避免完全断经济。";
        }

        return stageIndex switch
        {
            0 => "前期以最强战力保血，优先合能直接提升战力的通用装备，不要过早锁死阵容。",
            1 => "中期围绕推荐骨架补核心羁绊；经济健康时保持30~50金币，血线差就提前花钱提质量。",
            _ => "后期把经济转成主C/主坦两星和高质量挂件；不要为无关羁绊牺牲单卡质量。"
        };
    }

    public static string ClassifyRisk(double score, double confidence, double strategyScore, string warning)
    {
        bool hardWarning = ContainsAny(warning ?? "",
            "不应锁定", "缺少 Unrivaled", "强烈建议先刷新", "硬条件", "无法成立");

        if (hardWarning || score < 38 || strategyScore <= -10) return "高";
        if (!string.IsNullOrWhiteSpace(warning) || score < 58 || confidence < 55 || strategyScore < -3) return "中";
        return "低";
    }

    public static string ClassifyDecision(double score, double confidence, double margin, string risk, int rank)
    {
        if (risk == "高" && score < 50) return "不建议";
        if (confidence < 45)
            return rank == 0 && score >= 45 ? "观察" : "备选";
        if (rank == 0 && score >= 74 && confidence >= 65 && margin >= 7 && risk == "低") return "锁定";
        if (rank == 0 && score >= 58 && confidence >= 50 && risk != "高") return "主推";
        if (rank <= 1 && score >= 48) return "观察";
        return "备选";
    }

    public static string FreshnessWarning(OnlineMetaSnapshot meta)
    {
        if (meta.UpdatedAt == DateTime.MinValue) return "Meta更新时间未知，建议手动刷新。";
        double hours = (DateTime.Now - meta.UpdatedAt).TotalHours;
        if (hours > 96) return $"Meta缓存已超过{hours / 24:0.0}天，强烈建议先刷新Meta阵容。";
        if (hours > 48) return $"Meta缓存已超过{hours:0}小时，建议刷新后再锁阵。";
        return "";
    }

    private static bool IsGoldKnown(GameStateSnapshot state)
    {
        try
        {
            LiveHudSnapshot hud = LiveHudState.GetSnapshot();
            if (hud.Gold.HasValue && state.Gold == hud.Gold.Value)
                return IsFresh(hud.GoldAt, TimeSpan.FromSeconds(8));
        }
        catch { }

        // 与HUD旧值不同的正数视为用户手工覆盖；0只有实时HUD明确识别到时才算已知。
        return state.Gold > 0;
    }

    private static bool IsHpKnown(GameStateSnapshot state)
    {
        if (state.Hp <= 0) return false;
        try
        {
            LiveHudSnapshot hud = LiveHudState.GetSnapshot();
            if (hud.Hp.HasValue && state.Hp == hud.Hp.Value)
                return IsFresh(hud.HpAt, TimeSpan.FromSeconds(10));
        }
        catch { }

        // 与HUD旧值不同的正数视为用户手工覆盖。
        return true;
    }

    private static bool IsFresh(DateTime timestamp, TimeSpan maxAge)
        => timestamp != DateTime.MinValue && DateTime.Now - timestamp <= maxAge;

    private static int ResolveStageMajor(GameStateSnapshot state)
    {
        string stage = state.Stage?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(stage))
            stage = LiveHudState.GetSnapshot().Stage?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(stage)) return 0;
        int dash = stage.IndexOf('-');
        string major = dash > 0 ? stage[..dash] : stage;
        return int.TryParse(major.Trim(), out int value) ? value : 0;
    }

    private static bool LooksLikeUnrivaledComp(string name)
    {
        string lower = name.ToLowerInvariant();
        return ContainsAny(lower, "unrivaled", "宿敌") &&
               (ContainsAny(lower, "雷恩加尔", "rengar") || ContainsAny(lower, "卡兹克", "khazix", "kha'zix"));
    }

    private static string NormalizeEmblemName(string value)
    {
        return value.Replace("纹章", "", StringComparison.OrdinalIgnoreCase)
            .Replace("转职", "", StringComparison.OrdinalIgnoreCase)
            .Replace("之冕", "", StringComparison.OrdinalIgnoreCase)
            .Replace("之冠", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static bool IsApItem(string name)
        => ContainsAny(name.ToLowerInvariant(), "珠光", "大天使", "蓝霸符", "朔极", "帽", "法杖", "离子", "morello", "archangel", "rabadon", "jeweled", "gunblade");

    private static bool IsAdItem(string name)
        => ContainsAny(name.ToLowerInvariant(), "无尽", "轻语", "死亡之刃", "鬼索", "红霸符", "泰坦", "夜之锋刃", "巨人杀手", "infinity", "whisper", "guinsoo", "deathblade", "edge of night");

    private static bool IsTankItem(string name)
        => ContainsAny(name.ToLowerInvariant(), "石像鬼", "狂徒", "振奋", "棘刺", "龙爪", "巨龙之爪", "日炎", "冕卫", "适应性", "gargoyle", "warmog", "bramble", "dragon's claw", "crownguard");

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));
}
