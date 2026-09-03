using System.Reflection;
using System.Runtime.CompilerServices;

namespace JinChanChanTool.Services.AICoach;

/// <summary>
/// V4.1 安全默认值：OCR尚未得到血量时，0 表示“未知”。
/// 绝不能把未知血量默认为100，否则会错误鼓励Fast 8/Fast 9。
/// </summary>
public static class V41SafetyDefaults
{
    private static bool _applied;

    [ModuleInitializer]
    public static void Initialize()
    {
        Application.Idle += ApplyWhenReady;
    }

    private static void ApplyWhenReady(object? sender, EventArgs e)
    {
        if (_applied) return;
        AiCoachForm? form = Application.OpenForms.OfType<AiCoachForm>().FirstOrDefault(x => !x.IsDisposed);
        if (form == null) return;

        try
        {
            LiveHudSnapshot hud = LiveHudState.GetSnapshot();
            if (!hud.Hp.HasValue)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (typeof(AiCoachForm).GetField("_hpBox", flags)?.GetValue(form) is NumericUpDown hpBox)
                    hpBox.Value = 0;
            }
            _applied = true;
        }
        catch { }
    }
}
