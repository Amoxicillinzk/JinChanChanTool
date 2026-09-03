using System.Reflection;
using System.Runtime.CompilerServices;

namespace JinChanChanTool.Services.AICoach;

public static class V41UiPolish
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
        AiCoachForm? coach = Application.OpenForms.OfType<AiCoachForm>().FirstOrDefault(x => !x.IsDisposed);
        if (coach == null) return;

        _applied = true;
        coach.Text = "AI 云顶教练 V4.1｜胜率决策引擎";
        try
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            if (typeof(AiCoachForm).GetField("_recommendationList", flags)?.GetValue(coach) is ListView list)
            {
                list.ShowItemToolTips = true;
                if (list.Columns.Count >= 2) list.Columns[1].Text = "适配分";
            }
        }
        catch { }
    }
}
