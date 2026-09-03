using System.Reflection;
using System.Runtime.CompilerServices;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// V4 UI联动层：不侵入原 MainForm Designer。
/// - 在原程序菜单栏增加“刷新Meta阵容”按钮；
/// - AI教练单击推荐阵容时同步切换原程序阵容和前/中/后期阶段。
/// </summary>
public static class MetaLineupV4Integration
{
    private static MetaLineupRefreshCoordinator? _coordinator;
    private static MainForm? _mainForm;
    private static AiCoachForm? _coachForm;
    private static ToolStripButton? _refreshButton;
    private static ListView? _recommendationList;
    private static bool _exitHooked;
    private static bool _attaching;

    [ModuleInitializer]
    public static void Initialize()
    {
        Application.Idle += OnApplicationIdle;
    }

    private static void OnApplicationIdle(object? sender, EventArgs e)
    {
        if (_attaching) return;
        _attaching = true;
        try
        {
            MainForm? main = Application.OpenForms.OfType<MainForm>().FirstOrDefault(x => !x.IsDisposed);
            if (main == null || !main.Visible) return;

            if (_coordinator == null)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (typeof(MainForm).GetField("_iLineUpService", flags)?.GetValue(main) is not ILineUpService lineUpService)
                    return;
                if (typeof(MainForm).GetField("_iheroDataService", flags)?.GetValue(main) is not IHeroDataService heroDataService)
                    return;
                if (typeof(MainForm).GetField("_cardService", flags)?.GetValue(main) == null)
                    return; // 等 Form_Load 完成。

                _mainForm = main;
                _coordinator = new MetaLineupRefreshCoordinator(main, lineUpService, heroDataService);
                AttachMainRefreshButton(main);
            }

            AiCoachForm? coach = Application.OpenForms.OfType<AiCoachForm>().FirstOrDefault(x => !x.IsDisposed);
            if (coach != null && !ReferenceEquals(coach, _coachForm))
            {
                _coachForm = coach;
                AttachCoachLinkage(coach);
            }

            if (!_exitHooked)
            {
                _exitHooked = true;
                Application.ApplicationExit += (_, _) =>
                {
                    _coordinator?.Dispose();
                    _coordinator = null;
                };
            }
        }
        finally
        {
            _attaching = false;
        }
    }

    private static void AttachMainRefreshButton(MainForm main)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(MainForm).GetField("menuStrip_主窗口菜单", flags)?.GetValue(main) is not MenuStrip menu)
            return;
        if (menu.Items.Find("toolStripButton_V4MetaRefresh", true).Length > 0) return;

        _refreshButton = new ToolStripButton
        {
            Name = "toolStripButton_V4MetaRefresh",
            Text = "刷新Meta阵容",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "手动读取MetaTFT当前版本数据，调用AI生成LineUps.json并立即更新阵容库"
        };
        _refreshButton.Click += async (_, _) => await RefreshMetaFromMainAsync();
        menu.Items.Add(_refreshButton);
    }

    private static async Task RefreshMetaFromMainAsync()
    {
        if (_coordinator == null || _refreshButton == null) return;

        AiCoachSettings settings = new AiCoachSettingsStore().Load();
        if (settings.GenerateLineUpsWithAi && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            DialogResult choice = MessageBox.Show(
                "当前没有配置 AI API Key。\n\n继续刷新时，程序仍会保存 MetaTFT 数据并使用内置规则生成 LineUps.json，但不会经过大模型优化前中期过渡。\n\n建议先在 AI 云顶教练 -> AI设置 中配置接口。是否仍然继续？",
                "未配置AI接口",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes) return;
        }

        _refreshButton.Enabled = false;
        string original = _refreshButton.Text;
        _refreshButton.Text = "Meta刷新中...";
        SetCoachStatus("V4：正在手动刷新 MetaTFT，并重新生成 LineUps.json...");

        var progress = new Progress<string>(text =>
        {
            SetCoachStatus(text);
            if (_refreshButton != null && !_refreshButton.IsDisposed)
                _refreshButton.ToolTipText = text;
        });

        try
        {
            MetaLineupRefreshResult result = await _coordinator.RefreshMetaAndRebuildAsync(progress);
            SetCoachStatus(result.Message);
            if (result.Success)
            {
                MessageBox.Show(
                    $"{result.Message}\n\n正式文件：{result.OutputPath}\n备份：{(string.IsNullOrWhiteSpace(result.BackupPath) ? "无" : result.BackupPath)}\n\n生成提示词与模板已保存在：\n{LineupGenerationAssets.RootDirectory}",
                    "Meta阵容刷新完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(result.Message, "Meta阵容刷新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (_refreshButton != null)
            {
                _refreshButton.Enabled = true;
                _refreshButton.Text = original;
                _refreshButton.ToolTipText = "手动读取MetaTFT当前版本数据，调用AI生成LineUps.json并立即更新阵容库";
            }
        }
    }

    private static void AttachCoachLinkage(AiCoachForm coach)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(AiCoachForm).GetField("_recommendationList", flags)?.GetValue(coach) is not ListView list)
            return;
        if (ReferenceEquals(list, _recommendationList)) return;

        if (_recommendationList != null)
            _recommendationList.ItemSelectionChanged -= RecommendationList_ItemSelectionChanged;

        _recommendationList = list;
        list.HideSelection = false;
        list.ItemSelectionChanged += RecommendationList_ItemSelectionChanged;
        list.ShowItemToolTips = true;
    }

    private static void RecommendationList_ItemSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        if (!e.IsSelected || _coordinator == null) return;

        int stageIndex = 0;
        if (e.Item.SubItems.Count >= 3)
        {
            stageIndex = e.Item.SubItems[2].Text switch
            {
                "中期" => 1,
                "后期" => 2,
                _ => 0
            };
        }

        bool ok = _coordinator.TrySelectLineup(e.Item.Text, stageIndex, out string message);
        SetCoachStatus(message);
        if (!ok) e.Item.ToolTipText = message + Environment.NewLine + e.Item.ToolTipText;
    }

    private static void SetCoachStatus(string text)
    {
        AiCoachForm? coach = _coachForm ?? Application.OpenForms.OfType<AiCoachForm>().FirstOrDefault();
        if (coach == null || coach.IsDisposed) return;

        void Apply()
        {
            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (typeof(AiCoachForm).GetField("_statusLabel", flags)?.GetValue(coach) is Label label)
                    label.Text = text;
            }
            catch { }
        }

        if (coach.InvokeRequired) coach.BeginInvoke((Action)Apply);
        else Apply();
    }
}
