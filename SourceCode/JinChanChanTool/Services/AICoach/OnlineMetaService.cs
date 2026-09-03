using System.Text.Json;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// V3 在线阵容数据协调器：启动时先读本地缓存，再异步刷新在线 Meta。
/// 游戏过程中只读 OnlineMetaState，不会每秒请求外网。
/// </summary>
public sealed class OnlineMetaService : IDisposable
{
    private readonly AiCoachSettings _settings;
    private readonly List<IOnlineMetaProvider> _providers;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private System.Threading.Timer? _timer;
    private bool _disposed;

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

    public void Start()
    {
        if (!_settings.UseOnlineMeta || _timer != null) return;

        LoadCache();
        _ = RefreshAsync(force: false);

        // 每5分钟检查一次；只有缓存超过配置时长才真正访问网络。
        _timer = new System.Threading.Timer(
            _ => _ = RefreshAsync(force: false),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

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

            // 在线失败时不清空最近一次有效缓存。
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

        // 用户偏好阵容丰富：不只保留S/A，强力冷门也保留；最多120套避免异常接口污染内存。
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
            if (!File.Exists(_cachePath)) return;
            OnlineMetaSnapshot? cached = JsonSerializer.Deserialize<OnlineMetaSnapshot>(File.ReadAllText(_cachePath), JsonOptions);
            if (cached?.Comps is not { Count: > 0 }) return;
            cached.FromCache = true;
            cached.Source = string.IsNullOrWhiteSpace(cached.Source) ? "在线Meta缓存" : cached.Source + "缓存";
            OnlineMetaState.Update(cached);
            WriteLog($"已加载缓存：{cached.Comps.Count}套，更新时间 {cached.UpdatedAt:yyyy-MM-dd HH:mm:ss}。");
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
