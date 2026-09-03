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

    private static void OnBoardStateChanged(LiveBoardSnapshot snapshot)
    {
        AiCoachForm? form = _coachForm;
        if (form == null || form.IsDisposed || !form.IsHandleCreated) return;

        try
        {
            form.BeginInvoke(new Action(() =>
            {
                if (form.IsDisposed) return;
                LiveHudSnapshot hud = LiveHudState.GetSnapshot();
                string hudText = BuildHudTitle(hud);

                if (snapshot.InferredHeroes.Count > 0)
                {
                    form.Text = $"AI 云顶教练 V2.2{hudText}｜上场：{string.Join(" / ", snapshot.InferredHeroes)}";
                }
                else if (snapshot.Traits.Count > 0)
                {
                    string traits = string.Join(" / ", snapshot.Traits.Take(4).Select(x => $"{x.Key}{x.Value}"));
                    form.Text = $"AI 云顶教练 V2.2{hudText}｜羁绊：{traits}";
                }
                else if (!string.IsNullOrWhiteSpace(snapshot.Error))
                {
                    form.Text = $"AI 云顶教练 V2.2{hudText}｜棋盘识别待校准";
                }
            }));
        }
        catch
        {
            // 窗口关闭/句柄切换时忽略一次 UI 更新。
        }
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
