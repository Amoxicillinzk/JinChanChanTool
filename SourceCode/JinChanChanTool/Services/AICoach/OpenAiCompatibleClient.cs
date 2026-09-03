using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JinChanChanTool.Services.AICoach;

public sealed class OpenAiCompatibleClient
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };

    public async Task<string> AnalyzeAsync(
        AiCoachSettings settings,
        GameStateSnapshot state,
        IReadOnlyList<LineupRecommendation> recommendations,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl)) throw new InvalidOperationException("请先填写 API 地址。");
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new InvalidOperationException("请先填写 API Key。");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("请先填写模型名称。");

        string endpoint = BuildEndpoint(settings.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

        string stateJson = JsonSerializer.Serialize(state);
        string recJson = JsonSerializer.Serialize(recommendations);
        string prompt = $"""
你是云顶之弈复盘与训练教练。请严格基于给出的结构化局面和候选阵容，不要虚构未提供的棋子、装备、海克斯或纹章。
输出中文，简洁、可执行，包含：1. 推荐阵容及原因；2. 当前装备方向；3. 当前阶段最重要的两到三件事；4. 哪些条件出现时应该换到第二候选。
局面：{stateJson}
候选阵容：{recJson}
""";

        var body = new
        {
            model = settings.Model.Trim(),
            messages = new object[]
            {
                new { role = "system", content = "你是严谨的云顶之弈策略分析助手，只基于用户提供的结构化数据分析。" },
                new { role = "user", content = prompt }
            },
            temperature = 0.2
        };

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI 接口返回 {(int)response.StatusCode}: {Trim(json, 500)}");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? "AI 未返回文本内容。";
            }
        }
        throw new InvalidOperationException("AI 返回格式不兼容，未找到 choices[0].message.content。");
    }

    private static string BuildEndpoint(string baseUrl)
    {
        string value = baseUrl.Trim().TrimEnd('/');
        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return value;
        return value + "/chat/completions";
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
