namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// V4.1 评分校准层。
/// 评分需要保持排序单调，但不能让多个强候选同时被硬截断到100而失去差异。
/// 同时对极低登场率 Meta 样本降低置信度，而不是完全删除冷门强阵。
/// </summary>
public static class V41ScoreCalibration
{
    public static double NormalizeFitScore(double raw)
    {
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0;
        if (raw <= 0) return 0;
        if (raw <= 70) return raw;

        // 70分前保持线性，之后采用单调软上限。
        // raw=100约88分，raw=120约94分；强候选仍然能拉开差距，但不会无限增长。
        double softened = 70 + 30 * (1 - Math.Exp(-(raw - 70) / 32.0));
        return Math.Clamp(softened, 0, 99.5);
    }

    public static double MetaConfidenceAdjustment(OnlineMetaComp comp)
    {
        return comp.PickRate switch
        {
            <= 0 => -10,
            < 0.05 => -10,
            < 0.12 => -6,
            < 0.30 => -2,
            >= 2.0 => 2,
            >= 1.0 => 1,
            _ => 0
        };
    }

    public static int ResolveStageIndex(GameStateSnapshot state)
    {
        string stage = state.Stage?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(stage))
            stage = LiveHudState.GetSnapshot().Stage?.Trim() ?? "";

        int major = ParseStageMajor(stage);
        int level = state.Level;
        if (level <= 0)
        {
            LiveHudSnapshot hud = LiveHudState.GetSnapshot();
            level = hud.Level ?? 0;
        }
        if (level <= 0)
            level = LiveBoardState.GetSnapshot().InferredLevel;

        // 时间轴优先，等级用于处理同阶段的高滚/落后情况。
        if (major is 1 or 2) return 0;
        if (major == 3)
        {
            if (level is > 0 and <= 5) return 0;
            if (level >= 8) return 2;
            return 1;
        }
        if (major == 4)
        {
            // 4阶段仍停留5/6级通常是低费D牌或明显落后，不应直接拿9人口终局模板要求它。
            if (level is > 0 and <= 6) return 1;
            return 2;
        }
        if (major >= 5) return 2;

        // 阶段未知时再退回等级；两者都未知则前期保守。
        if (level > 0)
        {
            if (level <= 5) return 0;
            if (level <= 7) return 1;
            return 2;
        }
        return 0;
    }

    private static int ParseStageMajor(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return 0;
        int dash = stage.IndexOf('-');
        string major = dash > 0 ? stage[..dash] : stage;
        return int.TryParse(major.Trim(), out int value) ? value : 0;
    }
}
