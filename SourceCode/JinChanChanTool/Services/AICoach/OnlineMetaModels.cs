namespace JinChanChanTool.Services.AICoach;

public sealed class OnlineMetaUnit
{
    public string HeroName { get; set; } = "";
    public string[] EquipmentNames { get; set; } = ["", "", ""];
}

public sealed class OnlineMetaComp
{
    public string Name { get; set; } = "";
    public List<OnlineMetaUnit> Units { get; set; } = [];
    public string Tier { get; set; } = "";
    public double WinRate { get; set; }
    public double AverageRank { get; set; }
    public double PickRate { get; set; }
    public double TopFourRate { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Source { get; set; } = "";
}

public sealed class OnlineMetaSnapshot
{
    public string Source { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.MinValue;
    public List<OnlineMetaComp> Comps { get; set; } = [];
    public bool FromCache { get; set; }
    public string Error { get; set; } = "";

    public bool HasData => Comps.Count > 0;
}

public interface IOnlineMetaProvider
{
    string Name { get; }
    Task<List<OnlineMetaComp>> FetchAsync(CancellationToken cancellationToken = default);
}

public static class OnlineMetaState
{
    private static readonly object Sync = new();
    private static OnlineMetaSnapshot _current = new();

    public static event Action<OnlineMetaSnapshot>? Changed;

    public static OnlineMetaSnapshot GetSnapshot()
    {
        lock (Sync)
        {
            return Clone(_current);
        }
    }

    public static void Update(OnlineMetaSnapshot snapshot)
    {
        OnlineMetaSnapshot copy = Clone(snapshot);
        lock (Sync)
        {
            _current = copy;
        }
        Changed?.Invoke(Clone(copy));
    }

    private static OnlineMetaSnapshot Clone(OnlineMetaSnapshot source)
    {
        return new OnlineMetaSnapshot
        {
            Source = source.Source,
            UpdatedAt = source.UpdatedAt,
            FromCache = source.FromCache,
            Error = source.Error,
            Comps = source.Comps.Select(c => new OnlineMetaComp
            {
                Name = c.Name,
                Tier = c.Tier,
                WinRate = c.WinRate,
                AverageRank = c.AverageRank,
                PickRate = c.PickRate,
                TopFourRate = c.TopFourRate,
                Tags = c.Tags.ToList(),
                Source = c.Source,
                Units = c.Units.Select(u => new OnlineMetaUnit
                {
                    HeroName = u.HeroName,
                    EquipmentNames = u.EquipmentNames.ToArray()
                }).ToList()
            }).ToList()
        };
    }
}
