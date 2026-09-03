using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinChanChanTool.DataClass;
using JinChanChanTool.Services.DataServices.Interface;
using Newtonsoft.Json;

namespace JinChanChanTool.Services.AICoach;

public sealed class LineupGenerationResult
{
    public string Json { get; set; } = "[]";
    public int TotalCount { get; set; }
    public int AiGeneratedCount { get; set; }
    public int FallbackCount { get; set; }
    public string Model { get; set; } = "";
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// 把标准化 MetaTFT 阵容转换成 JinChanChanTool 的 LineUps.json。
/// V4.1 中 AI 主要负责前中期真实过渡；后期最终单位、装备与有效 Meta 站位由确定性代码生成。
/// 任何 AI 批次失败、费用不合理或结构不合格都会自动回退到确定性规则。
/// </summary>
public sealed class LineupGenerationEngine
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private sealed class HeroCatalogRow
    {
        public string HeroName { get; set; } = "";
        public int Cost { get; set; }
        public List<string> Traits { get; set; } = [];
    }

    private sealed class GeneratedLineupDto
    {
        public string LineUpName { get; set; } = "";
        public List<GeneratedSubDto> SubLineUps { get; set; } = [];
    }

    private sealed class GeneratedSubDto
    {
        public List<GeneratedUnitDto> LineUpUnits { get; set; } = [];
    }

    private sealed class GeneratedUnitDto
    {
        public string HeroName { get; set; } = "";
        public string[] EquipmentNames { get; set; } = ["", "", ""];
        public GeneratedPositionDto Position { get; set; } = new();
    }

    private sealed class GeneratedPositionDto
    {
        public int Item1 { get; set; }
        public int Item2 { get; set; }
    }

    public async Task<LineupGenerationResult> GenerateAsync(
        OnlineMetaSnapshot meta,
        IHeroDataService heroDataService,
        AiCoachSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!meta.HasData) throw new InvalidOperationException("没有可用于生成阵容库的 MetaTFT 数据。");

        LineupGenerationAssets.EnsureOnDisk();
        List<HeroCatalogRow> heroes = BuildHeroCatalog(heroDataService);
        Dictionary<string, HeroCatalogRow> heroByName = heroes
            .GroupBy(x => x.HeroName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> validHeroes = heroByName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> equipmentNames = LoadEquipmentCatalog(heroDataService);
        HashSet<string> validEquipment = equipmentNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        WriteInputCatalogFiles(meta, heroes, equipmentNames);

        int maxComps = Math.Clamp(settings.LineupGenerationMaxComps, 10, 120);
        List<OnlineMetaComp> source = meta.Comps
            .Where(c => c.Units.Count >= 3)
            .Take(maxComps)
            .ToList();

        var final = new List<LineUp>();
        var result = new LineupGenerationResult
        {
            Model = settings.Model,
            TotalCount = source.Count
        };

        bool canUseAi = settings.GenerateLineUpsWithAi &&
                        !string.IsNullOrWhiteSpace(settings.ApiKey) &&
                        !string.IsNullOrWhiteSpace(settings.BaseUrl) &&
                        !string.IsNullOrWhiteSpace(settings.Model);

        int batchSize = Math.Clamp(settings.LineupGenerationBatchSize, 1, 15);
        for (int offset = 0; offset < source.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<OnlineMetaComp> batch = source.Skip(offset).Take(batchSize).ToList();
            progress?.Report($"正在生成阵容 {offset + 1}-{offset + batch.Count}/{source.Count}...");

            Dictionary<string, LineUp> aiBatch = new(StringComparer.OrdinalIgnoreCase);
            if (canUseAi)
            {
                try
                {
                    List<LineUp> generated = await GenerateBatchWithAiAsync(
                        batch, heroes, equipmentNames, settings, cancellationToken);
                    foreach (LineUp lineup in generated)
                    {
                        if (!string.IsNullOrWhiteSpace(lineup.LineUpName) &&
                            !aiBatch.ContainsKey(lineup.LineUpName))
                            aiBatch[lineup.LineUpName] = lineup;
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"AI批次 {offset / batchSize + 1} 失败：{ex.Message}");
                }
            }

            foreach (OnlineMetaComp comp in batch)
            {
                LineUp? lineup = null;
                if (aiBatch.TryGetValue(comp.Name, out LineUp? aiLineup))
                {
                    lineup = NormalizeAndValidate(aiLineup, comp, heroByName, validHeroes, validEquipment);
                    if (lineup != null) result.AiGeneratedCount++;
                    else result.Warnings.Add($"{comp.Name}：AI前中期模板未通过费用/结构校验，已使用确定性过渡。");
                }

                if (lineup == null)
                {
                    lineup = BuildFallback(comp, heroes, validEquipment);
                    result.FallbackCount++;
                }

                // 联动依赖名称一一对应，因此强制使用 Meta 标准名称。
                lineup.LineUpName = comp.Name;
                final.Add(lineup);
            }
        }

        final = final
            .GroupBy(x => x.LineUpName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        result.TotalCount = final.Count;
        result.Json = JsonConvert.SerializeObject(final, Formatting.Indented);
        ValidateFinalJson(result.Json, validHeroes, validEquipment);
        return result;
    }

    private async Task<List<LineUp>> GenerateBatchWithAiAsync(
        List<OnlineMetaComp> batch,
        List<HeroCatalogRow> heroes,
        List<string> equipmentNames,
        AiCoachSettings settings,
        CancellationToken cancellationToken)
    {
        string endpoint = BuildEndpoint(settings.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

        string metaJson = System.Text.Json.JsonSerializer.Serialize(batch, new JsonSerializerOptions { WriteIndented = false });
        string heroJson = System.Text.Json.JsonSerializer.Serialize(heroes, new JsonSerializerOptions { WriteIndented = false });
        string equipmentJson = System.Text.Json.JsonSerializer.Serialize(equipmentNames, new JsonSerializerOptions { WriteIndented = false });

        string prompt = $"""
{LineupGenerationAssets.PromptText}

--- 固定结构规则 ---
{LineupGenerationAssets.RulesText}

--- JSON结构模板 ---
{LineupGenerationAssets.TemplateText}

--- 本批Meta阵容 ---
{metaJson}

--- 合法英雄目录 ---
{heroJson}

--- 合法装备目录 ---
{equipmentJson}
""";

        var body = new
        {
            model = settings.Model.Trim(),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是严格的 JSON 阵容库编译器。只输出满足用户给定 schema 的 JSON 数组，不输出 Markdown、解释或注释。前中期必须符合真实费用与运营时点。"
                },
                new { role = "user", content = prompt }
            },
            temperature = 0.1
        };

        request.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI接口返回 {(int)response.StatusCode}: {Trim(responseJson, 400)}");

        using JsonDocument doc = JsonDocument.Parse(responseJson);
        string? content = null;
        if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out JsonElement message) &&
            message.TryGetProperty("content", out JsonElement contentElement))
        {
            content = contentElement.GetString();
        }
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("AI响应中没有 choices[0].message.content。");

        string json = ExtractJsonArray(content);
        List<GeneratedLineupDto>? dto = System.Text.Json.JsonSerializer.Deserialize<List<GeneratedLineupDto>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto == null || dto.Count == 0)
            throw new InvalidOperationException("AI没有生成有效阵容数组。");

        return dto.Select(ConvertDto).ToList();
    }

    private static LineUp ConvertDto(GeneratedLineupDto dto)
    {
        var lineUp = new LineUp(dto.LineUpName)
        {
            SubLineUps = [new SubLineUp(), new SubLineUp(), new SubLineUp()]
        };

        for (int i = 0; i < Math.Min(3, dto.SubLineUps.Count); i++)
        {
            lineUp.SubLineUps[i].LineUpUnits = dto.SubLineUps[i].LineUpUnits.Select(u => new LineUpUnit
            {
                HeroName = u.HeroName?.Trim() ?? "",
                EquipmentNames = NormalizeEquipmentArray(u.EquipmentNames),
                Position = (u.Position?.Item1 ?? 0, u.Position?.Item2 ?? 0)
            }).ToList();
        }
        return lineUp;
    }

    private static LineUp? NormalizeAndValidate(
        LineUp input,
        OnlineMetaComp source,
        Dictionary<string, HeroCatalogRow> heroByName,
        HashSet<string> validHeroes,
        HashSet<string> validEquipment)
    {
        if (!string.Equals(input.LineUpName?.Trim(), source.Name, StringComparison.OrdinalIgnoreCase)) return null;
        if (input.SubLineUps == null || input.SubLineUps.Length != 3) return null;

        var normalized = new LineUp(source.Name)
        {
            SubLineUps = [new SubLineUp(), new SubLineUp(), new SubLineUp()]
        };

        // AI只决定前/中期。后期在下面直接由Meta源数据重建，避免模型改掉核心、装备或站位。
        for (int stage = 0; stage < 2; stage++)
        {
            List<LineUpUnit> units = input.SubLineUps[stage]?.LineUpUnits ?? [];
            var seenHeroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPositions = new HashSet<(int, int)>();
            var clean = new List<LineUpUnit>();

            foreach (LineUpUnit unit in units.Take(10))
            {
                string hero = unit.HeroName?.Trim() ?? "";
                if (!validHeroes.Contains(hero) || !seenHeroes.Add(hero)) continue;
                if (!heroByName.TryGetValue(hero, out HeroCatalogRow? heroData)) continue;

                if (stage == 0 && heroData.Cost >= 4) return null;
                if (stage == 1 && heroData.Cost >= 5) return null;

                string[] equipments = NormalizeEquipmentArray(unit.EquipmentNames)
                    .Select(x => string.IsNullOrWhiteSpace(x) || validEquipment.Contains(x) ? x : "")
                    .ToArray();

                (int row, int col) = unit.Position;
                if (row is < 1 or > 4 || col is < 1 or > 7 || !seenPositions.Add((row, col)))
                {
                    (row, col) = FindFreePosition(clean.Count, seenPositions);
                    seenPositions.Add((row, col));
                }

                clean.Add(new LineUpUnit
                {
                    HeroName = hero,
                    EquipmentNames = equipments,
                    Position = (row, col)
                });
            }

            int minimum = stage == 0 ? 3 : 4;
            if (clean.Count < minimum) return null;

            if (stage == 0)
            {
                int lowCost = clean.Count(u => heroByName.TryGetValue(u.HeroName, out HeroCatalogRow? h) && h.Cost <= 2);
                if (lowCost < Math.Min(3, clean.Count)) return null;
            }

            normalized.SubLineUps[stage].LineUpUnits = clean;
        }

        List<string> finalNames = source.Units
            .Where(x => !string.IsNullOrWhiteSpace(x.HeroName))
            .Select(x => x.HeroName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        normalized.SubLineUps[2].LineUpUnits = BuildUnits(
            finalNames, source, heroByName, validEquipment, preferMetaPositions: true);

        if (normalized.SubLineUps[2].LineUpUnits.Count < Math.Min(3, finalNames.Count)) return null;
        return normalized;
    }

    private static LineUp BuildFallback(
        OnlineMetaComp comp,
        List<HeroCatalogRow> heroes,
        HashSet<string> validEquipment)
    {
        Dictionary<string, HeroCatalogRow> catalog = heroes
            .GroupBy(x => x.HeroName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> finalNames = comp.Units.Select(x => x.HeroName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> keyTraits = comp.Units
            .SelectMany(u => catalog.TryGetValue(u.HeroName, out HeroCatalogRow? h) ? h.Traits : [])
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(4)
            .Select(g => g.Key)
            .ToList();

        int TraitOverlap(HeroCatalogRow h) => h.Traits.Count(t => keyTraits.Contains(t, StringComparer.OrdinalIgnoreCase));

        List<string> extraEarly = heroes
            .Where(h => h.Cost <= 2 && !finalNames.Contains(h.HeroName))
            .Where(h => TraitOverlap(h) > 0)
            .OrderByDescending(TraitOverlap)
            .ThenBy(h => h.Cost)
            .ThenBy(h => h.HeroName, StringComparer.OrdinalIgnoreCase)
            .Select(h => h.HeroName)
            .Take(8)
            .ToList();

        List<string> extraMid = heroes
            .Where(h => h.Cost <= 3 && !finalNames.Contains(h.HeroName))
            .Where(h => TraitOverlap(h) > 0)
            .OrderByDescending(TraitOverlap)
            .ThenBy(h => h.Cost)
            .ThenBy(h => h.HeroName, StringComparer.OrdinalIgnoreCase)
            .Select(h => h.HeroName)
            .Take(10)
            .ToList();

        List<string> finalSourceOrder = comp.Units
            .Where(u => !string.IsNullOrWhiteSpace(u.HeroName))
            .Select(u => u.HeroName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        List<string> affordableFinal = comp.Units
            .Where(u => catalog.TryGetValue(u.HeroName, out HeroCatalogRow? h) && h.Cost <= 4)
            .OrderByDescending(u => u.EquipmentNames.Count(x => !string.IsNullOrWhiteSpace(x)))
            .ThenBy(u => catalog.TryGetValue(u.HeroName, out HeroCatalogRow? h) ? h.Cost : 9)
            .Select(u => u.HeroName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> cheapFinal = affordableFinal
            .OrderBy(n => catalog.TryGetValue(n, out HeroCatalogRow? h) ? h.Cost : 9)
            .ToList();

        List<string> lowCostFinalCores = comp.Units
            .Where(u => catalog.TryGetValue(u.HeroName, out HeroCatalogRow? h) && h.Cost <= 2)
            .OrderByDescending(u => u.EquipmentNames.Count(x => !string.IsNullOrWhiteSpace(x)))
            .ThenBy(u => catalog.TryGetValue(u.HeroName, out HeroCatalogRow? h) ? h.Cost : 9)
            .Select(u => u.HeroName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> early = lowCostFinalCores
            .Concat(extraEarly)
            .Concat(cheapFinal.Where(n => catalog.TryGetValue(n, out HeroCatalogRow? h) && h.Cost == 3))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        if (early.Count < 3)
        {
            early = heroes.Where(h => h.Cost <= 2)
                .OrderByDescending(TraitOverlap)
                .ThenBy(h => h.Cost)
                .Select(h => h.HeroName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
        }

        List<string> mid = cheapFinal
            .Where(n => catalog.TryGetValue(n, out HeroCatalogRow? h) && h.Cost <= 3)
            .Concat(extraMid)
            .Concat(affordableFinal.Where(n => catalog.TryGetValue(n, out HeroCatalogRow? h) && h.Cost == 4))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(7)
            .ToList();
        if (mid.Count < 5)
        {
            mid = heroes.Where(h => h.Cost <= 3)
                .OrderByDescending(TraitOverlap)
                .ThenBy(h => h.Cost)
                .Select(h => h.HeroName)
                .Concat(affordableFinal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
        }

        return new LineUp(comp.Name)
        {
            SubLineUps = [
                new SubLineUp { LineUpUnits = BuildUnits(early, comp, catalog, validEquipment, preferMetaPositions: false) },
                new SubLineUp { LineUpUnits = BuildUnits(mid, comp, catalog, validEquipment, preferMetaPositions: false) },
                new SubLineUp { LineUpUnits = BuildUnits(finalSourceOrder, comp, catalog, validEquipment, preferMetaPositions: true) }
            ]
        };
    }

    private static List<LineUpUnit> BuildUnits(
        List<string> names,
        OnlineMetaComp comp,
        Dictionary<string, HeroCatalogRow> catalog,
        HashSet<string> validEquipment,
        bool preferMetaPositions)
    {
        Dictionary<string, OnlineMetaUnit> metaUnits = comp.Units
            .GroupBy(x => x.HeroName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var positions = new HashSet<(int, int)>();
        var result = new List<LineUpUnit>();
        foreach (string name in names.Take(10))
        {
            string[] equipment = ["", "", ""];
            OnlineMetaUnit? metaUnit = null;
            if (metaUnits.TryGetValue(name, out OnlineMetaUnit? found))
            {
                metaUnit = found;
                equipment = NormalizeEquipmentArray(found.EquipmentNames)
                    .Select(x => string.IsNullOrWhiteSpace(x) || validEquipment.Contains(x) ? x : "")
                    .ToArray();
            }

            (int row, int col) position;
            bool validMetaPosition = preferMetaPositions && metaUnit != null &&
                                     metaUnit.PositionRow is >= 1 and <= 4 &&
                                     metaUnit.PositionColumn is >= 1 and <= 7 &&
                                     !positions.Contains((metaUnit.PositionRow, metaUnit.PositionColumn));
            if (validMetaPosition)
            {
                position = (metaUnit!.PositionRow, metaUnit.PositionColumn);
            }
            else
            {
                bool frontline = IsFrontline(catalog.GetValueOrDefault(name), equipment);
                position = FindRolePosition(frontline, result.Count, positions);
            }

            positions.Add(position);
            result.Add(new LineUpUnit
            {
                HeroName = name,
                EquipmentNames = equipment,
                Position = position
            });
        }
        return result;
    }

    private static bool IsFrontline(HeroCatalogRow? hero, string[] equipment)
    {
        string[] frontlineTraits = ["护卫", "斗士", "重装", "堡垒", "主宰", "战士", "坦克"];
        string[] tankItems = ["石像鬼", "狂徒", "龙爪", "巨龙", "棘刺", "日炎", "冕卫", "振奋", "坚定", "薄暮"];
        if (hero != null && hero.Traits.Any(t => frontlineTraits.Any(f => t.Contains(f, StringComparison.OrdinalIgnoreCase))))
            return true;
        return equipment.Any(e => tankItems.Any(t => e.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    private static (int, int) FindRolePosition(bool frontline, int index, HashSet<(int, int)> used)
    {
        (int, int)[] preferred = frontline
            ? [(1, 2), (1, 4), (1, 6), (2, 3), (2, 5), (2, 1), (2, 7)]
            : [(4, 2), (4, 6), (4, 4), (3, 1), (3, 7), (3, 3), (3, 5)];
        foreach (var p in preferred)
            if (!used.Contains(p)) return p;
        return FindFreePosition(index, used);
    }

    private static (int, int) FindFreePosition(int seed, HashSet<(int, int)> used)
    {
        for (int r = 1; r <= 4; r++)
            for (int c = 1; c <= 7; c++)
                if (!used.Contains((r, c))) return (r, c);
        return (Math.Clamp(seed / 7 + 1, 1, 4), seed % 7 + 1);
    }

    private static List<HeroCatalogRow> BuildHeroCatalog(IHeroDataService heroDataService)
    {
        return heroDataService.GetHeroDatas()
            .Where(h => !string.IsNullOrWhiteSpace(h.HeroName))
            .Select(h => new HeroCatalogRow
            {
                HeroName = h.HeroName,
                Cost = h.Cost,
                Traits = h.Profession.Concat(h.Peculiarity)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderBy(h => h.Cost)
            .ThenBy(h => h.HeroName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> LoadEquipmentCatalog(IHeroDataService heroDataService)
    {
        try
        {
            string[] paths = heroDataService.GetFilePaths();
            int index = heroDataService.GetFilePathsIndex();
            if (paths.Length == 0) return [];
            string dir = paths[Math.Clamp(index, 0, paths.Length - 1)];
            string file = Path.Combine(dir, "Equipment.json");
            if (!File.Exists(file)) return [];
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.EnumerateArray()
                .Select(x => x.TryGetProperty("Name", out JsonElement name) ? name.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    private static void WriteInputCatalogFiles(
        OnlineMetaSnapshot meta,
        List<HeroCatalogRow> heroes,
        List<string> equipmentNames)
    {
        Directory.CreateDirectory(LineupGenerationAssets.RootDirectory);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(
            Path.Combine(LineupGenerationAssets.RootDirectory, "meta-source.json"),
            System.Text.Json.JsonSerializer.Serialize(meta, jsonOptions));
        File.WriteAllText(
            Path.Combine(LineupGenerationAssets.RootDirectory, "hero-catalog.json"),
            System.Text.Json.JsonSerializer.Serialize(heroes, jsonOptions));
        File.WriteAllText(
            Path.Combine(LineupGenerationAssets.RootDirectory, "equipment-catalog.json"),
            System.Text.Json.JsonSerializer.Serialize(equipmentNames, jsonOptions));
    }

    private static string[] NormalizeEquipmentArray(string[]? value)
        => (value ?? []).Take(3).Concat(Enumerable.Repeat("", 3)).Take(3).Select(x => x?.Trim() ?? "").ToArray();

    private static string ExtractJsonArray(string text)
    {
        string value = text.Trim();
        int first = value.IndexOf('[');
        int last = value.LastIndexOf(']');
        if (first < 0 || last <= first) throw new InvalidOperationException("AI返回内容中没有 JSON 数组。");
        return value[first..(last + 1)];
    }

    private static string BuildEndpoint(string baseUrl)
    {
        string value = baseUrl.Trim().TrimEnd('/');
        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return value;
        return value + "/chat/completions";
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private static void ValidateFinalJson(
        string json,
        HashSet<string> validHeroes,
        HashSet<string> validEquipment)
    {
        List<LineUp>? parsed = JsonConvert.DeserializeObject<List<LineUp>>(json);
        if (parsed == null || parsed.Count == 0) throw new InvalidOperationException("最终 LineUps.json 为空或无法反序列化。");
        foreach (LineUp lineup in parsed)
        {
            if (string.IsNullOrWhiteSpace(lineup.LineUpName)) throw new InvalidOperationException("存在空阵容名。");
            if (lineup.SubLineUps == null || lineup.SubLineUps.Length != 3) throw new InvalidOperationException($"{lineup.LineUpName} 的 SubLineUps 不是3个。");
            for (int stage = 0; stage < lineup.SubLineUps.Length; stage++)
            {
                SubLineUp sub = lineup.SubLineUps[stage];
                var heroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var positions = new HashSet<(int, int)>();
                foreach (LineUpUnit unit in sub.LineUpUnits)
                {
                    if (!validHeroes.Contains(unit.HeroName)) throw new InvalidOperationException($"非法英雄：{unit.HeroName}");
                    if (!heroes.Add(unit.HeroName)) throw new InvalidOperationException($"{lineup.LineUpName} 同阶段英雄重复：{unit.HeroName}");
                    if (unit.EquipmentNames == null || unit.EquipmentNames.Length != 3) throw new InvalidOperationException($"{unit.HeroName} 装备槽数量不是3。");
                    foreach (string equipment in unit.EquipmentNames)
                        if (!string.IsNullOrWhiteSpace(equipment) && !validEquipment.Contains(equipment))
                            throw new InvalidOperationException($"非法装备：{equipment}");
                    if (unit.Position.Item1 is < 1 or > 4 || unit.Position.Item2 is < 1 or > 7)
                        throw new InvalidOperationException($"{unit.HeroName} 站位越界。");
                    if (!positions.Add(unit.Position)) throw new InvalidOperationException($"{lineup.LineUpName} 同阶段站位重复。");
                }
            }
        }
    }
}
