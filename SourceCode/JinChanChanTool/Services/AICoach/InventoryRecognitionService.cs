using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace JinChanChanTool.Services.AICoach;

public sealed class InventoryRecognitionResult
{
    public List<InventorySlotDetection> Slots { get; set; } = [];
    public string Error { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.Now;

    public List<string> EquipmentNames => Slots
        .Where(x => !string.IsNullOrWhiteSpace(x.Name))
        .Select(x => x.Name)
        .ToList();
}

public sealed class InventorySlotDetection
{
    public int SlotIndex { get; set; }
    public string Name { get; set; } = "";
    public double Confidence { get; set; }
    public bool IsEmpty { get; set; }
}

/// <summary>
/// 识别云顶左侧装备栏。V2 使用本地装备图片模板，不发送截图到大模型。
/// 默认坐标按用户提供的 2048x1152 实战截图校准，并按当前游戏屏幕分辨率等比缩放。
/// </summary>
public sealed class InventoryRecognitionService : IDisposable
{
    private readonly Control _gameAnchor;
    private readonly object _templateLock = new();
    private readonly List<TemplateFeature> _templates = [];
    private string _loadedTemplateDirectory = "";

    private sealed class TemplateFeature
    {
        public string Name { get; init; } = "";
        public float[] Feature { get; init; } = [];
        public ulong DHash { get; init; }
    }

    public InventoryRecognitionService(Control gameAnchor)
    {
        _gameAnchor = gameAnchor;
    }

    public InventoryRecognitionResult Recognize(AiCoachSettings settings)
    {
        var result = new InventoryRecognitionResult();
        try
        {
            Rectangle screenBounds = ResolveGameScreenBounds();
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            {
                result.Error = "无法确定游戏所在屏幕。";
                return result;
            }

            EnsureTemplatesLoaded();
            if (_templates.Count == 0)
            {
                result.Error = "未找到装备模板 Resources/HeroDatas/*/EquipmentImages。";
                return result;
            }

            using var screen = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(screen))
            {
                g.CopyFromScreen(screenBounds.Left, screenBounds.Top, 0, 0, screenBounds.Size, CopyPixelOperation.SourceCopy);
            }

            float sx = screenBounds.Width / (float)Math.Max(1, settings.InventoryReferenceWidth);
            float sy = screenBounds.Height / (float)Math.Max(1, settings.InventoryReferenceHeight);

            for (int i = 0; i < Math.Clamp(settings.InventorySlotCount, 1, 12); i++)
            {
                int x = (int)Math.Round(settings.InventorySlotX * sx);
                int y = (int)Math.Round((settings.InventorySlotY + i * settings.InventorySlotStepY) * sy);
                int w = Math.Max(16, (int)Math.Round(settings.InventorySlotWidth * sx));
                int h = Math.Max(16, (int)Math.Round(settings.InventorySlotHeight * sy));
                Rectangle rect = Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(Point.Empty, screen.Size));
                if (rect.Width < 12 || rect.Height < 12) continue;

                using Bitmap slot = screen.Clone(rect, PixelFormat.Format24bppRgb);
                var metrics = MeasureInner(slot);
                if (metrics.MeanLuma < settings.InventoryEmptyMeanThreshold && metrics.StdDev < settings.InventoryEmptyStdThreshold)
                {
                    result.Slots.Add(new InventorySlotDetection { SlotIndex = i + 1, IsEmpty = true });
                    continue;
                }

                float[] feature = ExtractFeature(slot);
                ulong hash = ComputeDHash(slot);
                (string name, double confidence) = FindBestMatch(feature, hash);
                bool accepted = confidence >= settings.InventoryMatchThreshold;
                result.Slots.Add(new InventorySlotDetection
                {
                    SlotIndex = i + 1,
                    Name = accepted ? name : "",
                    Confidence = confidence,
                    IsEmpty = false
                });
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    public string SaveDebugCapture(AiCoachSettings settings)
    {
        Rectangle screenBounds = ResolveGameScreenBounds();
        using var screen = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(screen))
        {
            g.CopyFromScreen(screenBounds.Left, screenBounds.Top, 0, 0, screenBounds.Size, CopyPixelOperation.SourceCopy);
        }

        float sx = screenBounds.Width / (float)Math.Max(1, settings.InventoryReferenceWidth);
        float sy = screenBounds.Height / (float)Math.Max(1, settings.InventoryReferenceHeight);
        int x = (int)Math.Round(settings.InventorySlotX * sx);
        int y = (int)Math.Round(settings.InventorySlotY * sy);
        int w = Math.Max(16, (int)Math.Round(settings.InventorySlotWidth * sx));
        int h = Math.Max(16, (int)Math.Round((settings.InventorySlotHeight + (settings.InventorySlotCount - 1) * settings.InventorySlotStepY) * sy));
        Rectangle rect = Rectangle.Intersect(new Rectangle(x, y, w, h), new Rectangle(Point.Empty, screen.Size));
        using Bitmap crop = screen.Clone(rect, PixelFormat.Format24bppRgb);

        string dir = Path.Combine(Application.StartupPath, "Logs", "AICoach");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"Inventory-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        crop.Save(path, ImageFormat.Png);
        return path;
    }

    private Rectangle ResolveGameScreenBounds()
    {
        try
        {
            if (_gameAnchor.IsHandleCreated)
                return Screen.FromHandle(_gameAnchor.Handle).Bounds;
        }
        catch { }
        return Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
    }

    private void EnsureTemplatesLoaded()
    {
        string root = Path.Combine(Application.StartupPath, "Resources", "HeroDatas");
        if (!Directory.Exists(root)) return;

        string? preferred = Directory.GetDirectories(root)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "S18", StringComparison.OrdinalIgnoreCase));
        string? seasonDir = preferred ?? Directory.GetDirectories(root)
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "EquipmentImages")));
        if (seasonDir == null) return;

        string templateDir = Path.Combine(seasonDir, "EquipmentImages");
        if (!Directory.Exists(templateDir)) return;
        if (string.Equals(_loadedTemplateDirectory, templateDir, StringComparison.OrdinalIgnoreCase) && _templates.Count > 0) return;

        lock (_templateLock)
        {
            if (string.Equals(_loadedTemplateDirectory, templateDir, StringComparison.OrdinalIgnoreCase) && _templates.Count > 0) return;
            _templates.Clear();
            foreach (string file in Directory.EnumerateFiles(templateDir, "*.png", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var bmp = new Bitmap(file);
                    _templates.Add(new TemplateFeature
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        Feature = ExtractFeature(bmp),
                        DHash = ComputeDHash(bmp)
                    });
                }
                catch { }
            }
            _loadedTemplateDirectory = templateDir;
        }
    }

    private (string Name, double Confidence) FindBestMatch(float[] candidate, ulong candidateHash)
    {
        string bestName = "";
        double best = 0;
        foreach (TemplateFeature template in _templates)
        {
            double cosine = Cosine(candidate, template.Feature);
            int hamming = HammingDistance(candidateHash, template.DHash);
            double hashScore = 1.0 - hamming / 64.0;
            double score = cosine * 0.78 + hashScore * 0.22;
            if (score > best)
            {
                best = score;
                bestName = template.Name;
            }
        }
        return (bestName, Math.Clamp(best, 0, 1));
    }

    private static float[] ExtractFeature(Bitmap source)
    {
        using Bitmap normalized = Normalize(source, 20, 20);
        var values = new float[20 * 20 * 3];
        int k = 0;
        double sumSq = 0;
        for (int y = 0; y < normalized.Height; y++)
        {
            for (int x = 0; x < normalized.Width; x++)
            {
                Color c = normalized.GetPixel(x, y);
                float r = c.R / 255f;
                float g = c.G / 255f;
                float b = c.B / 255f;
                values[k++] = r; values[k++] = g; values[k++] = b;
                sumSq += r * r + g * g + b * b;
            }
        }
        float norm = (float)Math.Sqrt(Math.Max(1e-8, sumSq));
        for (int i = 0; i < values.Length; i++) values[i] /= norm;
        return values;
    }

    private static Bitmap Normalize(Bitmap source, int width, int height)
    {
        var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(output);
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        int trimX = Math.Max(1, (int)(source.Width * 0.12));
        int trimY = Math.Max(1, (int)(source.Height * 0.12));
        Rectangle src = new(trimX, trimY, Math.Max(1, source.Width - trimX * 2), Math.Max(1, source.Height - trimY * 2));
        g.DrawImage(source, new Rectangle(0, 0, width, height), src, GraphicsUnit.Pixel);
        return output;
    }

    private static ulong ComputeDHash(Bitmap source)
    {
        using Bitmap small = Normalize(source, 9, 8);
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Color a = small.GetPixel(x, y);
                Color b = small.GetPixel(x + 1, y);
                int ga = (a.R * 30 + a.G * 59 + a.B * 11) / 100;
                int gb = (b.R * 30 + b.G * 59 + b.B * 11) / 100;
                if (ga > gb) hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    private static double Cosine(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (int i = 0; i < n; i++) dot += a[i] * b[i];
        return Math.Clamp(dot, 0, 1);
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        ulong v = a ^ b;
        int count = 0;
        while (v != 0)
        {
            v &= v - 1;
            count++;
        }
        return count;
    }

    private static (double MeanLuma, double StdDev) MeasureInner(Bitmap source)
    {
        int left = Math.Max(0, (int)(source.Width * 0.18));
        int top = Math.Max(0, (int)(source.Height * 0.18));
        int right = Math.Min(source.Width, (int)(source.Width * 0.82));
        int bottom = Math.Min(source.Height, (int)(source.Height * 0.82));
        double sum = 0, sumSq = 0;
        int count = 0;
        for (int y = top; y < bottom; y += 2)
        {
            for (int x = left; x < right; x += 2)
            {
                Color c = source.GetPixel(x, y);
                double l = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                sum += l; sumSq += l * l; count++;
            }
        }
        if (count == 0) return (0, 0);
        double mean = sum / count;
        double variance = Math.Max(0, sumSq / count - mean * mean);
        return (mean, Math.Sqrt(variance));
    }

    public void Dispose()
    {
        _templates.Clear();
    }
}
