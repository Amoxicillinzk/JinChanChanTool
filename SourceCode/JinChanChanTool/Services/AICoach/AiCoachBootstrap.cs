using System.Reflection;
using System.Runtime.CompilerServices;
using JinChanChanTool.Services.DataServices.Interface;

namespace JinChanChanTool.Services.AICoach;

public static class AiCoachBootstrap
{
    private static AiCoachForm? _coachForm;
    private static bool _attaching;

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
            _coachForm.Show(main);
        }
        finally
        {
            _attaching = false;
        }
    }
}
