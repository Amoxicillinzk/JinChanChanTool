using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JinChanChanTool.Services;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// OCR 左侧羁绊计数，并结合 HeroData 反推当前棋盘。
/// V4.1：候选组合不唯一时不再把英雄信号全部丢掉，而是返回所有完整候选组合的可靠交集。
/// 若组合搜索达到上限，则不再声称任何英雄“可靠”，避免截断搜索产生假交集。
/// </summary>
public sealed class BoardTraitWatcher : IDisposable
{
    private readonly Rectangle _gameScreenBounds;
    private readonly QueuedOCRService? _ocrService;
    private readonly AiCoachSettings _settings;
    private readonly List<HeroRow> _heroes = [];
    private readonly List<string> _knownTraits = [];
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private System.Threading.Timer? _timer;
    private string _lastLoggedSummary = "";

    private sealed class HeroRow
    {
        public string HeroName { get; set; } = "";
        public int Cost { get; set; }
        public string[] Profession { get; set; } = [];
        public string[] Peculiarity { get; set; } = [];
        public string[] Traits => Profession.Concat(Peculiarity)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public BoardTraitWatcher(Control gameAnchor, CardService cardService)
    {
        _gameScreenBounds = Screen.FromControl(gameAnchor).Bounds;
        _settings = new AiCoachSettingsStore().Load();
        try
        {
            FieldInfo? field = typeof(CardService).GetField("_ocrService", BindingFlags.Instance | BindingFlags.NonPublic);
            _ocrService = field?.GetValue(cardService) as QueuedOCRService;
        }
        catch
        {
            _ocrService = null;
        }
        LoadHeroData();
    }

    public void Start()
    {
        if (!_settings.AutoDetectBoardTraits || _timer != null) return;
        _timer = new System.Threading.Timer(_ => _ = ScanSafeAsync(), null, 700, 2600);
    }

    private async Task ScanSafeAsync()
    {
        if (!_scanGate.Wait(0)) return;
        try
        {
            LiveBoardSnapshot snapshot = await ScanAsync();
            LiveBoardState.Update(snapshot);
            WriteDebugSummary(snapshot);
        }
        catch (Exception ex)
        {
            LiveBoardState.Update(new LiveBoardSnapshot { Error = ex.Message, CapturedAt = DateTime.Now });
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<LiveBoardSnapshot> ScanAsync()
    {
        var result = new LiveBoardSnapshot { CapturedAt = DateTime.Now };
        if (_ocrService == null)
        {
            result.Error = "无法复用原程序 OCR 服务。";
            return result;
        }
        if (_knownTraits.Count == 0)
        {
            result.Error = "未加载到当前赛季 HeroData 羁绊数据。";
            return result;
        }

        using var screen = new Bitmap(_gameScreenBounds.Width, _gameScreenBounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(screen))
        {
            g.CopyFromScreen(_gameScreenBounds.Left, _gameScreenBounds.Top, 0, 0,
                _gameScreenBounds.Size, CopyPixelOperation.SourceCopy);
        }

        float sx = screen.Width / (float)Math.Max(1, _settings.BoardReferenceWidth);
        float sy = screen.Height / (float)Math.Max(1, _settings.BoardReferenceHeight);
        var traits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int blankAfterData = 0;

        for (int row = 0; row < Math.Clamp(_settings.BoardTraitRowCount, 1, 12); row++)
        {
            Rectangle rect = ScaleRect(
                _settings.BoardTraitX,
                _settings.BoardTraitY + row * _settings.BoardTraitStepY,
                _settings.BoardTraitWidth,
                _settings.BoardTraitRowHeight,
                sx, sy, screen.Size);
            if (rect.Width < 20 || rect.Height < 15) continue;

            string text = await RecognizeCropAsync(screen, rect);
            if (TryParseTraitRow(text, out string trait, out int current))
            {
                traits[trait] = Math.Max(traits.GetValueOrDefault(trait), current);
                blankAfterData = 0;
            }
            else if (traits.Count > 0)
            {
                blankAfterData++;
                if (blankAfterData >= 2) break;
            }
        }

        result.Traits = traits;
        result.InferredLevel = await RecognizeLevelAsync(screen, sx, sy);

        // 7级以上组合空间过大；到7级仍可在候选池足够小时做受限精确搜索。
        if (result.InferredLevel is >= 1 and <= 7 && traits.Count >= 2)
        {
            (List<string> heroes, int combinations) = InferHeroes(traits, result.InferredLevel);
            result.InferredHeroes = heroes;
            result.CandidateCombinationCount = combinations;
        }
        return result;
    }

    private async Task<int> RecognizeLevelAsync(Bitmap screen, float sx, float sy)
    {
        Rectangle rect = ScaleRect(
            _settings.BoardLevelX, _settings.BoardLevelY,
            _settings.BoardLevelWidth, _settings.BoardLevelHeight,
            sx, sy, screen.Size);
        if (rect.Width < 20 || rect.Height < 15) return 0;
        string text = Regex.Replace(await RecognizeCropAsync(screen, rect), @"\s+", "");
        Match match = Regex.Match(text, @"(?<level>1[0-5]|[1-9])级");
        if (!match.Success) match = Regex.Match(text, @"(?<level>1[0-5]|[1-9])");
        return match.Success && int.TryParse(match.Groups["level"].Value, out int level) ? level : 0;
    }

    private async Task<string> RecognizeCropAsync(Bitmap source, Rectangle rect)
    {
        using Bitmap crop = source.Clone(rect, PixelFormat.Format24bppRgb);
        using Bitmap enlarged = new(Math.Max(1, crop.Width * 2), Math.Max(1, crop.Height * 2), PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(enlarged))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(crop, new Rectangle(0, 0, enlarged.Width, enlarged.Height));
        }
        return await _ocrService!.RecognizeTextAsync(enlarged);
    }

    private bool TryParseTraitRow(string raw, out string trait, out int current)
    {
        trait = "";
        current = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string compact = Regex.Replace(raw, @"\s+", "");
        trait = _knownTraits.FirstOrDefault(x => compact.Contains(x, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (trait.Length == 0) return false;

        Match fraction = Regex.Match(compact, @"(?<current>\d{1,2})/(?<next>\d{1,2})");
        if (!fraction.Success || !int.TryParse(fraction.Groups["current"].Value, out current))
        {
            trait = "";
            current = 0;
            return false;
        }
        return current > 0;
    }

    private (List<string> Heroes, int CombinationCount) InferHeroes(Dictionary<string, int> observed, int level)
    {
        List<HeroRow> candidates = _heroes
            .Where(h => h.Traits.Length > 0 && h.Traits.All(observed.ContainsKey))
            .OrderBy(h => h.Cost)
            .ThenBy(h => h.HeroName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count < level || candidates.Count > 30) return ([], 0);
        if (level >= 7 && candidates.Count > 20) return ([], 0);

        const int solutionCap = 96;
        var solutions = new List<List<HeroRow>>();
        var chosen = new List<HeroRow>();
        var counts = observed.Keys.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);

        void Search(int start)
        {
            if (solutions.Count >= solutionCap) return;
            if (chosen.Count == level)
            {
                if (observed.All(pair => counts.GetValueOrDefault(pair.Key) == pair.Value))
                    solutions.Add(chosen.ToList());
                return;
            }

            int remaining = level - chosen.Count;
            if (candidates.Count - start < remaining) return;

            for (int i = start; i < candidates.Count; i++)
            {
                HeroRow hero = candidates[i];
                bool exceeds = false;
                foreach (string t in hero.Traits)
                {
                    if (counts.GetValueOrDefault(t) + 1 > observed.GetValueOrDefault(t))
                    {
                        exceeds = true;
                        break;
                    }
                }
                if (exceeds) continue;

                chosen.Add(hero);
                foreach (string t in hero.Traits) counts[t] = counts.GetValueOrDefault(t) + 1;
                Search(i + 1);
                foreach (string t in hero.Traits) counts[t] = Math.Max(0, counts.GetValueOrDefault(t) - 1);
                chosen.RemoveAt(chosen.Count - 1);

                if (solutions.Count >= solutionCap) return;
            }
        }

        Search(0);
        if (solutions.Count == 0) return ([], 0);

        // 搜索触顶意味着结果集并不完整；此时任何“交集英雄”都可能被第97组之后的解推翻。
        // 因此只保留羁绊信号，不输出英雄事实。
        if (solutions.Count >= solutionCap)
            return ([], solutionCap);

        if (solutions.Count == 1)
            return (solutions[0].Select(x => x.HeroName).ToList(), 1);

        // 多组完整解时只返回每组都出现的英雄。宁可少报，不把猜测当成事实。
        var common = solutions[0].Select(x => x.HeroName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (List<HeroRow> solution in solutions.Skip(1))
            common.IntersectWith(solution.Select(x => x.HeroName));

        List<string> reliable = candidates
            .Where(x => common.Contains(x.HeroName))
            .Select(x => x.HeroName)
            .ToList();
        return (reliable, solutions.Count);
    }

    private static Rectangle ScaleRect(int x, int y, int w, int h, float sx, float sy, Size bounds)
    {
        Rectangle rect = new(
            (int)Math.Round(x * sx),
            (int)Math.Round(y * sy),
            Math.Max(1, (int)Math.Round(w * sx)),
            Math.Max(1, (int)Math.Round(h * sy)));
        return Rectangle.Intersect(rect, new Rectangle(Point.Empty, bounds));
    }

    private void LoadHeroData()
    {
        try
        {
            string root = Path.Combine(Application.StartupPath, "Resources", "HeroDatas");
            if (!Directory.Exists(root)) return;
            string? season = Directory.GetDirectories(root)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "S18", StringComparison.OrdinalIgnoreCase))
                ?? Directory.GetDirectories(root).FirstOrDefault(d => File.Exists(Path.Combine(d, "HeroData.json")));
            if (season == null) return;

            string path = Path.Combine(season, "HeroData.json");
            if (!File.Exists(path)) return;
            List<HeroRow> rows = JsonSerializer.Deserialize<List<HeroRow>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            _heroes.AddRange(rows.Where(x => !string.IsNullOrWhiteSpace(x.HeroName)));
            _knownTraits.AddRange(_heroes.SelectMany(x => x.Traits)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Length));
        }
        catch
        {
            _heroes.Clear();
            _knownTraits.Clear();
        }
    }

    private void WriteDebugSummary(LiveBoardSnapshot snapshot)
    {
        try
        {
            string traits = string.Join("、", snapshot.Traits.Select(x => $"{x.Key}{x.Value}"));
            string heroes;
            if (snapshot.CandidateCombinationCount >= 96 && snapshot.InferredHeroes.Count == 0)
                heroes = "候选>=96组，已禁用英雄反推，仅使用羁绊";
            else if (snapshot.InferredHeroes.Count > 0 && snapshot.CandidateCombinationCount > 1)
                heroes = $"可靠交集:{string.Join("、", snapshot.InferredHeroes)}({snapshot.CandidateCombinationCount}组候选)";
            else if (snapshot.InferredHeroes.Count > 0)
                heroes = string.Join("、", snapshot.InferredHeroes);
            else
                heroes = $"未反推({snapshot.CandidateCombinationCount}组候选)";

            string summary = $"等级{snapshot.InferredLevel}|{traits}|{heroes}|{snapshot.Error}";
            if (summary == _lastLoggedSummary) return;
            _lastLoggedSummary = summary;
            string dir = Path.Combine(Application.StartupPath, "Logs", "AICoach");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "board-state.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {summary}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _scanGate.Dispose();
    }
}
