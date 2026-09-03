using System.Reflection;
using System.Runtime.CompilerServices;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public static class AiCoachBootstrap
{
    private static AiCoachForm? _coachForm;
    private static BoardTraitWatcher? _boardWatcher;
    private static HudStateWatcher? _hudWatcher;
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

            ApplyV22UiState(_coachForm);
            LiveBoardState.Changed += OnBoardStateChanged;
            LiveHudState.Changed += OnHudStateChanged;

            _boardWatcher = new BoardTraitWatcher(main, cardService);
            _boardWatcher.Start();

            _hudWatcher = new HudStateWatcher(main, cardService);
            _hudWatcher.Start();

            if (!_exitHooked)
            {
                _exitHooked = true;
                Application.ApplicationExit += (_, _) =>
                {
                    LiveBoardState.Changed -= OnBoardStateChanged;
                    LiveHudState.Changed -= OnHudStateChanged;
                    _boardWatcher?.Dispose();
                    _boardWatcher = null;
                    _hudWatcher?.Dispose();
                    _hudWatcher = null;
                };
            }

            _coachForm.Show(main);
        }
        finally
        {
            _attaching = false;
        }
    }

    private static void ApplyV22UiState(AiCoachForm form)
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
                equipmentLabel.Text = "装备自动识别暂时关闭：下一步改为悬停 Tooltip OCR，当前可手动补充。";
            }
            if (typeof(AiCoachForm).GetField("_statusLabel", flags)?.GetValue(form) is Label statusLabel)
            {
                statusLabel.Text = "V2.2：实时读取阶段/等级/金币/血量，并结合上场棋盘、羁绊与商店推荐。";
            }
            form.Text = "AI 云顶教练 V2.2｜正在读取实时局面...";
        }
        catch
        {
            form.Text = "AI 云顶教练 V2.2";
        }
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
        catch
        {
            // 窗口关闭/句柄切换时忽略一次 UI 更新。
        }
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

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (typeof(AiCoachForm).GetField("_statusLabel", flags)?.GetValue(form) is Label statusLabel)
                {
                    if (snapshot.InferredHeroes.Count > 0)
                    {
                        statusLabel.Text = $"已从羁绊反推上场：{string.Join("、", snapshot.InferredHeroes)}；阶段/等级/金币/血量同步自动读取。";
                    }
                    else if (snapshot.Traits.Count > 0)
                    {
                        string traits = string.Join("、", snapshot.Traits.Select(x => $"{x.Key}{x.Value}"));
                        string suffix = snapshot.CandidateCombinationCount > 1
                            ? $"；存在 {snapshot.CandidateCombinationCount} 组可能棋子，不强行猜英雄。"
                            : "";
                        statusLabel.Text = $"已识别上场羁绊：{traits}{suffix}；HUD 自动读取中。";
                    }
                }
            }));
        }
        catch
        {
            // 窗口关闭/句柄切换时忽略一次 UI 更新。
        }
    }

    private static void UpdateWindowTitle(AiCoachForm form)
    {
        LiveHudSnapshot hud = LiveHudState.GetSnapshot();
        LiveBoardSnapshot board = LiveBoardState.GetSnapshot();
        string hudText = BuildHudTitle(hud);

        if (board.InferredHeroes.Count > 0)
            form.Text = $"AI 云顶教练 V2.2{hudText}｜上场：{string.Join(" / ", board.InferredHeroes)}";
        else if (board.Traits.Count > 0)
            form.Text = $"AI 云顶教练 V2.2{hudText}｜羁绊：{string.Join(" / ", board.Traits.Take(4).Select(x => $"{x.Key}{x.Value}"))}";
        else
            form.Text = $"AI 云顶教练 V2.2{hudText}";
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
