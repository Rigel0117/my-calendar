using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace MyCalendar;

public class MainWindow : Window
{
    public readonly Store Store;
    public Palette P;
    DateOnly today, selected;
    int vy, vm;
    int lastMaxChips = -1;

    // 画面の部品
    Grid calGrid = null!;
    UniformGrid dowRow = null!;
    TextBlock titleText = null!, sideDate = null!, sideSub = null!, syncText = null!;
    Shapes.Ellipse syncDot = null!;
    StackPanel list = null!;

    // 同期
    public SyncState SyncStateNow = SyncState.Local;
    public string SyncMessage = "";
    bool syncing, syncAgain;
    DateTime lastActivatedSync = DateTime.MinValue;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer minute = new() { Interval = TimeSpan.FromMinutes(1) };
    int minuteCount;

    public MainWindow(Store store)
    {
        Store = store;
        P = Palette.For(store.Settings.Theme);
        today = Dates.Today; selected = today; vy = today.Year; vm = today.Month;

        Title = "マイカレンダー";
        MinWidth = 760; MinHeight = 540;
        var s = store.Settings;
        Width = s.WinWidth > 0 ? s.WinWidth : 1180;
        Height = s.WinHeight > 0 ? s.WinHeight : 760;
        if (!double.IsNaN(s.WinLeft) && !double.IsNaN(s.WinTop) && OnScreen(s.WinLeft, s.WinTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WinLeft; Top = s.WinTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.Maximized) WindowState = WindowState.Maximized;

        FontFamily = UI.Body; FontSize = 14;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        BuildUI();

        SourceInitialized += (_, _) => UI.TitleBar(this, P.Dark);
        Loaded += async (_, _) => await DoSync(false);
        Activated += async (_, _) =>
        {
            if (Dates.Today != today) Render();
            if ((DateTime.Now - lastActivatedSync).TotalSeconds > 30) { lastActivatedSync = DateTime.Now; await DoSync(false); }
        };
        Closing += (_, _) => SaveBounds();
        PreviewKeyDown += OnKey;

        debounce.Tick += async (_, _) => { debounce.Stop(); await DoSync(false); };
        minute.Tick += async (_, _) =>
        {
            if (Dates.Today != today) Render();
            if (++minuteCount % 5 == 0) await DoSync(false);
        };
        minute.Start();
        NetworkChange.NetworkAvailabilityChanged += (_, e) => Dispatcher.InvokeAsync(async () =>
        {
            if (e.IsAvailable) await DoSync(false);
            else { SyncStateNow = Store.GetToken() == "" ? SyncState.Local : SyncState.Pending; SyncMessage = "オフライン"; PaintSync(); }
        });
    }

    static bool OnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 20 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    void SaveBounds()
    {
        var s = Store.Settings;
        s.Maximized = WindowState == WindowState.Maximized;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!r.IsEmpty) { s.WinLeft = r.Left; s.WinTop = r.Top; s.WinWidth = r.Width; s.WinHeight = r.Height; }
        TrySave();
    }

    public void TrySave()
    {
        try { Storage.Save(Store); }
        catch (Exception ex) { MessageBox.Show(this, "予定を保存できませんでした。\n" + ex.Message, "マイカレンダー", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ================= 画面づくり =================
    public void BuildUI()
    {
        Background = P.Paper;
        Foreground = P.Ink;
        lastMaxChips = -1;

        var root = new Grid { Margin = new Thickness(16, 12, 16, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        // ---- ヘッダー ----
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        Grid.SetColumnSpan(header, 3);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(UI.IconBtn(P, "", () => Go(-1), "前の月（←キー）"));
        titleText = new TextBlock
        {
            FontFamily = UI.Num, FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 2), MinWidth = 150, TextAlignment = TextAlignment.Center,
        };
        left.Children.Add(titleText);
        left.Children.Add(UI.IconBtn(P, "", () => Go(1), "次の月（→キー）"));
        var todayBtn = UI.Btn(P, "今日", GoToday, UI.Kind.Normal, "今日に戻る（Tキー）");
        todayBtn.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(todayBtn);
        DockPanel.SetDock(left, Dock.Left);
        header.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        syncDot = new Shapes.Ellipse { Width = 9, Height = 9, Fill = P.Muted, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        syncText = new TextBlock { Text = "この PC に保存", FontSize = 12.5, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        var pill = new StackPanel { Orientation = Orientation.Horizontal };
        pill.Children.Add(syncDot); pill.Children.Add(syncText);
        var syncBtn = UI.Btn(P, pill, OpenSettings, UI.Kind.Normal, "同期の状態（押すと設定）");
        syncBtn.Foreground = P.Muted;
        var search = UI.IconBtn(P, "", OpenSearch, "予定を検索（Ctrl+F）");
        search.Margin = new Thickness(8, 0, 0, 0);
        var settings = UI.IconBtn(P, "", OpenSettings, "設定");
        settings.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(syncBtn); right.Children.Add(search); right.Children.Add(settings);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        root.Children.Add(header);

        // ---- 月のカレンダー ----
        var calBorder = new Border { Background = P.Sheet, BorderBrush = P.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        Grid.SetRow(calBorder, 1);
        var calInner = new Grid();
        calInner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        calInner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        dowRow = new UniformGrid { Columns = 7 };
        var dowBorder = new Border { BorderBrush = P.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = dowRow };
        calInner.Children.Add(dowBorder);
        calGrid = new Grid();
        Grid.SetRow(calGrid, 1);
        calInner.Children.Add(calGrid);
        calBorder.Child = calInner;
        calInner.SizeChanged += (_, _) =>
        {
            calInner.Clip = new RectangleGeometry(new Rect(0, 0, calInner.ActualWidth, calInner.ActualHeight), 9, 9);
        };
        calGrid.SizeChanged += (_, _) => { if (MaxChips() != lastMaxChips) Render(); };
        calBorder.MouseWheel += (_, e) => Go(e.Delta < 0 ? 1 : -1);
        root.Children.Add(calBorder);

        // ---- 選んだ日の予定 ----
        var side = new Border { Background = P.Sheet, BorderBrush = P.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        Grid.SetRow(side, 1); Grid.SetColumn(side, 2);
        var sideDock = new DockPanel();
        var sideHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 12) };
        sideDate = new TextBlock { FontFamily = UI.Num, FontSize = 32, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom };
        sideSub = new TextBlock { Foreground = P.Muted, FontSize = 13, Margin = new Thickness(12, 0, 0, 5), VerticalAlignment = VerticalAlignment.Bottom };
        sideHead.Children.Add(sideDate); sideHead.Children.Add(sideSub);
        var headBorder = new Border { BorderBrush = P.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = sideHead };
        DockPanel.SetDock(headBorder, Dock.Top);
        sideDock.Children.Add(headBorder);
        var add = UI.Btn(P, "＋ この日に予定を追加", () => OpenEditor(null, selected, null), UI.Kind.Primary, "予定を追加（Nキー）");
        add.Margin = new Thickness(10); add.Padding = new Thickness(12, 11, 12, 11);
        DockPanel.SetDock(add, Dock.Bottom);
        sideDock.Children.Add(add);
        list = new StackPanel { Margin = new Thickness(8) };
        sideDock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        side.Child = sideDock;
        root.Children.Add(side);

        Content = root;
        Render();
        PaintSync();
    }

    int MaxChips()
    {
        int rows = calGrid.RowDefinitions.Count == 0 ? 6 : calGrid.RowDefinitions.Count;
        double cellH = calGrid.ActualHeight / rows;
        return Math.Max(1, (int)((cellH - 32) / 20));
    }

    // ================= 描画 =================
    public void Render()
    {
        today = Dates.Today;
        int ws = Store.Settings.WeekStart == 1 ? 1 : 0;
        titleText.Text = $"{vy}年 {vm}月";

        dowRow.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            int w = (i + ws) % 7;
            dowRow.Children.Add(new TextBlock
            {
                Text = Dates.WD[w], FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 6),
                Foreground = w == 0 ? P.Sun : w == 6 ? P.Sat : P.Muted,
            });
        }

        var first = new DateOnly(vy, vm, 1);
        int lead = ((int)first.DayOfWeek - ws + 7) % 7;
        var start = first.AddDays(-lead);
        int days = (int)Math.Ceiling((lead + DateTime.DaysInMonth(vy, vm)) / 7.0) * 7;
        int rows = days / 7;

        calGrid.Children.Clear();
        calGrid.RowDefinitions.Clear();
        calGrid.ColumnDefinitions.Clear();
        for (int r = 0; r < rows; r++) calGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < 7; c++) calGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int maxChips = lastMaxChips = MaxChips();
        for (int i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            var cell = DayCell(d, maxChips, i % 7 == 6, i / 7 == rows - 1);
            Grid.SetRow(cell, i / 7); Grid.SetColumn(cell, i % 7);
            calGrid.Children.Add(cell);
        }
        RenderSide();
    }

    FrameworkElement DayCell(DateOnly d, int maxChips, bool lastCol, bool lastRow)
    {
        bool other = d.Month != vm;
        string hol = Dates.HolidayName(d);
        var evs = Dates.EventsOn(Store, d);

        var cell = new Grid { Background = other ? P.Tint(P.Soft.Color.ToString(), P.Dark ? 0.5 : 0.6) : Brushes.Transparent, Cursor = Cursors.Hand };
        var border = new Border { BorderBrush = P.Line, BorderThickness = new Thickness(0, 0, lastCol ? 0 : 1, lastRow ? 0 : 1) };
        cell.Children.Add(border);

        var stack = new StackPanel { Margin = new Thickness(5, 4, 5, 2) };
        var numRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var numBrush = d.DayOfWeek == DayOfWeek.Sunday || hol != "" ? P.Sun : d.DayOfWeek == DayOfWeek.Saturday ? P.Sat : P.Ink;
        var num = new TextBlock
        {
            Text = d.Day.ToString(), FontFamily = UI.Num, FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = d == today ? P.AccentInk : numBrush,
        };
        var numBox = new Grid { Width = 26, Height = 26 };
        if (d == today) numBox.Children.Add(new Shapes.Ellipse { Fill = P.Accent });
        numBox.Children.Add(num);
        if (other) numBox.Opacity = 0.45;
        numRow.Children.Add(numBox);
        if (hol != "")
            numRow.Children.Add(new TextBlock { Text = hol, FontSize = 10.5, Foreground = P.Sun, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(numRow);

        int show = evs.Count > maxChips ? maxChips - 1 : maxChips;
        foreach (var (e, st) in evs.Take(Math.Max(show, 0)))
        {
            var tb = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = e.AllDay ? Brushes.White : P.Ink };
            if (!e.AllDay && st == d && !string.IsNullOrEmpty(e.Start)) tb.Inlines.Add(new Run(e.Start + " ") { FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new Run(e.Title ?? ""));
            var col = Palette.B(Palette.C(e.Color));
            stack.Children.Add(new Border
            {
                Child = tb, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(5, 1, 5, 1),
                Background = e.AllDay ? col : P.Tint(e.Color),
                BorderBrush = col, BorderThickness = new Thickness(e.AllDay ? 0 : 3, 0, 0, 0),
            });
        }
        if (evs.Count > Math.Max(show, 0))
            stack.Children.Add(new TextBlock { Text = $"ほか{evs.Count - Math.Max(show, 0)}件", FontSize = 11, Foreground = P.Muted, Margin = new Thickness(4, 0, 0, 0) });
        cell.Children.Add(stack);

        if (d == selected)
            cell.Children.Add(new Border { BorderBrush = P.Ink, BorderThickness = new Thickness(2), IsHitTestVisible = false, Margin = new Thickness(0, 0, lastCol ? 0 : 1, lastRow ? 0 : 1) });

        cell.MouseEnter += (_, _) => { if (d != selected) border.Background = P.Tint(P.Soft.Color.ToString(), 0.7); };
        cell.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { OpenEditor(null, d, null); e.Handled = true; return; }
            selected = d;
            if (d.Month != vm || d.Year != vy) { vy = d.Year; vm = d.Month; }
            Render();
        };
        cell.ToolTip = $"{d.Month}月{d.Day}日（{Dates.WD[(int)d.DayOfWeek]}） ダブルクリックで予定を追加";
        return cell;
    }

    void RenderSide()
    {
        var d = selected;
        string hol = Dates.HolidayName(d);
        sideDate.Text = $"{d.Month}/{d.Day}";
        sideSub.Inlines.Clear();
        sideSub.Inlines.Add(new Run($"{d.Year}年・{Dates.WD[(int)d.DayOfWeek]}曜日{(d == today ? "・今日" : "")}"));
        if (hol != "") { sideSub.Inlines.Add(new LineBreak()); sideSub.Inlines.Add(new Run(hol) { Foreground = P.Sun }); }

        list.Children.Clear();
        var evs = Dates.EventsOn(Store, d);
        if (evs.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "予定はありません", Foreground = P.Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 0) });
            return;
        }
        foreach (var (e, st) in evs)
            list.Children.Add(EventItem(e, st, Dates.TimeLabel(e, st, d), () => OpenEditor(e.Id, d, st)));
    }

    public FrameworkElement EventItem(CalEvent e, DateOnly st, string timeText, Action onClick, double timeWidth = 96)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(timeWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new Border { Background = Palette.B(Palette.C(e.Color)), CornerRadius = new CornerRadius(2) });
        var t = new TextBlock { Text = timeText, Foreground = P.Muted, FontSize = 12.5, Margin = new Thickness(10, 1, 6, 0) };
        Grid.SetColumn(t, 1); g.Children.Add(t);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = e.Title ?? "", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var rep = Dates.RepeatLabel(e.Repeat);
        if (rep != "") body.Children.Add(new TextBlock { Text = "↻ " + rep, FontSize = 11.5, Foreground = P.Muted });
        if (!string.IsNullOrWhiteSpace(e.Memo)) body.Children.Add(new TextBlock { Text = e.Memo, FontSize = 12.5, Foreground = P.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(body, 2); g.Children.Add(body);

        var item = new Border { Child = g, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        item.MouseEnter += (_, _) => item.Background = P.Soft;
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonUp += (_, _) => onClick();
        item.ToolTip = "押すと編集";
        return item;
    }

    // ================= 操作 =================
    void Go(int delta)
    {
        vm += delta;
        if (vm < 1) { vm = 12; vy--; }
        if (vm > 12) { vm = 1; vy++; }
        Render();
    }

    void GoToday() { today = Dates.Today; selected = today; vy = today.Year; vm = today.Month; Render(); }

    public void JumpTo(DateOnly d) { selected = d; vy = d.Year; vm = d.Month; Render(); }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { OpenSearch(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Left: Go(-1); e.Handled = true; break;
            case Key.Right: Go(1); e.Handled = true; break;
            case Key.N: OpenEditor(null, selected, null); e.Handled = true; break;
            case Key.T: GoToday(); e.Handled = true; break;
        }
    }

    void OpenEditor(string? id, DateOnly date, DateOnly? occ)
    {
        CalEvent? existing = id != null && Store.Events.TryGetValue(id, out var ex) ? ex : null;
        var dlg = new EventDialog(this, P, existing, date, occ);
        dlg.ShowDialog();
        if (dlg.Result == null) return;
        Store.Events[dlg.Result.Id] = dlg.Result;
        if (!dlg.Result.Deleted && Dates.TryParse(dlg.Result.Date, out var d) && dlg.JumpToDate) JumpTo(d); else Render();
        Changed();
    }

    void OpenSearch() => new SearchDialog(this).ShowDialog();

    void OpenSettings() => new SettingsDialog(this).ShowDialog();

    public void Changed()
    {
        Store.Sync.Dirty = true;
        TrySave();
        PaintSync();
        debounce.Stop(); debounce.Start();
    }

    public void ApplyTheme()
    {
        P = Palette.For(Store.Settings.Theme);
        UI.TitleBar(this, P.Dark);
        BuildUI();
    }

    // ================= 同期 =================
    public async Task DoSync(bool manual)
    {
        if (syncing) { syncAgain = true; return; }
        if (Store.GetToken() == "") { SyncStateNow = SyncState.Local; PaintSync(); return; }
        syncing = true; PaintSync();
        try
        {
            var (state, msg, changed) = await GistSync.Run(Store);
            SyncStateNow = state; SyncMessage = msg;
            TrySave();
            if (changed) Render();
            if (manual)
                MessageBox.Show(this, state == SyncState.Ok ? "同期しました。" : msg, "同期", MessageBoxButton.OK,
                    state == SyncState.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            syncing = false; PaintSync();
            if (syncAgain) { syncAgain = false; debounce.Stop(); debounce.Start(); }
        }
    }

    public string SyncSummary()
    {
        if (Store.GetToken() == "") return "同期：未設定。トークンを入れるとスマホと同期します。";
        string s = SyncStateNow switch
        {
            SyncState.Ok => "同期：OK",
            SyncState.Error => "同期：エラー – " + SyncMessage,
            SyncState.Pending => "同期：オフライン（つながったら送ります）",
            _ => "同期：待機中",
        };
        if (Store.Sync.Last > 0) s += "\n最後に同期：" + FmtTime(Store.Sync.Last);
        return s;
    }

    static string FmtTime(double ms)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
        return (DateOnly.FromDateTime(t) == Dates.Today ? "" : $"{t.Month}/{t.Day} ") + t.ToString("HH:mm");
    }

    public void PaintSync()
    {
        if (syncText == null) return;
        if (syncing) { syncDot.Fill = Palette.B("#2e9d5b"); syncText.Text = "同期中…"; return; }
        if (Store.GetToken() == "") { syncDot.Fill = P.Muted; syncText.Text = "この PC に保存"; return; }
        switch (SyncStateNow)
        {
            case SyncState.Ok:
                syncDot.Fill = Palette.B("#2e9d5b");
                syncText.Text = Store.Sync.Dirty ? "未送信あり" : "同期済み " + FmtTime(Store.Sync.Last);
                break;
            case SyncState.Pending:
                syncDot.Fill = Palette.B("#d99a1e");
                syncText.Text = Store.Sync.Dirty ? "オフライン・未送信あり" : "オフライン";
                break;
            case SyncState.Error:
                syncDot.Fill = P.Sun; syncText.Text = "同期エラー";
                break;
            default:
                syncDot.Fill = P.Muted; syncText.Text = Store.Sync.Dirty ? "未送信あり" : "同期待ち";
                break;
        }
    }
}
