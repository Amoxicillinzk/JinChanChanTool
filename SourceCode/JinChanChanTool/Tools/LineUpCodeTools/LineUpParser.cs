using JinChanChanTool.Services.DataServices.Interface;
using System.Diagnostics;

namespace JinChanChanTool.Tools.LineUpCodeTools
{
    /// <summary>
    /// 使用当前主赛季字典解析和生成阵容码。
    /// </summary>
    public sealed class LineUpParser : ILineUpParser
    {
        private readonly ILineUpCodeDictionaryService _dictionaryService;

        public LineUpParser(ILineUpCodeDictionaryService dictionaryService)
        {
            _dictionaryService = dictionaryService;
        }

        public bool IsAvailableForSeason(string season)
        {
            return _dictionaryService.IsReady &&
                   string.Equals(_dictionaryService.LoadedSeason, season, StringComparison.OrdinalIgnoreCase);
        }

        public List<string> ParseCode(string lineupCode, string season)
        {
            var heroes = new List<string>();
            if (!IsAvailableForSeason(season) || string.IsNullOrWhiteSpace(lineupCode))
            {
                return heroes;
            }

            string normalizedCode = lineupCode.Trim();
            string seasonSuffix = BuildSeasonSuffix(season);
            if (!normalizedCode.StartsWith("02", StringComparison.OrdinalIgnoreCase) ||
                !normalizedCode.EndsWith(seasonSuffix, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"阵容码赛季或格式不匹配：{lineupCode}");
                return heroes;
            }

            string hexCore = normalizedCode.Substring(2, normalizedCode.Length - 2 - seasonSuffix.Length);
            if (hexCore.Length == 0 || hexCore.Length % 3 != 0)
            {
                return heroes;
            }

            for (int i = 0; i < hexCore.Length; i += 3)
            {
                string chunk = hexCore.Substring(i, 3).ToLowerInvariant();
                if (chunk == "000") continue;

                if (_dictionaryService.CodeToName.TryGetValue(chunk, out string? heroName))
                {
                    heroes.Add(heroName);
                }
                else
                {
                    Debug.WriteLine($"代码 '{chunk}' 无法在主赛季字典中识别。");
                }
            }

            return heroes;
        }

        public string GenerateCode(List<string> heroNames, string season)
        {
            if (!IsAvailableForSeason(season))
            {
                throw new InvalidOperationException("当前阵容码字典不可用于所选赛季。");
            }

            if (heroNames == null || heroNames.Count == 0)
            {
                throw new ArgumentException("英雄名列表不能为空");
            }

            var hexCodes = new List<string>();
            int effectiveCount = Math.Min(heroNames.Count, 10);
            for (int i = 0; i < effectiveCount; i++)
            {
                if (_dictionaryService.NameToCode.TryGetValue(heroNames[i], out string? code))
                {
                    hexCodes.Add(code);
                }
            }

            while (hexCodes.Count < 10)
            {
                hexCodes.Add("000");
            }

            return "02" + string.Join(string.Empty, hexCodes) + BuildSeasonSuffix(season);
        }

        private static string BuildSeasonSuffix(string season)
        {
            string seasonNumber = new string(season.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(seasonNumber))
            {
                throw new ArgumentException($"赛季标识“{season}”格式无效。", nameof(season));
            }

            return $"TFTSet{seasonNumber}";
        }
    }
}
