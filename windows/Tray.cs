using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace MyCalendar;

/// <summary>タスクトレイのアイコンと、Windowsの通知</summary>
public sealed class Tray : IDisposable
{
    readonly WinForms.NotifyIcon icon;
    Action? onBalloonClick;
    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public Tray()
    {
        icon = new WinForms.NotifyIcon { Text = "マイカレンダー" };
        try { icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); }
        catch { icon.Icon = System.Drawing.SystemIcons.Application; }
        icon.Icon ??= System.Drawing.SystemIcons.Application;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        icon.BalloonTipClicked += (_, _) => onBalloonClick?.Invoke();
        icon.Visible = true;
    }

    /// <summary>Windowsの通知を出す（押すと onClick が呼ばれる）</summary>
    public void Notify(string title, string text, Action? onClick)
    {
        onBalloonClick = onClick;
        if (title.Length > 60) title = title[..60] + "…";
        if (text.Length > 250) text = text[..250] + "…";
        icon.ShowBalloonTip(15000, title, text, WinForms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }

    // ---- Windows起動時に自動で開く ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "MyCalendar";

    public static bool AutoStart
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(RunName) is string;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
                else k.DeleteValue(RunName, false);
            }
            catch { }
        }
    }
}
