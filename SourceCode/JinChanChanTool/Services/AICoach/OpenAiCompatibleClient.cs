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
        string boardJson = JsonSerializer.Serialize(LiveBoardState.GetSnapshot());
        string metaJson = JsonSerializer.Serialize(OnlineMetaState.GetSnapshot());
        string recJson = JsonSerializer.Serialize(recommendations);
        string prompt = $"""
你是严谨的云顶之弈复盘与训练教练。请严格基于给出的结构化局面、实时上场棋盘信号、当前版本在线Meta统计和候选阵容，不要虚构未提供的棋子、装备、海克斯或纹章。

数据可信度顺序：
1. 当前已上场棋子/羁绊；
2. 当前阶段、等级、金币、血量；
3. 在线Meta的当前版本统计（Tier、平均名次、前四率、登顶率、登场率、运营标签）；
4. 当前商店瞬时结果；
5. 已经确认的装备/纹章。

候选阵容的 Source/MetaTier/MetaWinRate/MetaTopFourRate/MetaPickRate/MetaAverageRank/MetaTags 来自实时在线阵容数据库。不要把高登顶率但与当前棋盘完全不相关的阵容强行列为第一选择；同时不要因为登场率低就否定数据优秀的冷门强阵。

实时上场棋盘信号来自左侧羁绊面板 OCR：Traits 是当前上场棋子产生的羁绊计数；InferredHeroes 仅在低等级羁绊组合唯一时填写，为空时不要自行猜具体英雄。
装备自动识别目前暂停，Equipments 中只使用已确认数据。

输出中文，简洁、可执行，包含：
1. 当前最推荐的阵容和为什么；
2. 第二候选以及什么条件下转过去；
3. 当前棋盘属于哪种过渡路线；
4. 结合等级/金币/血量说明运营节奏；
5. 如果在线Meta数据支持，给出该阵容的Tier、前四率和登顶率作为参考。

局面：{stateJson}
实时上场棋盘：{boardJson}
在线Meta状态：{metaJson}
候选阵容：{recJson}
""";

        var body = new
        {
            model = settings.Model.Trim(),
            messages = new object[]
            {
                new { role = "system", content = "你是严谨的云顶之弈策略分析助手。优先使用当前棋盘和局面，再结合实时版本Meta统计；商店只是瞬时弱信号。只基于提供的数据分析。" },
                new { role = "user", content = prompt }
            },
            temperature = 0.2
        };

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 接口返回 {(int)response.StatusCode}: {Trim(json, 500)}");

        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                return content.GetString() ?? "AI 未返回文本内容。";
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
