using JinChanChanTool.Services;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public sealed class AiCoachForm : Form
{
    private readonly CardServiceStateReader _stateReader;
    private readonly LineupRecommendationService _recommendationService;
    private readonly AiCoachSettingsStore _settingsStore = new();
    private readonly OpenAiCompatibleClient _aiClient = new();
    private readonly InventoryRecognitionService _inventoryRecognizer;
    private readonly System.Windows.Forms.Timer _timer = new();

    private AiCoachSettings _settings;
    private InventoryRecognitionResult _inventoryResult = new();
    private readonly Label _shopLabel = new();
    private readonly TextBox _stageBox = new();
    private readonly NumericUpDown _levelBox = new();
    private readonly NumericUpDown _goldBox = new();
    private readonly NumericUpDown _hpBox = new();
    private readonly CheckBox _autoEquipmentCheck = new();
    private readonly Label _autoEquipmentLabel = new();
    private readonly TextBox _equipmentBox = new();
    private readonly TextBox _augmentBox = new();
    private readonly TextBox _emblemBox = new();
    private readonly ListView _recommendationList = new();
    private readonly RichTextBox _aiOutput = new();
    private readonly Label _statusLabel = new();

    private readonly TextBox _baseUrlBox = new();
    private readonly TextBox _apiKeyBox = new();
    private readonly TextBox _modelBox = new();
    private readonly TextBox _inventoryRegionBox = new();
    private readonly NumericUpDown _inventoryThresholdBox = new();

    public AiCoachForm(CardService cardService, ILineUpService lineUpService, Control gameAnchor)
    {
        _stateReader = new CardServiceStateReader(cardService);
        _recommendationService = new LineupRecommendationService(lineUpService);
        _inventoryRecognizer = new InventoryRecognitionService(gameAnchor);
        _settings = _settingsStore.Load();

        Text = "AI 云顶教练 V2";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(660, 820);
        MinimumSize = new Size(600, 720);
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
        Disposed += (_, _) => _inventoryRecognizer.Dispose();
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
            RowCount = 11,
            AutoScroll = true
        };
        coach.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
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

        var autoEquipmentPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _autoEquipmentCheck.AutoSize = true;
        _autoEquipmentCheck.Text = "自动识别";
        _autoEquipmentLabel.AutoSize = true;
        _autoEquipmentLabel.MaximumSize = new Size(390, 0);
        _autoEquipmentLabel.Text = "等待装备栏截图...";
        autoEquipmentPanel.Controls.Add(_autoEquipmentCheck);
        autoEquipmentPanel.Controls.Add(_autoEquipmentLabel);
        AddRow(coach, 2, "装备栏", autoEquipmentPanel, 62);

        ConfigureCsvBox(_equipmentBox, "可手动补充/纠错，例如：反曲之弓,暴风之剑");
        ConfigureCsvBox(_augmentBox, "例如：DD街区,珠光莲花");
        ConfigureCsvBox(_emblemBox, "可手动补充，例如：花仙子纹章,神谕者纹章");
        AddRow(coach, 3, "手动补充装备", _equipmentBox);
        AddRow(coach, 4, "强化符文", _augmentBox);
        AddRow(coach, 5, "手动补充纹章", _emblemBox);

        _recommendationList.Dock = DockStyle.Fill;
        _recommendationList.View = View.Details;
        _recommendationList.FullRowSelect = true;
        _recommendationList.GridLines = true;
        _recommendationList.ShowItemToolTips = true;
        _recommendationList.Height = 170;
        _recommendationList.Columns.Add("阵容", 280);
        _recommendationList.Columns.Add("匹配度", 80);
        _recommendationList.Columns.Add("阶段", 70);
        AddRow(coach, 6, "推荐Top5", _recommendationList, 180);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var refreshButton = new Button { Text = "刷新推荐", AutoSize = true };
        var analyzeButton = new Button { Text = "调用AI分析", AutoSize = true };
        var captureButton = new Button { Text = "保存装备区截图", AutoSize = true };
        refreshButton.Click += (_, _) => RefreshRecommendations();
        analyzeButton.Click += async (_, _) => await AnalyzeWithAiAsync();
        captureButton.Click += (_, _) => SaveInventoryDebugCapture();
        buttons.Controls.Add(refreshButton);
        buttons.Controls.Add(analyzeButton);
        buttons.Controls.Add(captureButton);
        AddRow(coach, 7, "操作", buttons);

        _statusLabel.AutoSize = true;
        _statusLabel.MaximumSize = new Size(430, 0);
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Text = "V2：商店英雄 + 左侧装备/纹章实时识别；强化符文仍可手动录入。";
        AddRow(coach, 8, "状态", _statusLabel, 54);

        _aiOutput.Dock = DockStyle.Fill;
        _aiOutput.ReadOnly = true;
        _aiOutput.BackColor = SystemColors.Window;
        _aiOutput.Height = 210;
        AddRow(coach, 9, "AI建议", _aiOutput, 220);

        var policy = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            Text = "提示：请将 AI 建议用于训练/复盘，并遵守游戏及第三方工具的相关规则。",
            ForeColor = Color.DimGray
        };
        AddRow(coach, 10, "说明", policy);

        var settings = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsTab.Controls.Add(settings);
        _baseUrlBox.Dock = DockStyle.Fill;
        _apiKeyBox.Dock = DockStyle.Fill;
        _apiKeyBox.UseSystemPasswordChar = true;
        _modelBox.Dock = DockStyle.Fill;
        _inventoryRegionBox.Dock = DockStyle.Fill;
        _inventoryRegionBox.PlaceholderText = "x,y,w,h,纵向步长,槽位数";
        ConfigureNumber(_inventoryThresholdBox, 50, 95, 70, 70);
        _inventoryThresholdBox.DecimalPlaces = 0;
        _inventoryThresholdBox.Increment = 1;

        AddSettingRow(settings, 0, "API地址", _baseUrlBox);
        AddSettingRow(settings, 1, "API Key", _apiKeyBox);
        AddSettingRow(settings, 2, "模型", _modelBox);
        AddSettingRow(settings, 3, "装备区域", _inventoryRegionBox);
        AddSettingRow(settings, 4, "匹配阈值%", _inventoryThresholdBox);

        var saveButton = new Button { Text = "保存设置", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        saveButton.Click += (_, _) => SaveSettingsFromUi();
        settings.Controls.Add(saveButton, 1, 5);

        var help = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(450, 0),
            Margin = new Padding(0, 18, 0, 0),
            Text = "装备区域默认按你提供的 2048×1152 主屏截图校准：8,231,50,50,58,9。程序会按实际游戏屏幕分辨率缩放。AI接口兼容 OpenAI 风格 /v1/chat/completions。"
        };
        settings.Controls.Add(help, 1, 6);
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
        _autoEquipmentCheck.Checked = _settings.AutoDetectEquipments;
        _inventoryRegionBox.Text = $"{_settings.InventorySlotX},{_settings.InventorySlotY},{_settings.InventorySlotWidth},{_settings.InventorySlotHeight},{_settings.InventorySlotStepY},{_settings.InventorySlotCount}";
        decimal threshold = (decimal)(_settings.InventoryMatchThreshold * 100.0);
        _inventoryThresholdBox.Value = Math.Clamp(threshold, _inventoryThresholdBox.Minimum, _inventoryThresholdBox.Maximum);
    }

    private void SaveSettingsFromUi()
    {
        _settings.BaseUrl = _baseUrlBox.Text.Trim();
        _settings.ApiKey = _apiKeyBox.Text.Trim();
        _settings.Model = _modelBox.Text.Trim();
        _settings.AutoDetectEquipments = _autoEquipmentCheck.Checked;
        _settings.InventoryMatchThreshold = (double)_inventoryThresholdBox.Value / 100.0;
        ParseInventoryRegion(_inventoryRegionBox.Text);
        _settingsStore.Save(_settings);
        _statusLabel.Text = "设置已保存。";
    }

    private void ParseInventoryRegion(string text)
    {
        int[] values = text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x.Trim(), out int value) ? value : -1)
            .ToArray();
        if (values.Length != 6 || values.Any(x => x < 0)) return;
        _settings.InventorySlotX = values[0];
        _settings.InventorySlotY = values[1];
        _settings.InventorySlotWidth = Math.Max(16, values[2]);
        _settings.InventorySlotHeight = Math.Max(16, values[3]);
        _settings.InventorySlotStepY = Math.Max(16, values[4]);
        _settings.InventorySlotCount = Math.Clamp(values[5], 1, 12);
    }

    private GameStateSnapshot BuildSnapshot()
    {
        List<string> autoDetected = _autoEquipmentCheck.Checked ? _inventoryResult.EquipmentNames : [];
        List<string> autoEmblems = autoDetected.Where(IsEmblem).ToList();
        List<string> autoEquipments = autoDetected.Where(x => !IsEmblem(x)).ToList();

        return new GameStateSnapshot
        {
            ShopHeroes = _stateReader.GetShopHeroes(),
            Equipments = autoEquipments.Concat(ParseCsv(_equipmentBox.Text)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Augments = ParseCsv(_augmentBox.Text),
            Emblems = autoEmblems.Concat(ParseCsv(_emblemBox.Text)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Stage = _stageBox.Text.Trim(),
            Level = (int)_levelBox.Value,
            Gold = (int)_goldBox.Value,
            Hp = (int)_hpBox.Value
        };
    }

    private static bool IsEmblem(string name)
    {
        return name.EndsWith("纹章", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("转职", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseCsv(string value)
    {
        return value.Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RefreshInventory()
    {
        if (!_autoEquipmentCheck.Checked)
        {
            _autoEquipmentLabel.Text = "自动识别已关闭";
            return;
        }

        _inventoryResult = _inventoryRecognizer.Recognize(_settings);
        if (!string.IsNullOrWhiteSpace(_inventoryResult.Error))
        {
            _autoEquipmentLabel.Text = $"识别失败：{_inventoryResult.Error}";
            return;
        }

        var detected = _inventoryResult.Slots.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        if (detected.Count == 0)
        {
            int nonEmpty = _inventoryResult.Slots.Count(x => !x.IsEmpty);
            _autoEquipmentLabel.Text = nonEmpty == 0
                ? "装备栏为空"
                : $"检测到 {nonEmpty} 个非空槽，但置信度不足；可在设置中降低阈值或保存调试截图。";
            return;
        }

        _autoEquipmentLabel.Text = string.Join(" ｜ ", detected.Select(x => $"{x.Name} {x.Confidence:P0}"));
    }

    private void RefreshRecommendations()
    {
        if (IsDisposed) return;
        RefreshInventory();
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

    private void SaveInventoryDebugCapture()
    {
        try
        {
            ParseInventoryRegion(_inventoryRegionBox.Text);
            string path = _inventoryRecognizer.SaveDebugCapture(_settings);
            _statusLabel.Text = $"装备区截图已保存：{path}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"保存装备区截图失败：{ex.Message}";
        }
    }

    private async Task AnalyzeWithAiAsync()
    {
        SaveSettingsFromUi();
        RefreshInventory();
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
