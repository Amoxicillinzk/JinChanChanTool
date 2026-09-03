using System.Reflection;
using System.Runtime.CompilerServices;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public static class AiCoachBootstrap
{
    private static AiCoachForm? _coachForm;
    private static BoardTraitWatcher? _boardWatcher;
    private static HudStateWatcher? _hudWatcher;
    private static OnlineMetaService? _onlineMetaService;
    private static bool _attaching;
    private static bool _exitHooked;

    [ModuleInitializer]
    public static void Initialize()
    {
        Application.Idle += OnApplicationIdle;
    }

    private static void OnApplicationIdle(object? sender, EventArgs e)
    {
        if (_attaching || _coachForm != null) return;
        var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null || !main.Visible || main.IsDisposed) return;

        _attaching = true;
        try
        {
            var cardField = typeof(MainForm).GetField("_cardService", BindingFlags.Instance | BindingFlags.NonPublic);
            var lineupField = typeof(MainForm).GetField("_iLineUpService", BindingFlags.Instance | BindingFlags.NonPublic);
            if (cardField?.GetValue(main) is not CardService cardService) return;
            if (lineupField?.GetValue(main) is not ILineUpService lineUpService) return;

            _coachForm = new AiCoachForm(cardService, lineUpService, main)
            {
                TopMost = main.TopMost
            };

            Screen gameScreen = Screen.FromControl(main);
            Screen targetScreen = Screen.AllScreens.FirstOrDefault(s =>
                !string.Equals(s.DeviceName, gameScreen.DeviceName, StringComparison.OrdinalIgnoreCase)) ?? gameScreen;
            Rectangle working = targetScreen.WorkingArea;

            int x = targetScreen == gameScreen
                ? Math.Min(working.Right - _coachForm.Width, main.Right + 8)
                : working.Left + 16;
            int y = Math.Max(working.Top + 8, Math.Min(main.Top, working.Bottom - _coachForm.Height));
            _coachForm.Location = new Point(Math.Max(working.Left, x), y);

            ApplyV4UiState(_coachForm);
            LiveBoardState.Changed += OnBoardStateChanged;
            LiveHudState.Changed += OnHudStateChanged;
            OnlineMetaState.Changed += OnOnlineMetaChanged;

            _boardWatcher = new BoardTraitWatcher(main, cardService);
            _boardWatcher.Start();

            _hudWatcher = new HudStateWatcher(main, cardService);
            _hudWatcher.Start();

            // V4：启动时只读取上次手动刷新生成时使用的 Meta 缓存。
            // 不后台访问网络，避免 AI 教练与 LineUps.json 漂移成两个版本。
            _onlineMetaService = new OnlineMetaService();
            _onlineMetaService.LoadCacheOnly();

            if (!_exitHooked)
            {
                _exitHooked = true;
                Application.ApplicationExit += (_, _) =>
                {
                    LiveBoardState.Changed -= OnBoardStateChanged;
                    LiveHudState.Changed -= OnHudStateChanged;
                    OnlineMetaState.Changed -= OnOnlineMetaChanged;
                    _boardWatcher?.Dispose();
                    _boardWatcher = null;
                    _hudWatcher?.Dispose();
                    _hudWatcher = null;
                    _onlineMetaService?.Dispose();
                    _onlineMetaService = null;
                };
            }

            _coachForm.Show(main);
        }
        finally
        {
            _attaching = false;
        }
    }

    private static void ApplyV4UiState(AiCoachForm form)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            if (typeof(AiCoachForm).GetField("_autoEquipmentCheck", flags)?.GetValue(form) is CheckBox autoEquipment)
            {
                autoEquipment.Checked = false;
                autoEquipment.Enabled = false;
            }
            if (typeof(AiCoachForm).GetField("_autoEquipmentLabel", flags)?.GetValue(form) is Label equipmentLabel)
            {
                equipmentLabel.Text = "装备自动识别暂时关闭：后续改为悬停 Tooltip OCR，当前可手动补充。";
            }
            if (typeof(AiCoachForm).GetField("_statusLabel", flags)?.GetValue(form) is Label statusLabel)
            {
                OnlineMetaSnapshot meta = OnlineMetaState.GetSnapshot();
                statusLabel.Text = meta.HasData
                    ? $"V4：已载入上次手动刷新 Meta（{meta.Comps.Count}套）。点主程序“刷新Meta阵容”才会更新版本并重建LineUps.json。"
                    : "V4：尚无固定Meta快照，请在主程序菜单点击“刷新Meta阵容”。";
            }
            form.Text = "AI 云顶教练 V4｜Meta与阵容库同步模式";
        }
        catch
        {
            form.Text = "AI 云顶教练 V4";
        }
    }

    private static void OnOnlineMetaChanged(OnlineMetaSnapshot snapshot)
    {
        AiCoachForm? form = _coachForm;
        if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            form.BeginInvoke(new Action(() =>
            {
                if (form.IsDisposed) return;
                UpdateWindowTitle(form);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (typeof(AiCoachForm).GetField("_statusLabel", flags)?.GetValue(form) is Label statusLabel)
                {
                    if (snapshot.HasData)
                    {
                        string cache = snapshot.FromCache ? "（固定缓存）" : "（刚手动刷新）";
                        statusLabel.Text = $"Meta：{snapshot.Source}{cache}，{snapshot.Comps.Count}套，版本快照时间 {snapshot.UpdatedAt:MM-dd HH:mm}。主程序与AI教练共用此快照。";
                    }
                    else if (!string.IsNullOrWhiteSpace(snapshot.Error))
                    {
                        statusLabel.Text = $"Meta不可用：{snapshot.Error}；请点击主程序“刷新Meta阵容”。";
                    }
                }
            }));
        }
        catch { }
    }

    private static void OnHudStateChanged(LiveHudSnapshot snapshot)
    {
        AiCoachForm? form = _coachForm;
        if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            form.BeginInvoke(new Action(() =>
            {
                if (form.IsDisposed) return;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (!string.IsNullOrWhiteSpace(snapshot.Stage) &&
                    typeof(AiCoachForm).GetField("_stageBox", flags)?.GetValue(form) is TextBox stageBox)
                    stageBox.Text = snapshot.Stage;
                if (snapshot.Level.HasValue &&
                    typeof(AiCoachForm).GetField("_levelBox", flags)?.GetValue(form) is NumericUpDown levelBox)
                    levelBox.Value = Math.Clamp(snapshot.Level.Value, (int)levelBox.Minimum, (int)levelBox.Maximum);
                if (snapshot.Gold.HasValue &&
                    typeof(AiCoachForm).GetField("_goldBox", flags)?.GetValue(form) is NumericUpDown goldBox)
                    goldBox.Value = Math.Clamp(snapshot.Gold.Value, (int)goldBox.Minimum, (int)goldBox.Maximum);
                if (snapshot.Hp.HasValue &&
                    typeof(AiCoachForm).GetField("_hpBox", flags)?.GetValue(form) is NumericUpDown hpBox)
                    hpBox.Value = Math.Clamp(snapshot.Hp.Value, (int)hpBox.Minimum, (int)hpBox.Maximum);
                UpdateWindowTitle(form);
            }));
        }
        catch { }
    }

    private static void OnBoardStateChanged(LiveBoardSnapshot snapshot)
    {
        AiCoachForm? form = _coachForm;
        if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            form.BeginInvoke(new Action(() =>
            {
                if (form.IsDisposed) return;
                UpdateWindowTitle(form);
            }));
        }
        catch { }
    }

    private static void UpdateWindowTitle(AiCoachForm form)
    {
        LiveHudSnapshot hud = LiveHudState.GetSnapshot();
        LiveBoardSnapshot board = LiveBoardState.GetSnapshot();
        OnlineMetaSnapshot meta = OnlineMetaState.GetSnapshot();
        string hudText = BuildHudTitle(hud);
        string metaText = meta.HasData ? $"｜Meta {meta.Comps.Count}套" : "｜Meta未刷新";

        if (board.InferredHeroes.Count > 0)
            form.Text = $"AI 云顶教练 V4{hudText}{metaText}｜上场：{string.Join(" / ", board.InferredHeroes)}";
        else if (board.Traits.Count > 0)
            form.Text = $"AI 云顶教练 V4{hudText}{metaText}｜羁绊：{string.Join(" / ", board.Traits.Take(4).Select(x => $"{x.Key}{x.Value}"))}";
        else
            form.Text = $"AI 云顶教练 V4{hudText}{metaText}";
    }

    private static string BuildHudTitle(LiveHudSnapshot hud)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(hud.Stage)) parts.Add(hud.Stage!);
        if (hud.Level.HasValue) parts.Add($"{hud.Level}级");
        if (hud.Gold.HasValue) parts.Add($"{hud.Gold}金");
        if (hud.Hp.HasValue) parts.Add($"{hud.Hp}血");
        return parts.Count == 0 ? "" : "｜" + string.Join(" ", parts);
    }
}
