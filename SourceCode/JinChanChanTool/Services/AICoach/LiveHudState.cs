namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// 游戏 HUD 的实时识别结果。字段使用 nullable，避免一次 OCR 失败把上一次有效值覆盖成 0。
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
    /// </summary>
    public static void Merge(LiveHudSnapshot partial)
    {
        LiveHudSnapshot copy;
        lock (Sync)
        {
            DateTime now = partial.CapturedAt == DateTime.MinValue ? DateTime.Now : partial.CapturedAt;

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
