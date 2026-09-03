using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.RegularExpressions;
using JinChanChanTool.Services;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// 实时读取游戏 HUD：阶段、等级、金币、血量。
/// V4.1 金币会做多裁剪 OCR + 短暂截断保护，解决 12 被识别成 1 这类多位数丢字问题。
/// </summary>
public sealed class HudStateWatcher : IDisposable
{
    private readonly Rectangle _gameScreenBounds;
    private readonly QueuedOCRService? _ocrService;
    private readonly AiCoachSettings _settings;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private System.Threading.Timer? _timer;
    private string _lastLog = "";
    private int? _lastGold;
    private int _goldTruncationStreak;

    public HudStateWatcher(Control gameAnchor, CardService cardService)
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
    }

    public void Start()
    {
        if (!_settings.AutoDetectHud || _timer != null) return;
        _timer = new System.Threading.Timer(_ => _ = ScanSafeAsync(), null, 450,
            Math.Clamp(_settings.HudRefreshIntervalMs, 700, 3000));
    }

    private async Task ScanSafeAsync()
    {
        if (!_scanGate.Wait(0)) return;
        try
        {
            LiveHudSnapshot snapshot = await ScanAsync();
            LiveHudState.Merge(snapshot);
            WriteDebug(snapshot);
        }
        catch (Exception ex)
        {
            LiveHudState.Merge(new LiveHudSnapshot { Error = ex.Message, CapturedAt = DateTime.Now });
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task<LiveHudSnapshot> ScanAsync()
    {
        var result = new LiveHudSnapshot { CapturedAt = DateTime.Now };
        if (_ocrService == null)
        {
            result.Error = "无法复用原程序 OCR 服务。";
            return result;
        }

        using var screen = new Bitmap(_gameScreenBounds.Width, _gameScreenBounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(screen))
        {
            g.CopyFromScreen(_gameScreenBounds.Left, _gameScreenBounds.Top, 0, 0,
                _gameScreenBounds.Size, CopyPixelOperation.SourceCopy);
        }

        float sx = screen.Width / (float)Math.Max(1, _settings.HudReferenceWidth);
        float sy = screen.Height / (float)Math.Max(1, _settings.HudReferenceHeight);

        result.Stage = await RecognizeStageAsync(screen, sx, sy);
        result.Level = await RecognizeLevelAsync(screen, sx, sy);
        result.Gold = await RecognizeGoldAsync(screen, sx, sy);
        result.Hp = await RecognizeHpAsync(screen, sx, sy);
        return result;
    }

    private async Task<string?> RecognizeStageAsync(Bitmap screen, float sx, float sy)
    {
        Rectangle rect = ScaleRect(_settings.HudStageX, _settings.HudStageY,
            _settings.HudStageWidth, _settings.HudStageHeight, sx, sy, screen.Size);
        string text = Compact(await RecognizeCropAsync(screen, rect, 3));
        Match m = Regex.Match(text, @"(?<major>[2-9])[-—－](?<minor>[1-7])");
        if (!m.Success) return null;
        return $"{m.Groups["major"].Value}-{m.Groups["minor"].Value}";
    }

    private async Task<int?> RecognizeLevelAsync(Bitmap screen, float sx, float sy)
    {
        Rectangle rect = ScaleRect(_settings.HudLevelX, _settings.HudLevelY,
            _settings.HudLevelWidth, _settings.HudLevelHeight, sx, sy, screen.Size);
        string text = Compact(await RecognizeCropAsync(screen, rect, 3));
        Match m = Regex.Match(text, @"(?<n>1[0-5]|[1-9])级");
        if (!m.Success) m = Regex.Match(text, @"(?<n>1[0-5]|[1-9])");
        return m.Success && int.TryParse(m.Groups["n"].Value, out int value) && value is >= 1 and <= 15
            ? value
            : null;
    }

    private async Task<int?> RecognizeGoldAsync(Bitmap screen, float sx, float sy)
    {
        Rectangle baseRect = ScaleRect(_settings.HudGoldX, _settings.HudGoldY,
            _settings.HudGoldWidth, _settings.HudGoldHeight, sx, sy, screen.Size);

        // 云顶金币数字常有描边/动画；同一帧做两个不同边界和缩放的OCR，避免个位被裁掉。
        int padX = Math.Max(6, (int)Math.Round(16 * sx));
        int padY = Math.Max(3, (int)Math.Round(5 * sy));
        Rectangle expanded = Rectangle.Intersect(
            new Rectangle(baseRect.Left - padX, baseRect.Top - padY,
                baseRect.Width + padX * 2, baseRect.Height + padY * 2),
            new Rectangle(Point.Empty, screen.Size));

        string primary = Compact(await RecognizeCropAsync(screen, baseRect, 4));
        string secondary = Compact(await RecognizeCropAsync(screen, expanded, 5));
        var candidates = new List<int>();
        candidates.AddRange(ParseNumericCandidates(primary, 0, 200));
        candidates.AddRange(ParseNumericCandidates(secondary, 0, 200));
        if (candidates.Count == 0) return null;

        // 先按“出现次数”选；票数相同优先多位数。12->1 的场景通常会得到 [1,12]，这里选12。
        int chosen = candidates
            .GroupBy(x => x)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.ToString().Length)
            .ThenBy(g => _lastGold.HasValue ? Math.Abs(g.Key - _lastGold.Value) : 0)
            .Select(g => g.Key)
            .First();

        // 如果上一帧是多位数，而这一帧突然只剩它的首位，先保护1帧。
        // 真正从12花到1时最多延迟约1秒；OCR瞬时截断则不会把经济判断直接带偏。
        if (_lastGold is >= 10 && chosen < 10 &&
            _lastGold.Value.ToString().StartsWith(chosen.ToString(), StringComparison.Ordinal))
        {
            _goldTruncationStreak++;
            if (_goldTruncationStreak <= 1) return _lastGold;
        }
        else
        {
            _goldTruncationStreak = 0;
        }

        _lastGold = chosen;
        return chosen;
    }

    private static IEnumerable<int> ParseNumericCandidates(string text, int min, int max)
    {
        foreach (Match m in Regex.Matches(text ?? "", @"\d{1,3}").Cast<Match>())
        {
            if (int.TryParse(m.Value, out int value) && value >= min && value <= max)
                yield return value;
        }
    }

    private async Task<int?> RecognizeHpAsync(Bitmap screen, float sx, float sy)
    {
        Rectangle sidebar = ScaleRect(_settings.HudSidebarX, _settings.HudSidebarY,
            _settings.HudSidebarWidth, _settings.HudSidebarHeight, sx, sy, screen.Size);
        if (sidebar.Width < 100 || sidebar.Height < 150) return null;

        int selfCenterY = FindSelfPlayerCenterY(screen, sidebar);
        if (selfCenterY <= 0) return null;

        int x = sidebar.Left + (int)Math.Round(_settings.HudSelfHpOffsetX * sx);
        int y = selfCenterY - (int)Math.Round(_settings.HudSelfHpOffsetY * sy);
        int w = Math.Max(40, (int)Math.Round(_settings.HudSelfHpWidth * sx));
        int h = Math.Max(28, (int)Math.Round(_settings.HudSelfHpHeight * sy));
        Rectangle rect = Rectangle.Intersect(new Rectangle(x, y, w, h),
            new Rectangle(Point.Empty, screen.Size));
        if (rect.Width < 30 || rect.Height < 20) return null;

        string text = Compact(await RecognizeCropAsync(screen, rect, 4));
        foreach (int value in ParseNumericCandidates(text, 0, 100)) return value;
        return null;
    }

    private static int FindSelfPlayerCenterY(Bitmap screen, Rectangle sidebar)
    {
        int x0 = Math.Clamp(sidebar.Left + (int)(sidebar.Width * 0.38), 0, screen.Width - 1);
        int x1 = Math.Clamp(sidebar.Right - 2, x0 + 1, screen.Width);
        int y0 = Math.Clamp(sidebar.Top, 0, screen.Height - 1);
        int y1 = Math.Clamp(sidebar.Bottom, y0 + 1, screen.Height);

        int[] rowEnergy = new int[y1 - y0];
        for (int y = y0; y < y1; y += 2)
        {
            int score = 0;
            for (int x = x0; x < x1; x += 2)
            {
                Color c = screen.GetPixel(x, y);
                if (IsSelfGold(c)) score++;
            }
            rowEnergy[y - y0] = score;
        }

        int halfWindow = Math.Max(24, Math.Min(58, sidebar.Height / 14));
        int bestCenter = -1;
        int bestScore = 0;
        for (int cy = y0 + halfWindow; cy < y1 - halfWindow; cy += 3)
        {
            int sum = 0;
            int start = Math.Max(y0, cy - halfWindow);
            int end = Math.Min(y1 - 1, cy + halfWindow);
            for (int y = start; y <= end; y += 2)
                sum += rowEnergy[y - y0];
            if (sum > bestScore)
            {
                bestScore = sum;
                bestCenter = cy;
            }
        }
        return bestScore >= 45 ? bestCenter : -1;
    }

    private static bool IsSelfGold(Color c)
    {
        return c.R >= 145 && c.G >= 100 && c.B <= 105 &&
               c.R - c.B >= 55 && c.G - c.B >= 25;
    }

    private async Task<string> RecognizeCropAsync(Bitmap source, Rectangle rect, int scale)
    {
        if (rect.Width < 8 || rect.Height < 8) return "";
        using Bitmap crop = source.Clone(rect, PixelFormat.Format24bppRgb);
        using Bitmap enlarged = new(Math.Max(1, crop.Width * scale),
            Math.Max(1, crop.Height * scale), PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(enlarged))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(crop, new Rectangle(0, 0, enlarged.Width, enlarged.Height));
        }
        return await _ocrService!.RecognizeTextAsync(enlarged);
    }

    private static string Compact(string value)
        => Regex.Replace(value ?? "", @"\s+", "")
            .Replace("一", "-").Replace("—", "-").Replace("－", "-");

    private static Rectangle ScaleRect(int x, int y, int w, int h, float sx, float sy, Size bounds)
    {
        Rectangle rect = new(
            (int)Math.Round(x * sx),
            (int)Math.Round(y * sy),
            Math.Max(1, (int)Math.Round(w * sx)),
            Math.Max(1, (int)Math.Round(h * sy)));
        return Rectangle.Intersect(rect, new Rectangle(Point.Empty, bounds));
    }

    private void WriteDebug(LiveHudSnapshot snapshot)
    {
        try
        {
            string summary = $"阶段={snapshot.Stage ?? "?"}|等级={snapshot.Level?.ToString() ?? "?"}|金币={snapshot.Gold?.ToString() ?? "?"}|血量={snapshot.Hp?.ToString() ?? "?"}|{snapshot.Error}";
            if (summary == _lastLog) return;
            _lastLog = summary;
            string dir = Path.Combine(Application.StartupPath, "Logs", "AICoach");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "hud-state.log"),
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
