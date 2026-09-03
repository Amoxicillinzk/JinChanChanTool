using System.Reflection;
using System.Runtime.CompilerServices;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public static class AiCoachBootstrap
{
    private static AiCoachForm? _coachForm;
    private static BoardTraitWatcher? _boardWatcher;
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

            ApplyV21UiState(_coachForm);
            LiveBoardState.Changed += OnBoardStateChanged;
            _boardWatcher = new BoardTraitWatcher(main, cardService);
            _boardWatcher.Start();

            if (!_exitHooked)
            {
                _exitHooked = true;
                Application.ApplicationExit += (_, _) =>
                {
                    LiveBoardState.Changed -= OnBoardStateChanged;
                    _boardWatcher?.Dispose();
                    _boardWatcher = null;
                };
            }

            _coachForm.Show(main);
        }
        finally
        {
            _attaching = false;
        }
    }

    private static void ApplyV21UiState(AiCoachForm form)
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
                equipmentLabel.Text = "V2.1 已暂停图标模板识别：旧版会把羁绊栏误判成装备；装备暂时手动补充。";
            }
            if (typeof(AiCoachForm).GetField("_statusLabel", flags)?.GetValue(form) is Label statusLabel)
            {
                statusLabel.Text = "V2.1：自动读取已上场羁绊并参与阵容推荐；上场棋盘权重高于商店。";
            }
            form.Text = "AI 云顶教练 V2.1｜正在识别上场棋盘...";
        }
        catch
        {
            form.Text = "AI 云顶教练 V2.1";
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

                if (snapshot.InferredHeroes.Count > 0)
                {
                    form.Text = $"AI 云顶教练 V2.1｜上场：{string.Join(" / ", snapshot.InferredHeroes)}";
                }
                else if (snapshot.Traits.Count > 0)
                {
                    string traits = string.Join(" / ", snapshot.Traits.Take(5).Select(x => $"{x.Key}{x.Value}"));
                    form.Text = $"AI 云顶教练 V2.1｜羁绊：{traits}";
                }
                else if (!string.IsNullOrWhiteSpace(snapshot.Error))
                {
                    form.Text = "AI 云顶教练 V2.1｜棋盘识别待校准";
                }

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (typeof(AiCoachForm).GetField("_statusLabel", flags)?.GetValue(form) is Label statusLabel)
                {
                    if (snapshot.InferredHeroes.Count > 0)
                    {
                        statusLabel.Text = $"已从羁绊唯一反推上场：{string.Join("、", snapshot.InferredHeroes)}；推荐将以上场棋盘为主。";
                    }
                    else if (snapshot.Traits.Count > 0)
                    {
                        string traits = string.Join("、", snapshot.Traits.Select(x => $"{x.Key}{x.Value}"));
                        string suffix = snapshot.CandidateCombinationCount > 1
                            ? $"；存在 {snapshot.CandidateCombinationCount} 组可能棋子，不强行猜英雄。"
                            : "";
                        statusLabel.Text = $"已识别上场羁绊：{traits}{suffix}";
                    }
                    else if (!string.IsNullOrWhiteSpace(snapshot.Error))
                    {
                        statusLabel.Text = $"上场棋盘识别：{snapshot.Error}";
                    }
                }
            }));
        }
        catch
        {
            // 窗口关闭/句柄切换时忽略一次 UI 更新。
        }
    }
}
