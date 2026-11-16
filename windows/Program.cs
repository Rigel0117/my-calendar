using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace MyCalendar;

public static class Program
{
    const string ShowEventName = "MyCalendar.Show.v1";

    [STAThread]
    public static void Main(string[] args)
    {
        // 同じアプリを2つ開いて予定が食い違わないようにする。
        // 2つ目を開こうとしたときは、すでに開いている方を前に出す。
        using var mutex = new Mutex(true, "MyCalendar.SingleInstance.v1", out bool first);
        if (!first)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch
            {
                MessageBox.Show("マイカレンダーはすでに開いています。タスクトレイを確認してください。", "マイカレンダー");
            }
            return;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        // 日付や漢字の表示を日本語にする
        var ja = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentCulture = ja;
        CultureInfo.DefaultThreadCurrentUICulture = ja;
        Thread.CurrentThread.CurrentCulture = ja;
        Thread.CurrentThread.CurrentUICulture = ja;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ja-JP")));

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                File.AppendAllText(System.IO.Path.Combine(Storage.Dir, "error.log"), $"{DateTime.Now:o}\n{e.Exception}\n\n");
            }
            catch { }
            MessageBox.Show("問題が発生しました。予定は保存されています。\n" + e.Exception.Message, "マイカレンダー", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };

        var store = Storage.Load();
        var main = new MainWindow(store);
        app.MainWindow = main;

        var waiter = new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne())
                    app.Dispatcher.Invoke(() => main.ShowFromTray());
            }
            catch { }
        })
        { IsBackground = true };
        waiter.Start();

        // Windows起動時の自動起動（--tray）のときは、タスクトレイに入った状態で始める
        bool startHidden = args.Any(a => a == "--tray") && store.Settings.CloseToTray;
        if (!startHidden) main.Show();
        app.Run();
    }
}
