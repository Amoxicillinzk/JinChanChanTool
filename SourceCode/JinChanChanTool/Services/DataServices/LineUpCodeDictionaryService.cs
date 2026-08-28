using JinChanChanTool.DataClass;
using JinChanChanTool.Forms;
using JinChanChanTool.Services.DataServices.Interface;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JinChanChanTool.Services.DataServices
{
    internal sealed class LineUpCodeDictionaryDataFile
    {
        public string Season { get; set; } = string.Empty;
        public DateTime UpdateTime { get; set; } = DateTime.MinValue;
        public Dictionary<string, string> CodeToName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 管理各赛季阵容码字典的本地数据。
    /// 网络更新由 AutoUpdateService 统一负责。
    /// </summary>
    public sealed class LineUpCodeDictionaryService : ILineUpCodeDictionaryService
    {
        private const string CacheFileName = "LineUpCodeDictionary.json";

        private readonly Dictionary<string, string> _codeToName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _nameToCode = new(StringComparer.OrdinalIgnoreCase);

        public string LoadedSeason { get; private set; } = string.Empty;
        public bool IsReady => _codeToName.Count > 0 && _nameToCode.Count > 0;
        public IReadOnlyDictionary<string, string> CodeToName => _codeToName;
        public IReadOnlyDictionary<string, string> NameToCode => _nameToCode;

        public bool LoadSeasonDictionary(string season)
        {
            if (string.IsNullOrWhiteSpace(season)) return false;

            string filePath = GetCacheFilePath(season);
            if (!File.Exists(filePath)) return false;

            try
            {
                string json = File.ReadAllText(filePath);
                var dataFile = JsonSerializer.Deserialize<LineUpCodeDictionaryDataFile>(json, CreateJsonOptions());
                if (dataFile == null || !string.Equals(dataFile.Season, season, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Dictionary<string, string> validMap = FilterAndValidateForSeason(season, dataFile.CodeToName);
                if (validMap.Count == 0) return false;

                ApplyDictionary(season, validMap);
                OutputForm.Instance.WriteLineOutputMessage(
                    $"阵容码字典已从本地加载：{season}，共 {_codeToName.Count} 个英雄。");
                return true;
            }
            catch (Exception ex)
            {
                OutputForm.Instance.WriteLineOutputMessage($"本地阵容码字典读取失败：{ex.Message}");
                return false;
            }
        }

        public bool NeedsUpdate(string season)
        {
            return !IsReady || !string.Equals(LoadedSeason, season, StringComparison.OrdinalIgnoreCase);
        }

        public bool UpdateDataFromCrawling(string season, IReadOnlyDictionary<string, string> codeToName)
        {
            if (string.IsNullOrWhiteSpace(season) || codeToName == null) return false;

            Dictionary<string, string> validMap = FilterAndValidateForSeason(season, codeToName);
            if (validMap.Count == 0) return false;

            SaveCache(season, validMap);
            ApplyDictionary(season, validMap);
            OutputForm.Instance.WriteLineOutputMessage(
                $"阵容码字典已更新并缓存：{season}，共 {_codeToName.Count} 个英雄。");
            return true;
        }

        private static Dictionary<string, string> FilterAndValidateForSeason(
            string season,
            IEnumerable<KeyValuePair<string, string>> entries)
        {
            Dictionary<string, string> localNames = LoadLocalHeroNames(season);
            if (localNames.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var filteredEntries = entries
                .Where(entry => localNames.TryGetValue(NormalizeHeroName(entry.Value), out _))
                .Select(entry => new KeyValuePair<string, string>(
                    entry.Key,
                    localNames[NormalizeHeroName(entry.Value)]));

            return ValidateAndNormalize(filteredEntries);
        }

        private static Dictionary<string, string> ValidateAndNormalize(IEnumerable<KeyValuePair<string, string>> entries)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                string code = entry.Key?.Trim().ToLowerInvariant() ?? string.Empty;
                string name = entry.Value?.Trim() ?? string.Empty;
                if (!Regex.IsMatch(code, "^[0-9a-f]{3}$") || code == "000" || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (result.TryGetValue(code, out string? existingName) &&
                    !string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"阵容码“{code}”对应多个英雄：{existingName}、{name}。");
                }

                result[code] = name;
            }

            return result;
        }

        private static Dictionary<string, string> ValidateAndNormalize(IReadOnlyDictionary<string, string> entries)
        {
            return ValidateAndNormalize(entries.AsEnumerable());
        }

        private static Dictionary<string, string> LoadLocalHeroNames(string season)
        {
            string heroDataPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Resources",
                "HeroDatas",
                season,
                "HeroData.json");
            if (!File.Exists(heroDataPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                string json = File.ReadAllText(heroDataPath);
                List<Hero>? heroes = JsonSerializer.Deserialize<List<Hero>>(json, CreateJsonOptions());
                return heroes?
                    .Where(hero => !string.IsNullOrWhiteSpace(hero.HeroName))
                    .GroupBy(hero => NormalizeHeroName(hero.HeroName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().HeroName, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                OutputForm.Instance.WriteLineOutputMessage($"本地英雄数据读取失败：{season}，{ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeHeroName(string? name)
        {
            return new string((name ?? string.Empty)
                .Where(character => char.IsLetterOrDigit(character))
                .ToArray());
        }

        private void ApplyDictionary(string season, IReadOnlyDictionary<string, string> source)
        {
            _codeToName.Clear();
            _nameToCode.Clear();

            foreach (var pair in source)
            {
                _codeToName[pair.Key] = pair.Value;
                if (!_nameToCode.ContainsKey(pair.Value))
                {
                    _nameToCode[pair.Value] = pair.Key;
                }
            }

            LoadedSeason = season;
        }

        private static void SaveCache(string season, IReadOnlyDictionary<string, string> source)
        {
            string filePath = GetCacheFilePath(season);
            string directory = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(directory);

            var dataFile = new LineUpCodeDictionaryDataFile
            {
                Season = season,
                UpdateTime = DateTime.Now,
                CodeToName = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
            };

            string json = JsonSerializer.Serialize(dataFile, CreateJsonOptions());
            string tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, true);
        }

        private static string GetCacheFilePath(string season)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "HeroDatas", season, CacheFileName);
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }
    }
}
