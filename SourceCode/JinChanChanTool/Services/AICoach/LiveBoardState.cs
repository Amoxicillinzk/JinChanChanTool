namespace JinChanChanTool.Services.AICoach;

public sealed class LiveBoardSnapshot
{
    public Dictionary<string, int> Traits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> InferredHeroes { get; set; } = [];
    public int InferredLevel { get; set; }
    public int CandidateCombinationCount { get; set; }
    public string Error { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.MinValue;

    public bool HasBoardSignal => Traits.Count > 0 || InferredHeroes.Count > 0;
}

public static class LiveBoardState
{
    private static readonly object Sync = new();
    private static LiveBoardSnapshot _current = new();

    public static event Action<LiveBoardSnapshot>? Changed;

    public static LiveBoardSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            return Clone(_current);
        }
    }

    public static void Update(LiveBoardSnapshot snapshot)
    {
        LiveBoardSnapshot copy = Clone(snapshot);
        lock (Sync)
        {
            _current = copy;
        }
        Changed?.Invoke(Clone(copy));
    }

    public static void Clear()
    {
        LiveBoardSnapshot copy = new() { CapturedAt = DateTime.Now };
        lock (Sync)
        {
            _current = Clone(copy);
        }
        Changed?.Invoke(Clone(copy));
    }

    private static LiveBoardSnapshot Clone(LiveBoardSnapshot source)
    {
        return new LiveBoardSnapshot
        {
            Traits = new Dictionary<string, int>(source.Traits, StringComparer.OrdinalIgnoreCase),
            InferredHeroes = source.InferredHeroes.ToList(),
            InferredLevel = source.InferredLevel,
            CandidateCombinationCount = source.CandidateCombinationCount,
            Error = source.Error,
            CapturedAt = source.CapturedAt
        };
    }
}
