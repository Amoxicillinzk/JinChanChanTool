using System.Text.RegularExpressions;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// 解析手动棋子数量输入。
/// 支持：易、易*7、易x7、易×7、易:7、易7张。
/// 未写数量时按1张处理；同名多次输入会累加，最终限制在合理范围。
/// </summary>
public static class HeroCountParser
{
    private static readonly Regex CountPattern = new(
        @"^(?<name>.+?)(?:\s*(?:\*|x|X|×|:|：)\s*(?<count>\d{1,2})|\s*(?<count2>\d{1,2})\s*张)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Dictionary<string, int> Parse(string value)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in Split(value))
        {
            Match match = CountPattern.Match(token);
            if (!match.Success) continue;

            string name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            int count = 1;
            string rawCount = match.Groups["count"].Success
                ? match.Groups["count"].Value
                : match.Groups["count2"].Value;
            if (!string.IsNullOrWhiteSpace(rawCount) && int.TryParse(rawCount, out int parsed))
                count = Math.Clamp(parsed, 1, 18);

            result[name] = Math.Clamp(result.GetValueOrDefault(name) + count, 1, 18);
        }
        return result;
    }

    public static List<string> Names(Dictionary<string, int> counts)
        => counts.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public static string Format(Dictionary<string, int> counts, IEnumerable<string>? filter = null)
    {
        HashSet<string>? allowed = filter?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join("、", counts
            .Where(x => allowed == null || allowed.Contains(x.Key))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Value > 1 ? $"{x.Key}×{x.Value}" : x.Key));
    }

    private static IEnumerable<string> Split(string value)
        => (value ?? "")
            .Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0);
}
