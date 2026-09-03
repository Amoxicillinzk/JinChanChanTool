namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// 追三星数量竞争必须逐英雄判断。不同英雄的张数不能合并成“接近三星”。
/// </summary>
public static class HeroCopyRaceEvaluator
{
    public sealed record HeroRace(string Hero, int Held, int Contested)
    {
        public int Delta => Held - Contested;
    }

    public sealed class Result
    {
        public List<HeroRace> Races { get; init; } = [];
        public int TotalHeld { get; init; }
        public int TotalContested { get; init; }
        public HeroRace? BestProgress { get; init; }
        public HeroRace? WorstPressure { get; init; }
        public double ScoreAdjustment { get; init; }
        public double ContestPenalty { get; init; }

        public string ProgressText => BestProgress == null
            ? ""
            : $"{BestProgress.Hero}×{BestProgress.Held}";
    }

    public static Result Evaluate(
        IEnumerable<string> coreHeroes,
        GameStateSnapshot state,
        bool reroll)
    {
        var heroes = coreHeroes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var races = new List<HeroRace>();
        foreach (string hero in heroes)
        {
            int held = GetCount(state.HeldHeroCounts, state.HeldHeroes, hero);
            int contested = GetCount(state.ContestedHeroCounts, state.ContestedHeroes, hero);
            if (held > 0 || contested > 0)
                races.Add(new HeroRace(hero, held, contested));
        }

        HeroRace? bestProgress = races
            .Where(x => x.Held > 0)
            .OrderByDescending(x => x.Held)
            .ThenByDescending(x => x.Delta)
            .FirstOrDefault();
        HeroRace? worstPressure = races
            .Where(x => x.Contested > 0)
            .OrderByDescending(x => x.Contested - x.Held)
            .ThenByDescending(x => x.Contested)
            .FirstOrDefault();

        int totalHeld = races.Sum(x => x.Held);
        int totalContested = races.Sum(x => x.Contested);

        double positive = 0;
        double negative = 0;
        if (reroll)
        {
            foreach (HeroRace race in races)
            {
                if (race.Held >= 8 && race.Contested <= 2) positive = Math.Max(positive, 9);
                else if (race.Held >= 7 && race.Contested <= 3) positive = Math.Max(positive, 7);
                else if (race.Held >= 5 && race.Delta >= 1) positive = Math.Max(positive, 4);

                if (race.Contested >= race.Held + 5 && race.Held <= 4) negative = Math.Min(negative, -9);
                else if (race.Contested >= race.Held + 3 && race.Held <= 3) negative = Math.Min(negative, -5);
            }
        }

        // 同行惩罚看“相对竞争态势”，不是只看对方绝对张数。
        // 自己已经7~8张且明显领先时，对方少量库存不应把接近完成的三星路线重罚。
        double contestPenalty = reroll
            ? Math.Min(32, races.Sum(race =>
            {
                if (race.Contested <= 0) return 0.0;
                if (race.Held >= 8 && race.Delta >= 5)
                    return 0.5 + race.Contested * 0.35;
                if (race.Held >= 7 && race.Delta >= 3)
                    return 1.0 + race.Contested * 0.55;
                if (race.Held >= 5 && race.Delta >= 2)
                    return 1.5 + race.Contested * 0.90;
                return 3.0 + race.Contested * 2.20;
            }))
            : Math.Min(14, races.Sum(x => x.Contested > 0 ? 2.5 + x.Contested * 0.9 : 0));

        return new Result
        {
            Races = races,
            TotalHeld = totalHeld,
            TotalContested = totalContested,
            BestProgress = bestProgress,
            WorstPressure = worstPressure,
            ScoreAdjustment = Math.Clamp(positive + negative, -12, 9),
            ContestPenalty = contestPenalty
        };
    }

    public static string BuildWarning(Result race, bool reroll)
    {
        if (race.WorstPressure == null) return "";
        HeroRace p = race.WorstPressure;

        if (!reroll)
            return $"核心牌被同行争抢：{p.Hero}你约{p.Held}张/同行约{p.Contested}张；预计搜牌成本上升。";

        if (p.Contested >= p.Held + 5 && p.Held <= 4)
            return $"追三竞争明显不利：{p.Hero}你约{p.Held}张，同行约{p.Contested}张；当前数量条件下不应锁定，除非装备/强化强绑定。";
        if (p.Contested >= p.Held + 3 && p.Held <= 3)
            return $"追三竞争偏劣：{p.Hero}你约{p.Held}张，同行约{p.Contested}张；下一轮仍无数量优势就降低追三优先级。";

        // 自己数量领先或差距很小时，只把它作为解释数据，不把整套阵容强制打成“中风险”。
        return "";
    }

    public static string ApplyAction(string action, Result race, bool reroll)
    {
        if (!reroll) return action;

        HeroRace? best = race.BestProgress;
        if (best is { Held: >= 8 } && best.Contested <= 3)
            return action + $" {best.Hero}已有{best.Held}张，优先完成三星后立即停D。";

        HeroRace? worst = race.WorstPressure;
        if (worst != null && worst.Contested >= worst.Held + 5 && worst.Held <= 4)
            return action + $" {worst.Hero}同行库存明显领先，本轮不要继续无上限追三，优先评估转阵。";

        if (best is { Held: >= 7 } && best.Contested <= 3)
            return action + $" {best.Hero}已有{best.Held}张，已接近三星，转阵前必须计入这部分沉没成本。";

        if (best is { Held: >= 5 })
            return action + $" {best.Hero}已有{best.Held}张，继续观察单卡进度，不要把不同核心张数合并判断。";

        return action;
    }

    private static int GetCount(
        Dictionary<string, int> counts,
        IReadOnlyCollection<string> names,
        string hero)
    {
        if (counts.TryGetValue(hero, out int count)) return Math.Clamp(count, 0, 18);
        return names.Contains(hero, StringComparer.OrdinalIgnoreCase) ? 1 : 0;
    }
}
