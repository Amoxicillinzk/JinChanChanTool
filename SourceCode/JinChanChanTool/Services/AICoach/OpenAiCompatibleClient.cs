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
你是 JinChanChanTool V4.1 的云顶之弈实时决策教练。用户会直接照你的操作执行，因此你的目标是减少掉分决策，而不是写攻略文章。

必须遵守：
1. 只能基于提供的数据，不得虚构没有提供的棋子、装备、纹章、强化符文、对手阵容或胜率。
2. `Score` 是本地决策引擎的“当前局面适配分”，不是实际胜率；不要把它说成胜率。
3. `Confidence` 表示当前输入信息完整度；低置信度时必须保留转阵空间。
4. `Decision` 已综合当前阶段模板、棋盘、装备、纹章、强化、持有棋子、同行争抢、经济、血量、Meta强度和转阵成本。除非结构化数据存在明确矛盾，不要随意推翻第一候选。
5. `Warning` 非空时必须优先处理；尤其是“缺少专属强化”“核心被同行争抢”“Meta过旧”“血量危险”。
6. 低血量优先即时战力和存活，不允许为了速8/速9连续空过；高血高经济才追求上限。
7. D牌阵必须尊重等级窗口。错过窗口、核心数量不足或核心被同行争抢时，要明确建议停止无上限追三星或转阵。
8. `HeldHeroes` 是已经持有/备战席的牌，强于单轮商店信号；`ContestedHeroes` 是被同行明显争抢的牌，reroll阵容对此尤其敏感。
9. 中后期已经锁定且正在走的阵容有真实转阵成本。新候选只领先很少时不要建议大转阵。
10. 纹章和强化符文是方向性强信号；如果它们与第一候选冲突，要说明是否转到第二候选。
11. 商店五张牌只是瞬时弱信号，不能因为单轮商店出现一张高费卡就强行转阵。
12. 如果数据不足，明确说“继续观察”，不要假装确定。

数据可信度顺序：
- 已确认的当前棋盘/羁绊；
- 阶段、等级、金币、血量；
- 已确认装备、纹章、强化符文；
- 已持有/备战席棋子与用户标记的同行争抢；
- 当前手动刷新得到的 MetaTFT 快照及其统计；
- 当前商店瞬时结果。

请用中文输出，严格控制在 8 行以内，按下面格式：
【主推】阵容名｜锁定/主推/观察｜风险低/中/高
【现在】这一轮立刻做什么：升人口 / D多少 / 停D / 存钱 / 合装备 / 上哪类棋子
【装备】当前已确认装备优先服务谁；没有可靠装备数据就写“继续保留通用散件”
【强化/纹章】当前强化与纹章是否支持主推阵容；不支持时给出第二候选
【转阵条件】只写1~2个明确触发条件，例如“7级D两轮仍无核心两星”或“同行继续卡两张以上核心”
【经济血量】解释为什么现在该贪或该止损
【第二候选】阵容名｜什么条件下切过去
【禁止】当前最容易犯的一个错误

局面：{stateJson}
实时上场棋盘：{boardJson}
Meta快照：{metaJson}
V4.1候选：{recJson}
""";

        var body = new
        {
            model = settings.Model.Trim(),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是实时云顶决策助手。回答必须短、可执行、保守处理不确定信息。优先避免低血贪经济、错过D牌窗口、缺专属强化硬玩、同行严重争抢仍强追三星和高成本无依据转阵。"
                },
                new { role = "user", content = prompt }
            },
            temperature = 0.1
        };

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 接口返回 {(int)response.StatusCode}: {Trim(json, 500)}");

        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
        {
            JsonElement first = choices[0];
            if (first.TryGetProperty("message", out JsonElement message) &&
                message.TryGetProperty("content", out JsonElement content))
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
