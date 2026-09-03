using System.Reflection;
using System.Text.Json;
using JinChanChanTool.DataClass;
using JinChanChanTool.Services.DataServices.Interface;
using Newtonsoft.Json;

namespace JinChanChanTool.Services.AICoach;

public sealed class MetaLineupRefreshResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int MetaCount { get; set; }
    public int LineupCount { get; set; }
    public int AiGeneratedCount { get; set; }
    public int FallbackCount { get; set; }
    public string BackupPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
}

/// <summary>
/// V4 的单一数据链路协调器：
/// MetaTFT -> 本地Meta缓存 -> AI/规则生成 LineUps.json -> 原程序重新加载 -> AI教练与主程序名称联动。
/// </summary>
public sealed class MetaLineupRefreshCoordinator : IDisposable
{
    private readonly MainForm _mainForm;
    private readonly ILineUpService _lineUpService;
    private readonly IHeroDataService _heroDataService;
    private readonly OnlineMetaService _metaService;
    private readonly LineupGenerationEngine _generationEngine = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public MetaLineupRefreshCoordinator(
        MainForm mainForm,
        ILineUpService lineUpService,
        IHeroDataService heroDataService)
    {
        _mainForm = mainForm;
        _lineUpService = lineUpService;
        _heroDataService = heroDataService;
        _metaService = new OnlineMetaService();
        LineupGenerationAssets.EnsureOnDisk();
    }

    public async Task<MetaLineupRefreshResult> RefreshMetaAndRebuildAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetaLineupRefreshCoordinator));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            progress?.Report("正在从 MetaTFT 手动刷新当前版本数据...");
            await _metaService.ForceRefreshAsync();
            cancellationToken.ThrowIfCancellationRequested();

            OnlineMetaSnapshot meta = OnlineMetaState.GetSnapshot();
            if (!meta.HasData)
                throw new InvalidOperationException($"MetaTFT 未返回可用阵容数据。{meta.Error}");

            progress?.Report($"MetaTFT 已读取 {meta.Comps.Count} 套，正在准备生成输入文件...");
            LineupGenerationAssets.EnsureOnDisk();

            AiCoachSettings settings = new AiCoachSettingsStore().Load();
            LineupGenerationResult generated = await _generationEngine.GenerateAsync(
                meta,
                _heroDataService,
                settings,
                progress,
                cancellationToken);

            // MetaTFT 详情接口本身已经计算了推荐站位。无论该套是AI生成还是规则回退，
            // 后期成型阵容都优先应用真实 MetaTFT 站位，避免模型凭经验猜最终棋盘。
            generated.Json = ApplyMetaFinalPositions(generated.Json, meta);

            string lineupPath = GetCurrentLineupsPath();
            string backup = BackupCurrentLineups(lineupPath);
            string generatedCopy = Path.Combine(LineupGenerationAssets.RootDirectory, "LineUps.generated.json");
            File.WriteAllText(generatedCopy, generated.Json);

            progress?.Report($"生成 {generated.TotalCount} 套阵容，正在更新 JinChanChanTool...");
            WriteAtomically(lineupPath, generated.Json);

            _lineUpService.ReLoad(_heroDataService);
            RefreshMainUi();

            var report = new
            {
                generatedAt = DateTime.Now,
                source = meta.Source,
                metaUpdatedAt = meta.UpdatedAt,
                metaCount = meta.Comps.Count,
                outputCount = generated.TotalCount,
                aiGeneratedCount = generated.AiGeneratedCount,
                fallbackCount = generated.FallbackCount,
                model = generated.Model,
                metaPositioningApplied = true,
                outputPath = lineupPath,
                backupPath = backup,
                warnings = generated.Warnings
            };
            File.WriteAllText(
                Path.Combine(LineupGenerationAssets.RootDirectory, "generation-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            WriteLog($"阵容库刷新成功：Meta {meta.Comps.Count}套 -> LineUps {generated.TotalCount}套；AI {generated.AiGeneratedCount}，规则回退 {generated.FallbackCount}；后期站位已应用MetaTFT数据。");
            progress?.Report("阵容库已更新并重新加载。AI教练与主程序现在使用同一套 Meta 阵容名称。 ");

            return new MetaLineupRefreshResult
            {
                Success = true,
                Message = $"已从 MetaTFT 刷新并生成 {generated.TotalCount} 套阵容。AI生成 {generated.AiGeneratedCount} 套，规则补齐 {generated.FallbackCount} 套。",
                MetaCount = meta.Comps.Count,
                LineupCount = generated.TotalCount,
                AiGeneratedCount = generated.AiGeneratedCount,
                FallbackCount = generated.FallbackCount,
                BackupPath = backup,
                OutputPath = lineupPath
            };
        }
        catch (Exception ex)
        {
            WriteLog($"阵容库刷新失败：{ex}");
            return new MetaLineupRefreshResult
            {
                Success = false,
                Message = ex.Message
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TrySelectLineup(string metaName, int stageIndex, out string message)
    {
        if (string.IsNullOrWhiteSpace(metaName))
        {
            message = "阵容名称为空。";
            return false;
        }

        List<LineUp> lineups = _lineUpService.GetLineUps();
        int index = lineups.FindIndex(x => string.Equals(x.LineUpName, metaName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            string target = NormalizeName(metaName);
            index = lineups.FindIndex(x => NormalizeName(x.LineUpName) == target);
        }

        if (index < 0)
        {
            message = $"本地 LineUps.json 中没有“{metaName}”。请先点击主程序的“刷新Meta阵容”。";
            return false;
        }

        if (!_lineUpService.SetLineUpIndex(index))
        {
            message = "切换阵容索引失败。";
            return false;
        }
        _lineUpService.SetSubLineUpIndex(Math.Clamp(stageIndex, 0, 2));
        RefreshMainUi();
        message = $"已同步主程序：{lineups[index].LineUpName}（{StageName(stageIndex)}）。";
        WriteLog(message);
        return true;
    }

    public string GetCurrentLineupsPath()
    {
        string[] paths = _heroDataService.GetFilePaths();
        if (paths.Length == 0) throw new DirectoryNotFoundException("当前赛季 HeroDatas 目录不存在。");
        int index = Math.Clamp(_heroDataService.GetFilePathsIndex(), 0, paths.Length - 1);
        return Path.Combine(paths[index], "LineUps.json");
    }

    private string BackupCurrentLineups(string path)
    {
        string backupDir = Path.Combine(LineupGenerationAssets.RootDirectory, "Backups");
        Directory.CreateDirectory(backupDir);
        if (!File.Exists(path)) return "";
        string backup = Path.Combine(backupDir, $"LineUps-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Copy(path, backup, overwrite: true);
        return backup;
    }

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".v4.tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static string ApplyMetaFinalPositions(string json, OnlineMetaSnapshot meta)
    {
        List<LineUp>? lineups = JsonConvert.DeserializeObject<List<LineUp>>(json);
        if (lineups == null) return json;

        Dictionary<string, OnlineMetaComp> metaByName = meta.Comps
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (LineUp lineup in lineups)
        {
            if (lineup.SubLineUps == null || lineup.SubLineUps.Length < 3) continue;
            if (!metaByName.TryGetValue(lineup.LineUpName, out OnlineMetaComp? comp)) continue;

            List<LineUpUnit> final = lineup.SubLineUps[2].LineUpUnits ?? [];
            Dictionary<string, OnlineMetaUnit> units = comp.Units
                .Where(x => x.PositionRow is >= 1 and <= 4 && x.PositionColumn is >= 1 and <= 7)
                .GroupBy(x => x.HeroName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var used = new HashSet<(int, int)>();
            var positioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 第一遍先锁定真实 Meta 站位。
            foreach (LineUpUnit unit in final)
            {
                if (!units.TryGetValue(unit.HeroName, out OnlineMetaUnit? metaUnit)) continue;
                var position = (metaUnit.PositionRow, metaUnit.PositionColumn);
                if (!used.Add(position)) continue;
                unit.Position = position;
                positioned.Add(unit.HeroName);
            }

            // 第二遍给没有可靠 Meta 坐标的单位安排不冲突位置。
            foreach (LineUpUnit unit in final)
            {
                if (positioned.Contains(unit.HeroName)) continue;
                (int row, int col) = unit.Position;
                if (row is >= 1 and <= 4 && col is >= 1 and <= 7 && used.Add((row, col)))
                    continue;
                unit.Position = FindFreePosition(used);
                used.Add(unit.Position);
            }
        }

        return JsonConvert.SerializeObject(lineups, Formatting.Indented);
    }

    private static (int, int) FindFreePosition(HashSet<(int, int)> used)
    {
        // 优先常用棋盘格，再兜底逐格扫描。
        (int, int)[] preferred =
        [
            (1, 2), (1, 4), (1, 6),
            (2, 1), (2, 3), (2, 5), (2, 7),
            (4, 2), (4, 4), (4, 6),
            (3, 1), (3, 3), (3, 5), (3, 7)
        ];
        foreach (var p in preferred)
            if (!used.Contains(p)) return p;
        for (int r = 1; r <= 4; r++)
            for (int c = 1; c <= 7; c++)
                if (!used.Contains((r, c))) return (r, c);
        return (4, 7);
    }

    private void RefreshMainUi()
    {
        if (_mainForm.IsDisposed) return;
        void Apply()
        {
            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainForm).GetMethod("LoadLineUpsToComboBox", flags)?.Invoke(_mainForm, null);
                typeof(MainForm).GetMethod("LoadLineUpToUI", flags)?.Invoke(_mainForm, null);
            }
            catch (Exception ex)
            {
                WriteLog($"刷新主界面失败：{ex.Message}");
            }
        }

        if (_mainForm.InvokeRequired) _mainForm.BeginInvoke((Action)Apply);
        else Apply();
    }

    private static string NormalizeName(string name)
    {
        return new string(name
            .Where(c => char.IsLetterOrDigit(c) || c >= 0x4e00)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string StageName(int index) => index switch
    {
        0 => "前期",
        1 => "中期",
        _ => "后期"
    };

    private static void WriteLog(string message)
    {
        try
        {
            string dir = Path.Combine(Application.StartupPath, "Logs", "AICoach");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "lineup-refresh.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _metaService.Dispose();
        _gate.Dispose();
    }
}
