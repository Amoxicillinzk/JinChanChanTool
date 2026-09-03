namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// V4.1 阵容库生成资产。运行时会把提示词、规则、模板写入本地缓存目录，
/// 因此即使换成没有任何会话记忆的大模型，也可以只读取这些文件和 Meta 数据生成 LineUps.json。
/// </summary>
public static class LineupGenerationAssets
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JinChanChanTool",
        "MetaCache",
        "LineupGenerator");

    public static string PromptPath => Path.Combine(RootDirectory, "01-LineUps生成提示词.md");
    public static string RulesPath => Path.Combine(RootDirectory, "02-LineUps生成规则.md");
    public static string TemplatePath => Path.Combine(RootDirectory, "03-LineUps模板.json");
    public static string ReadmePath => Path.Combine(RootDirectory, "README.md");

    public static void EnsureOnDisk()
    {
        Directory.CreateDirectory(RootDirectory);
        File.WriteAllText(PromptPath, PromptText);
        File.WriteAllText(RulesPath, RulesText);
        File.WriteAllText(TemplatePath, TemplateText);
        File.WriteAllText(ReadmePath, ReadmeText);
    }

    public const string ReadmeText = """
# JinChanChanTool V4.1 阵容库生成目录

本目录由程序自动维护，用于把 MetaTFT 在线数据转换成 JinChanChanTool 可直接读取的 `LineUps.json`。

文件用途：
- `01-LineUps生成提示词.md`：可直接发送给任意 OpenAI-compatible 大模型的完整提示词。
- `02-LineUps生成规则.md`：字段、前中后期、英雄/装备/站位的硬约束。
- `03-LineUps模板.json`：最终输出 JSON 的结构模板。
- `meta-source.json`：最近一次 MetaTFT 标准化数据，包含阵容统计、最终棋子、推荐装备、标签以及可用时的推荐站位。
- `hero-catalog.json`：当前赛季合法英雄、费用与羁绊。
- `equipment-catalog.json`：当前赛季合法装备名。
- `LineUps.generated.json`：最近一次通过校验的生成结果。
- `generation-report.json`：生成时间、模型、成功/回退数量等信息。
- `Backups/`：每次覆盖正式 `LineUps.json` 前自动备份。

原则：大模型没有任何历史上下文也没关系。只要把 01/02/03 + meta-source + hero-catalog + equipment-catalog 提供给模型，它就拥有生成所需的全部上下文。
V4.1 中后期最终阵容、装备与有效 Meta 站位由程序进行确定性校验/兜底，模型的主要价值是生成真实可玩的前中期过渡。
""";

    public const string PromptText = """
# 角色
你是 JinChanChanTool 的 S18 阵容库编译器。你的任务不是自由创作攻略，而是把输入的 MetaTFT 当前版本阵容数据，转换成严格可被 JinChanChanTool 反序列化的 `LineUps.json`。

你没有任何历史会话记忆。必须只依据本提示词、生成规则、JSON 模板、`meta-source.json`、`hero-catalog.json` 和 `equipment-catalog.json` 工作。

# 输入
程序会在本提示词末尾附加：
1. `本批Meta阵容`：若干个 MetaTFT 阵容，包含名称、Tier、平均名次、前四率、登顶率、登场率、标签、最终英雄、推荐装备，以及可用时每个最终英雄的 `PositionRow/PositionColumn` 推荐站位。
2. `合法英雄目录`：当前 S18 全部英雄的中文名、费用、职业/特质。
3. `合法装备目录`：当前 S18 全部合法装备中文名。

# 输出目标
对本批每一个 Meta 阵容生成且只生成一个阵容对象。最终输出必须是 **纯 JSON 数组**，禁止 Markdown 代码围栏、解释、注释、前言、后记。

每个对象必须：
- `LineUpName`：与输入 Meta 阵容 `Name` **完全一致**，不得自行加 TOP、S级、胜率、英文前后缀。
- `SubLineUps`：固定长度 3，顺序必须是前期、中期、后期。
- 每个 `SubLineUp` 只有一个 `LineUpUnits` 数组。
- 每个 `LineUpUnit` 必须包含 `HeroName`、长度固定为3的 `EquipmentNames`、`Position:{Item1,Item2}`。

# 前中后期定义
- 前期：通常 4~5 人。目标是自然过渡、保血、低费打工，并优先保留能通向最终阵容的核心低费牌。
- 中期：通常 6~7 人。目标是形成核心羁绊/主要D牌骨架。若 Meta 标签明确写“5级D、6级D、7级D、Reroll”，必须围绕该等级设计。
- 后期：7~10 人。必须忠实于 MetaTFT 的最终 Units。最终阵容不是创作区，不能为了羁绊好看擅自替换 Meta 英雄。

## 费用与可获得性硬规则
前中期必须符合真实商店概率和正常运营，而不是把最终阵容提前复制：
- 前期默认以1~2费为主体，可使用合理的3费打工牌；**禁止5费卡**，通常也不要放4费卡。
- 一个正常4~5人前期阵容至少应有3名1~2费单位；如果做不到，宁可减少人数也不要塞不现实的高费卡。
- 中期以1~3费骨架为主，可以加入少量4费核心；**禁止把5费卡当作标准中期单位**。
- 只有后期模板才可以把5费卡作为正常组成部分。
- 不能因为某张5费卡是最终主C，就在前期/中期模板中提前出现。

允许前期/中期使用最终阵容之外的合法打工英雄，但只能在满足以下条件时使用：
- 与最终核心共享关键羁绊，或
- 可以合理承接最终主C/主坦装备，或
- 是低费、高质量、能明显改善过渡的单位。
禁止为了“凑人数”随机添加不相关英雄。

# D牌阵特殊规则
若 Tags 明确包含 Reroll / 5级D / 6级D / 7级D：
- 对应D牌等级的 SubLineUp 必须保留真正需要追三的低费核心，而不是用高费终局替代。
- 5级D阵容：前期模板就是核心搜牌骨架；中期是在三星或质量提升后逐步补羁绊。
- 6级D阵容：前期保经济与核心牌，中期必须围绕6级主D骨架。
- 7级D阵容：中期应体现7级启动阵容，不要在前期虚构已经拥有完整三/四费核心。
- 不要用“标准Fast8阵容”的过渡方式套用到低费D牌阵。

# Fast8 / Fast9 特殊规则
若 Tags 明确包含 Fast 8 / Fast 9 / 速8 / 速9：
- 前期使用低费强势打工与关键羁绊，不提前塞终局高费牌。
- 中期优先选择能平滑承接终局装备和羁绊的1~4费单位。
- Fast9 的5费核心只放在后期模板；中期必须是到9之前真正能保血的棋盘。

# 装备规则
- 只允许使用 `equipment-catalog.json` 中存在的名称。
- MetaTFT 已明确给某英雄的三件装备时，后期优先原样保留。
- 前中期可以把同类装备放到合理打工人身上，表示装备承载关系。
- 没有可靠依据时填空字符串 `""`，不要编造装备。
- 不要自行创造纹章、神器、光明装备；只有 Meta 输入明确包含时才能使用。

# 站位规则
JinChanChanTool 棋盘坐标固定：
- Item1 = 行，只允许 1~4；1 为前排，4 为最后排。
- Item2 = 列，只允许 1~7。
- MetaTFT 输入中的 `PositionRow` 对应输出 `Position.Item1`，`PositionColumn` 对应输出 `Position.Item2`。
- 当 MetaTFT 给出了 1~4 / 1~7 范围内的有效推荐坐标时，**后期必须原样优先采用该坐标**；不要把真实 MetaTFT 站位替换成自己想象的站位。
- 只有 PositionRow/PositionColumn 为0、越界或缺失时，才根据角色自行补站位。
- 同一个 SubLineUp 内不允许两个英雄占用相同坐标。
- 前期/中期：主坦/近战通常放1~2排；远程主C通常放4排；站位应分散且可实际摆放。

# 强制校验规则
- 英雄名必须逐字存在于合法英雄目录。
- 装备名必须逐字存在于合法装备目录，空字符串除外。
- 每套阵容三个阶段均不得出现同名英雄两次。
- `EquipmentNames` 必须恰好3项。
- 前/中/后期人数不得超过10。
- 前期禁止5费；中期禁止把5费作为常规组成。
- 后期必须覆盖输入 Meta 的全部可容纳最终 Units；不要用过渡牌替换最终单位。
- 禁止输出 JSON 之外的任何文本。

# 质量目标
优先级从高到低：
1. JSON 可被软件读取；
2. 后期忠实于 MetaTFT 的最终英雄、装备和站位；
3. 前中期在费用、人口和时间轴上真实可获得；
4. D牌/Fast8/Fast9节奏与标签一致；
5. 核心装备承接合理；
6. 前中期站位合法；
7. 不臆造数据。
""";

    public const string RulesText = """
# LineUps.json 固定结构与生成规则

## 1. 顶层
顶层必须是 JSON 数组，每项是一套阵容：
- LineUpName: string
- SubLineUps: array，长度必须为3

## 2. 三个 SubLineUps 的固定含义
- SubLineUps[0] = 前期
- SubLineUps[1] = 中期
- SubLineUps[2] = 后期

JinChanChanTool 的“前期 / 中期 / 后期”三个按钮直接切换这三个索引，自动拿牌也读取当前索引内的英雄。

## 3. LineUpUnit
每个英雄必须是：
- HeroName: 当前赛季合法中文英雄名
- EquipmentNames: 恰好3个字符串，可以为空字符串
- Position:
  - Item1: 1~4
  - Item2: 1~7

## 4. 阶段人数与费用建议
- 前期：4~5，极特殊低费D牌阵可3~6；以1~2费为主体，可少量3费；禁止5费。
- 中期：6~7，特殊节奏可5~8；以1~3费为骨架，可少量4费；禁止把5费作为标准中期单位。
- 后期：7~10；按Meta最终阵容，可正常包含4/5费。

## 5. MetaTFT 数据解释
- 有装备的英雄通常视为更高核心权重，但不要认为无装备英雄一定是无关挂件。
- Tags 中的 Reroll / Level 5 / Level 6 / Level 7 / Fast 8 / Fast 9 等信息决定中期运营节点。
- PositionRow/PositionColumn 是 MetaTFT 最终阵容推荐站位，后期应优先转成 Position.Item1/Item2。
- Tier、AverageRank、TopFourRate、WinRate、PickRate用于选择需要生成的阵容，不写入 LineUps.json。

## 6. 名称联动规则
LineUpName 必须与 MetaTFT 标准化 Name 完全一致。AI 教练使用该名称与 JinChanChanTool 本地阵容库做一一对应，所以禁止重命名。

## 7. 决策引擎依赖
V4.1 会直接拿当前阶段的 SubLineUp 做实时匹配，因此阶段模板质量会改变推荐结果：
- 不能把后期终局复制到前中期。
- 不能把正常情况下买不到的5费牌放进前中期。
- 不确定过渡时，宁可使用共享羁绊的低费单位，也不要虚构高费高滚棋盘。
- 后期数据必须稳定、确定、忠实Meta；模型主要负责前中期过渡。

## 8. 禁止事项
- 禁止杜撰英雄。
- 禁止杜撰装备。
- 禁止把胜率、TOP序号、Tier拼进阵容名。
- 禁止把三个阶段做成“标准/镜像/缩角站位”；三个阶段就是运营时间轴。
- 禁止在 MetaTFT 已提供有效最终站位时自行覆盖成另一个后期站位。
- 禁止输出注释或 Markdown。
""";

    public const string TemplateText = """
[
  {
    "LineUpName": "必须与MetaTFT的Name完全一致",
    "SubLineUps": [
      {
        "LineUpUnits": [
          {
            "HeroName": "前期合法低费英雄名",
            "EquipmentNames": ["", "", ""],
            "Position": {"Item1": 1, "Item2": 2}
          }
        ]
      },
      {
        "LineUpUnits": [
          {
            "HeroName": "中期合法英雄名",
            "EquipmentNames": ["", "", ""],
            "Position": {"Item1": 1, "Item2": 2}
          }
        ]
      },
      {
        "LineUpUnits": [
          {
            "HeroName": "后期Meta最终英雄名",
            "EquipmentNames": ["装备1", "装备2", "装备3"],
            "Position": {"Item1": 4, "Item2": 6}
          }
        ]
      }
    ]
  }
]
""";
}
