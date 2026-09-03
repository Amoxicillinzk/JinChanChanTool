using JinChanChanTool.Services;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachForm : Form
{
    private readonly CardServiceStateReader _stateReader;
    private readonly LineupRecommendationService _recommendationService;
    private readonly AiCoachSettingsStore _settingsStore = new();
    private readonly OpenAiCompatibleClient _aiClient = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private AiCoachSettings _settings;
    private readonly Label _shopLabel = new();
    private readonly TextBox _stageBox = new();
    private readonly NumericUpDown _levelBox = new();
    private readonly NumericUpDown _goldBox = new();
    private readonly NumericUpDown _hpBox = new();
    private readonly TextBox _equipmentBox = new();
    private readonly TextBox _augmentBox = new();
    private readonly TextBox _emblemBox = new();
    private readonly ListView _recommendationList = new();
    private readonly RichTextBox _aiOutput = new();
    private readonly Label _statusLabel = new();

    private readonly TextBox _baseUrlBox = new();
    private readonly TextBox _apiKeyBox = new();
    private readonly TextBox _modelBox = new();

    public AiCoachForm(CardService cardService, ILineUpService lineUpService)
    {
        _stateReader = new CardServiceStateReader(cardService);
        _recommendationService = new LineupRecommendationService(lineUpService);
        _settings = _settingsStore.Load();

        Text = "AI 云顶教练 V1";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(610, 760);
        MinimumSize = new Size(560, 660);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = true;

        BuildUi();
        LoadSettingsToUi();

        _timer.Interval = Math.Clamp(_settings.RefreshIntervalMs, 500, 5000);
        _timer.Tick += (_, _) => RefreshRecommendations();
        if (_settings.AutoRefresh) _timer.Start();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        Shown += (_, _) => RefreshRecommendations();
    }

    private void BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);

        var coachTab = new TabPage("实时推荐") { Padding = new Padding(10) };
        var settingsTab = new TabPage("AI设置") { Padding = new Padding(12) };
        tabs.TabPages.Add(coachTab);
        tabs.TabPages.Add(settingsTab);

        var coach = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 10,
            AutoScroll = true
        };
        coach.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        coach.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        coachTab.Controls.Add(coach);

        AddRow(coach, 0, "商店OCR", _shopLabel);
        _shopLabel.AutoSize = true;
        _shopLabel.Text = "等待原程序 OCR 结果...";

        var statePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _stageBox.Width = 58;
        _stageBox.PlaceholderText = "3-2";
        ConfigureNumber(_levelBox, 0, 15, 0, 48);
        ConfigureNumber(_goldBox, 0, 200, 0, 58);
        ConfigureNumber(_hpBox, 0, 100, 100, 58);
        statePanel.Controls.AddRange([
            new Label { Text = "阶段", AutoSize = true, Padding = new Padding(0,7,0,0) }, _stageBox,
            new Label { Text = "等级", AutoSize = true, Padding = new Padding(8,7,0,0) }, _levelBox,
            new Label { Text = "金币", AutoSize = true, Padding = new Padding(8,7,0,0) }, _goldBox,
            new Label { Text = "血量", AutoSize = true, Padding = new Padding(8,7,0,0) }, _hpBox
        ]);
        AddRow(coach, 1, "局面", statePanel);

        ConfigureCsvBox(_equipmentBox, "例如：反曲之弓,暴风之剑,鬼索的狂暴之刃");
        ConfigureCsvBox(_augmentBox, "例如：DD街区,珠光莲花");
        ConfigureCsvBox(_emblemBox, "例如：花仙子纹章,神谕者纹章");
        AddRow(coach, 2, "装备", _equipmentBox);
        AddRow(coach, 3, "强化符文", _augmentBox);
        AddRow(coach, 4, "纹章", _emblemBox);

        _recommendationList.Dock = DockStyle.Fill;
        _recommendationList.View = View.Details;
        _recommendationList.FullRowSelect = true;
        _recommendationList.GridLines = true;
        _recommendationList.Height = 170;
        _recommendationList.Columns.Add("阵容", 250);
        _recommendationList.Columns.Add("匹配度", 80);
        _recommendationList.Columns.Add("阶段", 70);
        AddRow(coach, 5, "推荐Top5", _recommendationList, 180);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var refreshButton = new Button { Text = "刷新推荐", AutoSize = true };
        var analyzeButton = new Button { Text = "调用AI分析", AutoSize = true };
        refreshButton.Click += (_, _) => RefreshRecommendations();
        analyzeButton.Click += async (_, _) => await AnalyzeWithAiAsync();
        buttons.Controls.Add(refreshButton);
        buttons.Controls.Add(analyzeButton);
        AddRow(coach, 6, "操作", buttons);

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Text = "V1：商店英雄实时读取；装备/海克斯/纹章先结构化录入，后续接截图识别。";
        AddRow(coach, 7, "状态", _statusLabel);

        _aiOutput.Dock = DockStyle.Fill;
        _aiOutput.ReadOnly = true;
        _aiOutput.BackColor = SystemColors.Window;
        _aiOutput.Height = 230;
        AddRow(coach, 8, "AI建议", _aiOutput, 240);

        var policy = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Text = "提示：请将 AI 建议用于训练/复盘，并遵守游戏及第三方工具的相关规则。",
            ForeColor = Color.DimGray
        };
        AddRow(coach, 9, "说明", policy);

        var settings = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsTab.Controls.Add(settings);
        _baseUrlBox.Dock = DockStyle.Fill;
        _apiKeyBox.Dock = DockStyle.Fill;
        _apiKeyBox.UseSystemPasswordChar = true;
        _modelBox.Dock = DockStyle.Fill;
        AddSettingRow(settings, 0, "API地址", _baseUrlBox);
        AddSettingRow(settings, 1, "API Key", _apiKeyBox);
        AddSettingRow(settings, 2, "模型", _modelBox);

        var saveButton = new Button { Text = "保存AI设置", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        saveButton.Click += (_, _) => SaveSettingsFromUi();
        settings.Controls.Add(saveButton, 1, 3);
        settings.SetColumnSpan(saveButton, 1);

        var help = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Margin = new Padding(0, 18, 0, 0),
            Text = "兼容 OpenAI 风格 /v1/chat/completions 接口。API地址可填 https://api.openai.com/v1 或你的中转地址；如果直接填写到 /chat/completions 也支持。"
        };
        settings.Controls.Add(help, 1, 4);
    }

    private static void ConfigureNumber(NumericUpDown box, decimal min, decimal max, decimal value, int width)
    {
        box.Minimum = min;
        box.Maximum = max;
        box.Value = value;
        box.Width = width;
    }

    private static void ConfigureCsvBox(TextBox box, string placeholder)
    {
        box.Dock = DockStyle.Fill;
        box.PlaceholderText = placeholder;
    }

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control, int height = 38)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var title = new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        control.Margin = new Padding(3, 4, 3, 4);
        panel.Controls.Add(title, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void AddSettingRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 9, 0, 0) }, 0, row);
        panel.Controls.Add(control, 1, row);
        control.Margin = new Padding(3, 5, 3, 5);
    }

    private void LoadSettingsToUi()
    {
        _baseUrlBox.Text = _settings.BaseUrl;
        _apiKeyBox.Text = _settings.ApiKey;
        _modelBox.Text = _settings.Model;
    }

    private void SaveSettingsFromUi()
    {
        _settings.BaseUrl = _baseUrlBox.Text.Trim();
        _settings.ApiKey = _apiKeyBox.Text.Trim();
        _settings.Model = _modelBox.Text.Trim();
        _settingsStore.Save(_settings);
        _statusLabel.Text = "AI 设置已保存。";
    }

    private GameStateSnapshot BuildSnapshot()
    {
        return new GameStateSnapshot
        {
            ShopHeroes = _stateReader.GetShopHeroes(),
            Equipments = ParseCsv(_equipmentBox.Text),
            Augments = ParseCsv(_augmentBox.Text),
            Emblems = ParseCsv(_emblemBox.Text),
            Stage = _stageBox.Text.Trim(),
            Level = (int)_levelBox.Value,
            Gold = (int)_goldBox.Value,
            Hp = (int)_hpBox.Value
        };
    }

    private static List<string> ParseCsv(string value)
    {
        return value.Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RefreshRecommendations()
    {
        if (IsDisposed) return;
        var state = BuildSnapshot();
        _shopLabel.Text = state.ShopHeroes.Length == 0 ? "未识别到商店英雄（请先启用原程序高亮/自动拿牌 OCR）" : string.Join(" ｜ ", state.ShopHeroes);
        var recommendations = _recommendationService.Recommend(state, 5);

        _recommendationList.BeginUpdate();
        _recommendationList.Items.Clear();
        foreach (var rec in recommendations)
        {
            string stage = rec.StageIndex switch { 0 => "前期", 1 => "中期", _ => "后期" };
            var item = new ListViewItem(rec.Name);
            item.SubItems.Add($"{rec.Score:0}%");
            item.SubItems.Add(stage);
            item.ToolTipText = rec.Reason;
            _recommendationList.Items.Add(item);
        }
        _recommendationList.EndUpdate();
    }

    private async Task AnalyzeWithAiAsync()
    {
        SaveSettingsFromUi();
        var state = BuildSnapshot();
        var recommendations = _recommendationService.Recommend(state, 5);
        _statusLabel.Text = "正在调用 AI...";
        _aiOutput.Text = "";
        try
        {
            string result = await _aiClient.AnalyzeAsync(_settings, state, recommendations);
            _aiOutput.Text = result;
            _statusLabel.Text = "AI 分析完成。";
        }
        catch (Exception ex)
        {
            _aiOutput.Text = ex.Message;
            _statusLabel.Text = "AI 调用失败。";
        }
    }
}
