using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace MyCalendar;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // 同じアプリを2つ開いて予定が食い違わないようにする
        using var mutex = new Mutex(true, "MyCalendar.SingleInstance.v1", out bool first);
        if (!first)
        {
            MessageBox.Show("マイカレンダーはすでに開いています。タスクバーを確認してください。", "マイカレンダー");
            return;
        }

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
        app.Run(new MainWindow(store));
    }
}
