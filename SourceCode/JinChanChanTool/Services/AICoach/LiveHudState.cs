namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// 游戏 HUD 的实时识别结果。字段使用 nullable，避免一次 OCR 失败把上一次有效值覆盖成 0。
/// V4.1 对“中后期 -> 2阶段”的回跳做双帧确认，确认新对局后再清除上一局 HUD 残留。
/// </summary>
public sealed class LiveHudSnapshot
{
    public string? Stage { get; set; }
    public int? Level { get; set; }
    public int? Gold { get; set; }
    public int? Hp { get; set; }
    public string Error { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.MinValue;
    public DateTime StageAt { get; set; } = DateTime.MinValue;
    public DateTime LevelAt { get; set; } = DateTime.MinValue;
    public DateTime GoldAt { get; set; } = DateTime.MinValue;
    public DateTime HpAt { get; set; } = DateTime.MinValue;

    public bool HasAnyValue => !string.IsNullOrWhiteSpace(Stage) || Level.HasValue || Gold.HasValue || Hp.HasValue;
}

public static class LiveHudState
{
    private static readonly object Sync = new();
    private static LiveHudSnapshot _current = new();
    private static int _newGameStage2Streak;

    public static event Action<LiveHudSnapshot>? Changed;

    public static LiveHudSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            return Clone(_current);
        }
    }

    /// <summary>
    /// 合并一次扫描。只有本轮实际识别成功的字段才覆盖旧值。
    /// 若连续两次检测到上一局中后期 -> 新一局2阶段，则先清空旧局 HUD。
    /// 第一帧可疑回跳不会写入任何数值，避免一次 OCR 毛刺污染当前决策。
    /// </summary>
    public static void Merge(LiveHudSnapshot partial)
    {
        LiveHudSnapshot copy;
        lock (Sync)
        {
            DateTime now = partial.CapturedAt == DateTime.MinValue ? DateTime.Now : partial.CapturedAt;
            int previousMajor = ParseStageMajor(_current.Stage);
            int incomingMajor = ParseStageMajor(partial.Stage);
            bool suspiciousRollback = previousMajor >= 3 && incomingMajor == 2;

            if (suspiciousRollback)
            {
                _newGameStage2Streak++;
                if (_newGameStage2Streak < 2)
                {
                    _current.Error = "检测到疑似新对局阶段回跳，等待下一帧确认。";
                    _current.CapturedAt = now;
                    copy = Clone(_current);
                    Monitor.Exit(Sync);
                    try { Changed?.Invoke(copy); }
                    finally { Monitor.Enter(Sync); }
                    return;
                }

                _current = new LiveHudSnapshot();
                _newGameStage2Streak = 0;
            }
            else if (incomingMajor > 0)
            {
                _newGameStage2Streak = 0;
            }

            if (!string.IsNullOrWhiteSpace(partial.Stage))
            {
                _current.Stage = partial.Stage;
                _current.StageAt = now;
            }
            if (partial.Level.HasValue)
            {
                _current.Level = partial.Level;
                _current.LevelAt = now;
            }
            if (partial.Gold.HasValue)
            {
                _current.Gold = partial.Gold;
                _current.GoldAt = now;
            }
            if (partial.Hp.HasValue)
            {
                _current.Hp = partial.Hp;
                _current.HpAt = now;
            }

            _current.Error = partial.Error;
            _current.CapturedAt = now;
            copy = Clone(_current);
        }
        Changed?.Invoke(copy);
    }

    public static void Clear()
    {
        LiveHudSnapshot copy;
        lock (Sync)
        {
            _current = new LiveHudSnapshot { CapturedAt = DateTime.Now };
            _newGameStage2Streak = 0;
            copy = Clone(_current);
        }
        Changed?.Invoke(copy);
    }

    private static int ParseStageMajor(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return 0;
        int dash = stage.IndexOf('-');
        string major = dash > 0 ? stage[..dash] : stage;
        return int.TryParse(major.Trim(), out int value) ? value : 0;
    }

    private static LiveHudSnapshot Clone(LiveHudSnapshot source)
    {
        return new LiveHudSnapshot
        {
            Stage = source.Stage,
            Level = source.Level,
            Gold = source.Gold,
            Hp = source.Hp,
            Error = source.Error,
            CapturedAt = source.CapturedAt,
            StageAt = source.StageAt,
            LevelAt = source.LevelAt,
            GoldAt = source.GoldAt,
            HpAt = source.HpAt
        };
    }
}
