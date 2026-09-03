using System.Text.Json;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// 在线阵容数据协调器。
/// V4 默认由 AI 教练启动时只读取本地缓存；只有用户点击“刷新Meta阵容”才强制访问 MetaTFT，
/// 保证 AI 教练与已生成的 LineUps.json 锁定在同一次 Meta 快照。
/// </summary>
public sealed class OnlineMetaService : IDisposable
{
    private readonly AiCoachSettings _settings;
    private readonly List<IOnlineMetaProvider> _providers;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private System.Threading.Timer? _timer;
    private bool _disposed;
    private bool _cacheLoaded;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public OnlineMetaService(IEnumerable<IOnlineMetaProvider>? providers = null)
    {
        _settings = new AiCoachSettingsStore().Load();
        _providers = providers?.ToList() ?? [new MetaTftOnlineMetaProvider()];

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JinChanChanTool",
            "MetaCache");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "s18-online-meta.json");
    }

    /// <summary>
    /// 兼容旧 V3：加载缓存并允许后台按缓存时效刷新。
    /// V4 AI教练不再调用此方法，而改用 LoadCacheOnly()。
    /// </summary>
    public void Start()
    {
        if (!_settings.UseOnlineMeta || _timer != null) return;

        LoadCacheOnly();
        _ = RefreshAsync(force: false);

        _timer = new System.Threading.Timer(
            _ => _ = RefreshAsync(force: false),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// V4：只载入本地最近一次 Meta 快照，不发起任何网络请求。
    /// </summary>
    public void LoadCacheOnly()
    {
        if (_disposed || !_settings.UseOnlineMeta || _cacheLoaded) return;
        _cacheLoaded = true;
        LoadCache();
    }

    /// <summary>
    /// 用户手动刷新入口。无论缓存是否过期，都立即访问 MetaTFT。
    /// </summary>
    public Task ForceRefreshAsync() => RefreshAsync(force: true);

    private async Task RefreshAsync(bool force)
    {
        if (_disposed || !_settings.UseOnlineMeta || !_refreshGate.Wait(0)) return;
        try
        {
            OnlineMetaSnapshot current = OnlineMetaState.GetSnapshot();
            TimeSpan maxAge = TimeSpan.FromMinutes(Math.Clamp(_settings.OnlineMetaCacheMinutes, 5, 240));
            if (!force && current.HasData && DateTime.Now - current.UpdatedAt < maxAge)
                return;

            Exception? lastError = null;
            foreach (IOnlineMetaProvider provider in _providers)
            {
                try
                {
                    List<OnlineMetaComp> fetched = await provider.FetchAsync();
                    List<OnlineMetaComp> filtered = FilterComps(fetched);
                    if (filtered.Count == 0) continue;

                    var snapshot = new OnlineMetaSnapshot
                    {
                        Source = provider.Name,
                        UpdatedAt = DateTime.Now,
                        Comps = filtered,
                        FromCache = false
                    };
                    OnlineMetaState.Update(snapshot);
                    SaveCache(snapshot);
                    WriteLog($"在线更新成功：{provider.Name}，{filtered.Count}套阵容。");
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    WriteLog($"{provider.Name} 更新失败：{ex.Message}");
                }
            }

            current = OnlineMetaState.GetSnapshot();
            if (current.HasData)
            {
                current.Error = lastError?.Message ?? "在线 Meta 暂时不可用";
                OnlineMetaState.Update(current);
            }
            else
            {
                OnlineMetaState.Update(new OnlineMetaSnapshot
                {
                    Source = "本地阵容兜底",
                    UpdatedAt = DateTime.Now,
                    Error = lastError?.Message ?? "未获取到在线 Meta"
                });
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private List<OnlineMetaComp> FilterComps(IEnumerable<OnlineMetaComp> source)
    {
        IEnumerable<OnlineMetaComp> query = source
            .Where(x => x.Units.Count >= 3)
            .Where(x => x.AverageRank <= 0 || x.AverageRank < 5.4)
            .Where(x => x.Tier != "D" || x.TopFourRate >= 50 || x.WinRate >= 10);

        if (!_settings.IncludeLowPickStrongComps)
        {
            query = query.Where(x => x.PickRate >= 0.12 || x.Tier is "S" or "A");
        }

        return query
            .OrderBy(x => TierOrder(x.Tier))
            .ThenBy(x => x.AverageRank <= 0 ? 99 : x.AverageRank)
            .ThenByDescending(x => x.TopFourRate)
            .ThenByDescending(x => x.WinRate)
            .Take(120)
            .ToList();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                WriteLog("本地尚无 Meta 缓存；等待用户手动点击“刷新Meta阵容”。");
                return;
            }
            OnlineMetaSnapshot? cached = JsonSerializer.Deserialize<OnlineMetaSnapshot>(File.ReadAllText(_cachePath), JsonOptions);
            if (cached?.Comps is not { Count: > 0 }) return;
            cached.FromCache = true;
            cached.Source = string.IsNullOrWhiteSpace(cached.Source) ? "MetaTFT缓存" : cached.Source.Replace("缓存", "") + "缓存";
            OnlineMetaState.Update(cached);
            WriteLog($"已加载固定Meta缓存：{cached.Comps.Count}套，更新时间 {cached.UpdatedAt:yyyy-MM-dd HH:mm:ss}；不会后台自动换版本。");
        }
        catch (Exception ex)
        {
            WriteLog($"读取缓存失败：{ex.Message}");
        }
    }

    private void SaveCache(OnlineMetaSnapshot snapshot)
    {
        try
        {
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception ex)
        {
            WriteLog($"写入缓存失败：{ex.Message}");
        }
    }

    private static int TierOrder(string tier) => tier switch
    {
        "S" => 0,
        "A" => 1,
        "B" => 2,
        "C" => 3,
        "D" => 4,
        _ => 5
    };

    private static void WriteLog(string message)
    {
        try
        {
            string dir = Path.Combine(Application.StartupPath, "Logs", "AICoach");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "online-meta.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        _refreshGate.Dispose();
    }
}
