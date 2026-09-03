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
        double reliability = Math.Clamp(comp.PickRate * 0.20, 0, 2.0);
        return Math.Clamp(tier + avg + win + top4 + reliability, 0, 22);
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
        bool reroll5 = ContainsAny(tags, "5级d", "5级 d", "level 5", "5 reroll");
        bool reroll6 = ContainsAny(tags, "6级d", "6级 d", "level 6", "6 reroll");
        bool reroll7 = ContainsAny(tags, "7级d", "7级 d", "level 7", "7 reroll", "7/8级d");
        bool fast8 = ContainsAny(tags, "速8", "fast 8", "8级");
        bool fast9 = ContainsAny(tags, "速9", "fast 9", "9级");

        if (reroll5)
        {
            score += state.Level <= 5 ? 10 : state.Level == 6 ? 1 : -8;
            if (state.Level <= 5 && state.Gold >= 35) score += 3;
        }
        if (reroll6)
        {
            score += state.Level == 6 ? 10 : state.Level <= 5 ? 4 : state.Level == 7 ? 1 : -6;
            if (state.Level == 6 && state.Gold >= 30) score += 3;
        }
        if (reroll7)
        {
            score += state.Level == 7 ? 10 : state.Level == 6 ? 5 : state.Level <= 5 ? 1 : state.Level == 8 ? 0 : -5;
            if (state.Level is 7 or 8 && state.Gold >= 30) score += 3;
        }

        if (fast8)
        {
            if (state.Level >= 7 && state.Gold >= 30 && state.Hp >= 55) score += 8;
            else if (state.Level <= 6 && state.Hp >= 70 && state.Gold >= 30) score += 4;
            if (state.Hp is > 0 and < 50 && state.Level < 8) score -= 8;
            if (state.Gold < 15 && state.Level < 8) score -= 4;
        }
        if (fast9)
        {
            if (state.Level >= 8 && state.Gold >= 35 && state.Hp >= 60) score += 10;
            else if (state.Level >= 7 && state.Gold >= 50 && state.Hp >= 75) score += 5;
            if (state.Hp is > 0 and < 60) score -= 11;
            if (state.Gold < 25 && state.Level < 9) score -= 7;
        }

        // 低血量阶段以“先活下来”为第一目标，任何需要长期贪经济的线路都降权。
        if (state.Hp is > 0 and <= 35 && (fast8 || fast9)) score -= 5;
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
            // 专属强化阵容没有录入强化时不能直接判死，但要给风险提示。
            if (LooksLikeUnrivaledComp(comp.Name))
                return new AugmentFit(-18, [], "该阵容疑似依赖“Unrivaled/宿敌”专属强化；未录入强化符文时不要硬锁。" );
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

        double each = state.Hp switch
        {
            <= 0 => 1.5,
            <= 35 => 4.0,
            <= 55 => 3.0,
            _ => 1.8
        };
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
        if (state.Hp > 0) value += 6;
        if (state.Gold > 0) value += 5;
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

        if (state.Hp is > 0 and <= 30)
        {
            if (state.Gold >= 20) return "危险血线：本轮优先花钱提升即时战力，D到能稳住再停，不再贪长期经济。";
            return "危险血线：优先上最强战力和成装，暂停高成本转阵。";
        }
        if (d5)
        {
            if (state.Level < 5) return "不要主动多升人口；攒钱到5级进入D牌窗口。";
            if (state.Level == 5) return state.Gold >= 50 ? "5级卡50利息慢D核心三星；血量低于45时可D到30左右稳血。" : "5级先攒经济，除非掉血严重否则不要把钱D空。";
            return "已错过最佳5级D牌窗口；仅在核心数量明显领先时继续，否则准备转阵。";
        }
        if (d6)
        {
            if (state.Level < 6) return "升到6级后进入主D窗口，途中只做高性价比补强。";
            if (state.Level == 6) return state.Gold >= 50 ? "6级卡利息慢D；血线差时D到30/20把主C主坦提到两星。" : "6级先补经济，下一轮集中D牌。";
            return "已经高于6级窗口；检查核心数量，不够就降低追三星执念并转向提人口。";
        }
        if (d7)
        {
            if (state.Level < 7) return "优先到7级再启动D牌，不要在低等级提前把钱花光。";
            if (state.Level is 7 or 8) return state.Gold >= 40 ? "当前就是D牌窗口：卡30~50利息追核心；低血可一次性D到20稳住。" : "保持20~30金币底线，小幅D牌补两星后继续恢复经济。";
        }
        if (fast9)
        {
            if (state.Level >= 9) return "已到9级：把经济转成高费两星与完整前排，不再存无效金币。";
            if (state.Hp >= 65 && state.Gold >= 45) return "保持连胜/血量，优先攒钱拉9；8级只小D止血。";
            if (state.Hp < 55) return "暂缓速9：先在8级D出稳定两星四费/前排，血线稳定后再考虑9。";
            return "以经济和血量为门槛冲9，不要为了9级牺牲两回合以上战力。";
        }
        if (fast8)
        {
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
        if (!string.IsNullOrWhiteSpace(warning) || score < 38 || strategyScore <= -10) return "高";
        if (score < 58 || confidence < 55 || strategyScore < -3) return "中";
        return "低";
    }

    public static string ClassifyDecision(double score, double confidence, double margin, string risk, int rank)
    {
        if (risk == "高" && score < 50) return "不建议";
        if (rank == 0 && score >= 74 && confidence >= 65 && margin >= 7 && risk != "高") return "锁定";
        if (rank == 0 && score >= 58 && risk != "高") return "主推";
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
