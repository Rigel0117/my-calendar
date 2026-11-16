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
    // タスク
    string sideTab = "day";
    Grid dayPane = null!, taskPane = null!;
    Button addBtn = null!;
    StackPanel taskList = null!;
    TextBox quick = null!;
    Border tabDay = null!, tabTasks = null!, taskBadge = null!;
    TextBlock taskBadgeText = null!;

    // 同期
    public SyncState SyncStateNow = SyncState.Local;
    public string SyncMessage = "";
    bool syncing, syncAgain;
    DateTime lastActivatedSync = DateTime.MinValue;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer minute = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer remindTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    public readonly Tray Tray = new();
    bool exiting;
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
        // 表示していない（タスクトレイで起動した）ときも同期と通知が動くように
        Dispatcher.InvokeAsync(async () => { CheckReminders(); await DoSync(false); }, DispatcherPriority.ApplicationIdle);
        Activated += async (_, _) =>
        {
            if (Dates.Today != today) Render();
            if ((DateTime.Now - lastActivatedSync).TotalSeconds > 30) { lastActivatedSync = DateTime.Now; await DoSync(false); }
        };
        Closing += (_, e) =>
        {
            SaveBounds();
            if (exiting || !Store.Settings.CloseToTray) return;
            // ×で閉じてもタスクトレイで動き続ける（通知を出すため）
            e.Cancel = true;
            Hide();
            if (!Store.Settings.TrayHintShown)
            {
                Tray.Notify("マイカレンダー", "タスクトレイで動いています。予定の通知はここから届きます。終了するときはアイコンを右クリック →「終了」。", ShowFromTray);
                Store.Settings.TrayHintShown = true;
                TrySave();
            }
        };
        Closed += (_, _) => Tray.Dispose();
        Tray.OpenRequested += ShowFromTray;
        Tray.ExitRequested += ExitApp;
        remindTimer.Tick += (_, _) => CheckReminders();
        remindTimer.Start();
        if (Tray.AutoStart) Tray.AutoStart = true; // exeを移動していても新しい場所で登録し直す
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

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // 前面に出す
        Focus();
    }

    public void ExitApp()
    {
        exiting = true;
        Close();
    }

    /// <summary>通知の時刻になった予定を、Windowsの通知で知らせる</summary>
    public void CheckReminders()
    {
        var now = DateTime.Now;
        bool changed = false;
        foreach (var (e, occ, fire) in Dates.DueReminders(Store, now, TimeSpan.FromMinutes(15)))
        {
            var key = $"{e.Id}|{Dates.F(occ)}|{e.Remind}|{e.Start}";
            if (Store.Notified.ContainsKey(key)) continue;
            Store.Notified[key] = new DateTimeOffset(fire).ToUnixTimeMilliseconds();
            changed = true;
            var d = occ;
            Tray.Notify("🔔 " + (e.Title ?? ""), Dates.ReminderText(e, occ), () => { ShowFromTray(); JumpTo(d); });
        }
        // 3日より前の記録は消す
        var cutoff = DateTimeOffset.Now.AddDays(-3).ToUnixTimeMilliseconds();
        foreach (var k in Store.Notified.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            Store.Notified.Remove(k); changed = true;
        }
        if (changed) TrySave();
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

        // タブ（この日／タスク）
        var tabs = new Grid { Margin = new Thickness(8, 6, 8, 0) };
        tabs.ColumnDefinitions.Add(new ColumnDefinition()); tabs.ColumnDefinitions.Add(new ColumnDefinition());
        tabDay = Tab("この日", null, () => SetTab("day"));
        taskBadgeText = new TextBlock { FontSize = 11, Foreground = P.AccentInk, FontWeight = FontWeights.SemiBold };
        taskBadge = new Border { Background = P.Accent, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(6, 0, 0, 0), Child = taskBadgeText, VerticalAlignment = VerticalAlignment.Center };
        tabTasks = Tab("タスク", taskBadge, () => SetTab("tasks"));
        Grid.SetColumn(tabTasks, 1);
        tabs.Children.Add(tabDay); tabs.Children.Add(tabTasks);
        var tabsBorder = new Border { BorderBrush = P.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs };
        DockPanel.SetDock(tabsBorder, Dock.Top);
        sideDock.Children.Add(tabsBorder);

        var sideHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 18, 12) };
        sideDate = new TextBlock { FontFamily = UI.Num, FontSize = 32, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom };
        sideSub = new TextBlock { Foreground = P.Muted, FontSize = 13, Margin = new Thickness(12, 0, 0, 5), VerticalAlignment = VerticalAlignment.Bottom };
        sideHead.Children.Add(sideDate); sideHead.Children.Add(sideSub);
        var headBorder = new Border { BorderBrush = P.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = sideHead };
        addBtn = UI.Btn(P, "", AddFromSide, UI.Kind.Primary, "追加（Nキー）");
        addBtn.Margin = new Thickness(10); addBtn.Padding = new Thickness(12, 11, 12, 11);
        DockPanel.SetDock(addBtn, Dock.Bottom);
        sideDock.Children.Add(addBtn);

        // 「この日」の中身
        dayPane = new Grid();
        dayPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dayPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        dayPane.Children.Add(headBorder);
        list = new StackPanel { Margin = new Thickness(8) };
        var listScroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(listScroll, 1);
        dayPane.Children.Add(listScroll);

        // 「タスク」の中身
        taskPane = new Grid();
        taskPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        taskPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var quickRow = new DockPanel { Margin = new Thickness(8, 10, 8, 2) };
        var quickAdd = UI.Btn(P, "追加", QuickAdd, UI.Kind.Primary);
        quickAdd.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(quickAdd, Dock.Right);
        quick = UI.Styled(P, new TextBox { MaxLength = 200, VerticalContentAlignment = VerticalAlignment.Center });
        UI.AutomationName(quick, "新しいタスク");
        quick.ToolTip = "タスクを入力して Enter";
        quick.KeyDown += (_, e) => { if (e.Key == Key.Enter) { QuickAdd(); e.Handled = true; } };
        var quickHint = new TextBlock { Text = "タスクを入力して Enter", Foreground = P.Muted, IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        quick.TextChanged += (_, _) => quickHint.Visibility = quick.Text == "" ? Visibility.Visible : Visibility.Collapsed;
        var quickBox = new Grid(); quickBox.Children.Add(quick); quickBox.Children.Add(quickHint);
        quickRow.Children.Add(quickAdd); quickRow.Children.Add(quickBox);
        taskPane.Children.Add(quickRow);
        taskList = new StackPanel { Margin = new Thickness(8) };
        var taskScroll = new ScrollViewer { Content = taskList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(taskScroll, 1);
        taskPane.Children.Add(taskScroll);

        var panes = new Grid();
        panes.Children.Add(dayPane); panes.Children.Add(taskPane);
        sideDock.Children.Add(panes);
        side.Child = sideDock;
        root.Children.Add(side);

        Content = root;
        SetTab(sideTab);
        Render();
        PaintSync();
    }

    Border Tab(string text, UIElement? extra, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        if (extra != null) sp.Children.Add(extra);
        var b = new Border { Child = sp, Padding = new Thickness(0, 9, 0, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand, BorderThickness = new Thickness(0, 0, 0, 2) };
        b.MouseLeftButtonUp += (_, _) => onClick();
        UI.AutomationName(b, text);
        return b;
    }

    void SetTab(string t)
    {
        sideTab = t;
        void Paint(Border tab, bool on)
        {
            tab.BorderBrush = on ? P.Ink : Brushes.Transparent;
            ((TextBlock)((StackPanel)tab.Child).Children[0]).Foreground = on ? P.Ink : P.Muted;
        }
        Paint(tabDay, t == "day"); Paint(tabTasks, t == "tasks");
        dayPane.Visibility = t == "day" ? Visibility.Visible : Visibility.Collapsed;
        taskPane.Visibility = t == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        addBtn.Content = t == "day" ? "＋ この日に予定を追加" : "＋ タスクを追加";
        if (t == "tasks") quick.Focus();
    }

    void AddFromSide()
    {
        if (sideTab == "tasks") OpenEditor(null, selected, null, "task", true);
        else OpenEditor(null, selected, null);
    }

    void QuickAdd()
    {
        var title = quick.Text.Trim();
        if (title == "") return;
        var t = new CalEvent
        {
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x") + Guid.NewGuid().ToString("N")[..6],
            Kind = "task", Title = title, Date = "", EndDate = "", AllDay = true, Start = "", End = "", Repeat = "none", Until = "",
            Exdates = new List<string>(), Color = UI.Colors[0], Memo = "", Done = false, UpdatedAt = Store.Now(),
        };
        Store.Events[t.Id] = t;
        quick.Text = "";
        Render();
        Changed();
    }

    public void ToggleTask(string id)
    {
        if (!Store.Events.TryGetValue(id, out var t)) return;
        var c = t.Clone();
        c.Done = !t.Done;
        c.DoneAt = c.Done ? Store.Now() : null;
        c.UpdatedAt = Store.Now();
        Store.Events[id] = c;
        Render();
        Changed();
    }

    string DueText(CalEvent t, out bool over)
    {
        over = false;
        if (!Dates.TryParse(t.Due, out var d)) return "期限なし";
        string range = t.IsSpanTask && Dates.TryParse(t.Date, out var s0) ? $"{s0.Month}/{s0.Day}〜" : "";
        int dd = d.DayNumber - today.DayNumber;
        string rel = dd switch { 0 => "今日", 1 => "明日", -1 => "昨日", _ => $"{d.Month}/{d.Day}({Dates.WD[(int)d.DayOfWeek]})" };
        string txt = range + rel + (string.IsNullOrEmpty(t.Start) ? "" : " " + t.Start);
        if (!t.Done && d < today) { over = true; return "期限切れ：" + txt; }
        return (range != "" ? "期間：" : "期限：") + txt;
    }

    /// <summary>タスク1件の行（チェックで完了、文字を押すと編集）</summary>
    public FrameworkElement TaskItem(CalEvent t, bool showDue, Action? onOpen = null, Action? onToggle = null)
    {
        var id = t.Id;
        onOpen ??= () => OpenEditor(id, selected, null);
        onToggle ??= () => ToggleTask(id);
        var col = Palette.B(Palette.C(t.Color));

        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var box = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), BorderBrush = col, BorderThickness = new Thickness(2),
            Background = t.Done ? col : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 1, 0, 0), Cursor = Cursors.Hand,
            ToolTip = t.Done ? "未完了に戻す" : "完了にする",
            Child = t.Done ? new TextBlock { Text = "\uE73E", FontFamily = UI.Icons, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
        };
        UI.AutomationName(box, (t.Title ?? "") + (t.Done ? " を未完了に戻す" : " を完了にする"));
        box.MouseLeftButtonUp += (_, e) => { e.Handled = true; onToggle(); };
        g.Children.Add(box);

        var body = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var title = new TextBlock { Text = t.Title ?? "", TextWrapping = TextWrapping.Wrap, FontWeight = t.Done ? FontWeights.Normal : FontWeights.SemiBold, Foreground = t.Done ? P.Muted : P.Ink };
        if (t.Done) title.TextDecorations = TextDecorations.Strikethrough;
        body.Children.Add(title);
        var parts = new List<string>();
        bool over = false;
        if (showDue) parts.Add(DueText(t, out over));
        else if (!string.IsNullOrEmpty(t.Start)) parts.Add(t.Start + "まで");
        if (t.Remind is int rm && rm >= 0) parts.Add("通知：" + Dates.RemindLabel(rm));
        if (!string.IsNullOrWhiteSpace(t.Memo)) { var m = t.Memo.Trim().Split('\n')[0]; parts.Add(m.Length > 40 ? m[..40] + "…" : m); }
        if (parts.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = string.Join("　", parts), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Foreground = over ? P.Sun : P.Muted, FontWeight = over ? FontWeights.Bold : FontWeights.Normal,
            });
        body.MouseLeftButtonUp += (_, _) => onOpen();
        Grid.SetColumn(body, 1);
        g.Children.Add(body);

        var item = new Border { Child = g, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
        item.MouseEnter += (_, _) => item.Background = P.Soft;
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        return item;
    }

    TextBlock GroupHeader(string text, bool warn = false) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = warn ? P.Sun : P.Muted, Margin = new Thickness(10, 12, 10, 2),
    };

    void RenderTasks()
    {
        taskList.Children.Clear();
        var all = Store.Tasks.ToList();
        var open = all.Where(t => !t.Done).ToList();
        open.Sort(Store.TaskOrder);
        var todayS = Dates.F(today);
        var over = open.Where(t => t.Due != "" && string.CompareOrdinal(t.Due, todayS) < 0).ToList();
        var tod = open.Where(t => t.Due == todayS).ToList();
        var later = open.Where(t => t.Due != "" && string.CompareOrdinal(t.Due, todayS) > 0).ToList();
        var nodue = open.Where(t => t.Due == "").ToList();
        var done = all.Where(t => t.Done).OrderByDescending(t => t.DoneAt ?? 0).Take(30).ToList();

        void Group(string h, List<CalEvent> items, bool warn = false)
        {
            if (items.Count == 0) return;
            taskList.Children.Add(GroupHeader($"{h}（{items.Count}）", warn));
            foreach (var t in items) taskList.Children.Add(TaskItem(t, true));
        }
        Group("期限切れ", over, true); Group("今日", tod); Group("これから", later); Group("期限なし", nodue); Group("完了", done);
        if (taskList.Children.Count == 0)
            taskList.Children.Add(new TextBlock { Text = "タスクはありません。上の欄から追加できます。", Foreground = P.Muted, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 28, 0, 0) });

        int n = over.Count + tod.Count;
        taskBadgeText.Text = n.ToString();
        taskBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        taskBadge.ToolTip = $"期限切れ・今日のタスク {n}件";
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
        for (int w = 0; w < rows; w++)
        {
            var wStart = start.AddDays(w * 7);
            var (segs, laneCount) = Dates.WeekLayout(Store, wStart);
            // 長いバーを何段まで表示するか（残りは「ほか○件」）
            int vis = laneCount <= maxChips ? laneCount : Math.Max(maxChips - 1, 0);
            for (int c = 0; c < 7; c++)
            {
                var d = wStart.AddDays(c);
                int col = c;
                int hiddenBars = segs.Count(x => x.C0 <= col && col <= x.C1 && x.Lane >= vis);
                var cell = DayCell(d, maxChips, vis, hiddenBars, c == 6, w == rows - 1);
                Grid.SetRow(cell, w); Grid.SetColumn(cell, c);
                calGrid.Children.Add(cell);
            }
            // 連日の予定を1本のバーで描く
            foreach (var x in segs.Where(sg => sg.Lane < vis))
            {
                var bar = SpanBar(x);
                Grid.SetRow(bar, w); Grid.SetColumn(bar, x.C0); Grid.SetColumnSpan(bar, x.C1 - x.C0 + 1);
                Panel.SetZIndex(bar, 5);
                calGrid.Children.Add(bar);
            }
        }
        RenderSide();
    }

    const int LaneH = 20, BarTop = 33;

    FrameworkElement SpanBar(SpanSeg x)
    {
        var e = x.E;
        var tb = new TextBlock { FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (!x.Head) tb.Inlines.Add(new Run("◀ ") { FontSize = 8 });
        if (e.IsTask) tb.Inlines.Add(new Run(e.Done ? "\uE73A " : "\uE739 ") { FontFamily = UI.Icons, FontSize = 10 });
        else if (!e.AllDay && x.Head && !string.IsNullOrEmpty(e.Start)) tb.Inlines.Add(new Run(e.Start + " ") { FontWeight = FontWeights.Bold });
        tb.Inlines.Add(new Run(e.Title ?? ""));
        double l = x.Head ? 5 : 0, r = x.Tail ? 5 : 0;
        var bar = new Border
        {
            Child = tb, Background = Palette.B(Palette.C(e.Color)), Height = 18, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3, BarTop + x.Lane * LaneH, 4, 0), Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(l, r, r, l), Cursor = Cursors.Hand,
            ToolTip = $"{e.Title}（{x.St.Month}/{x.St.Day}〜{x.En.Month}/{x.En.Day}）",
        };
        double baseOpacity = e.IsTask && e.Done ? 0.5 : 1;
        if (e.IsTask && e.Done) tb.TextDecorations = TextDecorations.Strikethrough;
        bar.Opacity = baseOpacity;
        bar.MouseEnter += (_, _) => bar.Opacity = baseOpacity * 0.88;
        bar.MouseLeave += (_, _) => bar.Opacity = baseOpacity;
        bar.MouseLeftButtonUp += (_, _) => OpenEditor(e.Id, x.St, x.St);
        return bar;
    }

    FrameworkElement DayCell(DateOnly d, int maxChips, int vis, int hiddenBars, bool lastCol, bool lastRow)
    {
        bool other = d.Month != vm;
        string hol = Dates.HolidayName(d);
        var evs = Dates.EventsOn(Store, d);
        var singles = evs.Where(x => Dates.Span(x.e) == 0).ToList();
        var dayTasks = Store.TasksOn(d);
        int total = singles.Count + dayTasks.Count;

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

        if (vis > 0) stack.Children.Add(new Border { Height = vis * LaneH - 1 });
        int room = maxChips - vis;
        int show = total + hiddenBars > room ? Math.Max(room - 1, 0) : room;
        foreach (var (e, st) in singles.Take(show))
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
        foreach (var t in dayTasks.Take(Math.Max(show - singles.Count, 0)))
        {
            var col = Palette.B(Palette.C(t.Color));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 1) };
            row.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(3), BorderBrush = col, BorderThickness = new Thickness(1.5),
                Background = t.Done ? col : Brushes.Transparent, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            var tt = new TextBlock { Text = t.Title ?? "", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = t.Done ? P.Muted : P.Ink };
            if (t.Done) tt.TextDecorations = TextDecorations.Strikethrough;
            row.Children.Add(tt);
            stack.Children.Add(row);
        }
        int hidden = total - Math.Min(show, total) + hiddenBars;
        if (hidden > 0)
            stack.Children.Add(new TextBlock { Text = $"ほか{hidden}件", FontSize = 11, Foreground = P.Muted, Margin = new Thickness(4, 0, 0, 0) });
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
        var ts = Store.TasksActiveOn(d);
        foreach (var (e, st) in evs)
            list.Children.Add(EventItem(e, st, Dates.TimeLabel(e, st, d), () => OpenEditor(e.Id, d, st)));
        if (ts.Count > 0)
        {
            list.Children.Add(GroupHeader("タスク"));
            foreach (var t in ts) list.Children.Add(TaskItem(t, t.IsSpanTask));
        }
        if (evs.Count == 0 && ts.Count == 0)
            list.Children.Add(new TextBlock { Text = "予定はありません", Foreground = P.Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 0) });
        RenderTasks();
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
        var info = (rep != "" ? "↻ " + rep + "　" : "") + (e.Remind is int rm && rm >= 0 ? "通知：" + Dates.RemindLabel(rm) : "");
        if (info != "") body.Children.Add(new TextBlock { Text = info.Trim(), FontSize = 11.5, Foreground = P.Muted });
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
            case Key.N: AddFromSide(); e.Handled = true; break;
            case Key.T: GoToday(); e.Handled = true; break;
        }
    }

    public void OpenEditor(string? id, DateOnly date, DateOnly? occ, string kind = "event", bool noDue = false)
    {
        CalEvent? existing = id != null && Store.Events.TryGetValue(id, out var ex) ? ex : null;
        var dlg = new EventDialog(this, P, existing, date, occ, kind, noDue);
        dlg.ShowDialog();
        if (dlg.Result == null) return;
        Store.Events[dlg.Result.Id] = dlg.Result;
        if (!dlg.Result.Deleted && Dates.TryParse(dlg.Result.IsTask ? dlg.Result.Due : dlg.Result.Date, out var d) && dlg.JumpToDate) JumpTo(d); else Render();
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
