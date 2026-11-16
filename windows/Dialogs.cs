using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Shapes = System.Windows.Shapes;

namespace MyCalendar;

// ======================= 予定の追加・編集 =======================
public class EventDialog : BaseDialog
{
    public CalEvent? Result;
    public bool JumpToDate;

    readonly CalEvent? existing;
    readonly DateOnly occ;
    readonly TextBox title, memo;
    readonly CheckBox allDay;
    readonly DatePicker dStart, dEnd, dUntil;
    readonly ComboBox tStart, tEnd, repeat, remind;
    readonly TextBox remindCustom;
    readonly StackPanel remindCustomField;
    readonly TextBlock remindNote;
    readonly Grid timeGrid1, timeGrid2;
    readonly StackPanel untilField;
    readonly WrapPanel swatches = new();
    string color;

    static readonly string[] Times = Enumerable.Range(0, 96).Select(i => $"{i / 4:00}:{i % 4 * 15:00}").ToArray();
    static readonly (int? value, string label)[] Reminds =
    {
        (null, "なし"), (0, "開始時刻ちょうど"), (5, "5分前"), (10, "10分前"), (15, "15分前"), (30, "30分前"),
        (60, "1時間前"), (120, "2時間前"), (1440, "1日前"), (2880, "2日前"), (10080, "1週間前"), (-1, "時間を指定…"),
    };
    static readonly (string value, string label)[] Repeats =
        { ("none", "なし"), ("daily", "毎日"), ("weekly", "毎週"), ("monthly", "毎月（同じ日）"), ("yearly", "毎年") };

    public EventDialog(Window owner, Palette p, CalEvent? existing, DateOnly date, DateOnly? occStart)
        : base(owner, p, existing == null ? "予定を追加" : "予定を編集", 520)
    {
        this.existing = existing;
        occ = occStart ?? date;
        var e = existing;
        color = e?.Color ?? UI.Colors[0];

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

        title = UI.Styled(P, new TextBox { Text = e?.Title ?? "", MaxLength = 200 });
        title.FontSize = 17; title.FontWeight = FontWeights.SemiBold;
        root.Children.Add(UI.Field(P, "タイトル", title));

        allDay = new CheckBox { Content = "終日", IsChecked = e?.AllDay ?? false, Foreground = P.Ink, Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Center };
        root.Children.Add(allDay);

        // 開始・終了
        var d0 = e != null ? Dates.P(e.Date, date) : date;
        var d1 = e != null ? Dates.P(e.EndDate, d0) : date;
        string defStart = "10:00";
        if (e == null && date == Dates.Today) defStart = $"{Math.Min(DateTime.Now.Hour + 1, 23):00}:00";
        string defEnd = $"{Math.Min(int.Parse(defStart[..2]) + 1, 23):00}:{defStart[3..]}";

        dStart = Picker(d0); dEnd = Picker(d1);
        tStart = TimeBox(e != null ? e.Start ?? "" : defStart);
        tEnd = TimeBox(e != null ? e.End ?? "" : defEnd);
        timeGrid1 = Row(UI.Field(P, "開始日", dStart), UI.Field(P, "開始時刻", tStart));
        timeGrid2 = Row(UI.Field(P, "終了日", dEnd), UI.Field(P, "終了時刻", tEnd));
        root.Children.Add(timeGrid1);
        root.Children.Add(timeGrid2);

        // 繰り返し
        repeat = UI.Styled(P, new ComboBox());
        foreach (var (v, l) in Repeats) repeat.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        repeat.SelectedIndex = Math.Max(0, Array.FindIndex(Repeats, r => r.value == (e?.Repeat ?? "none")));
        dUntil = Picker(Dates.TryParse(e?.Until, out var u) ? u : (DateOnly?)null);
        untilField = UI.Field(P, "繰り返しの終了日（任意）", dUntil);
        root.Children.Add(Row(UI.Field(P, "繰り返し", repeat), untilField));

        // 通知
        remind = UI.Styled(P, new ComboBox());
        foreach (var (v, l) in Reminds) remind.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        remindCustom = UI.Styled(P, new TextBox { MaxLength = 5 });
        remindCustomField = UI.Field(P, "何分前に通知するか", remindCustom);
        int idx = 0;
        if (e?.Remind is int rm && rm >= 0)
        {
            idx = Array.FindIndex(Reminds, r => r.value == rm);
            if (idx < 0) { idx = Reminds.Length - 1; remindCustom.Text = rm.ToString(); }
        }
        remind.SelectedIndex = idx;
        root.Children.Add(Row(UI.Field(P, "通知", remind, 4), remindCustomField));
        remindNote = new TextBlock
        {
            Text = "終日の予定は、その日の 9:00 を基準に通知します。", Foreground = P.Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(remindNote);

        // 色
        BuildSwatches();
        root.Children.Add(UI.Field(P, "色", swatches));

        memo = UI.Styled(P, new TextBox
        {
            Text = e?.Memo ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 84,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 5000,
        });
        root.Children.Add(UI.Field(P, "メモ", memo, 16));

        // ボタン
        var buttons = new DockPanel { LastChildFill = false };
        if (e != null)
        {
            bool rep = !string.IsNullOrEmpty(e.Repeat) && e.Repeat != "none";
            var del = UI.Btn(P, rep ? "すべて削除" : "削除", DeleteAll, UI.Kind.Danger);
            DockPanel.SetDock(del, Dock.Left); buttons.Children.Add(del);
            if (rep)
            {
                var one = UI.Btn(P, "この日だけ削除", DeleteOne, UI.Kind.Danger);
                one.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(one, Dock.Left); buttons.Children.Add(one);
            }
        }
        var save = UI.Btn(P, "保存", Save, UI.Kind.Primary);
        save.IsDefault = true; save.Margin = new Thickness(8, 0, 0, 0);
        var cancel = UI.Btn(P, "キャンセル", Close);
        DockPanel.SetDock(save, Dock.Right); DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;

        allDay.Checked += (_, _) => SyncVisibility();
        allDay.Unchecked += (_, _) => SyncVisibility();
        repeat.SelectionChanged += (_, _) => SyncVisibility();
        remind.SelectionChanged += (_, _) => { SyncVisibility(); if (Reminds[Math.Max(remind.SelectedIndex, 0)].value == -1) remindCustom.Focus(); };
        dStart.SelectedDateChanged += (_, _) =>
        {
            if (dStart.SelectedDate is DateTime s && (dEnd.SelectedDate == null || dEnd.SelectedDate < s)) dEnd.SelectedDate = s;
        };
        tStart.LostFocus += (_, _) => AdjustEnd();
        tStart.SelectionChanged += (_, _) => Dispatcher.InvokeAsync(AdjustEnd);
        SyncVisibility();
        Loaded += (_, _) => { if (existing == null) { title.Focus(); } };
    }

    DatePicker Picker(DateOnly? d)
    {
        var p = UI.Styled(P, new DatePicker { SelectedDateFormat = DatePickerFormat.Short });
        if (d.HasValue) p.SelectedDate = d.Value.ToDateTime(TimeOnly.MinValue);
        return p;
    }

    ComboBox TimeBox(string value)
    {
        var c = UI.Styled(P, new ComboBox { IsEditable = true, ItemsSource = Times, Text = value, MaxDropDownHeight = 260 });
        c.Loaded += (_, _) => c.Text = value;
        return c;
    }

    static Grid Row(UIElement a, UIElement b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(b, 2);
        g.Children.Add(a); g.Children.Add(b);
        return g;
    }

    void BuildSwatches()
    {
        swatches.Children.Clear();
        foreach (var hex in UI.Colors)
        {
            var c = Palette.C(hex);
            var dot = new Grid { Width = 32, Height = 32, Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
            if (string.Equals(hex, color, StringComparison.OrdinalIgnoreCase))
                dot.Children.Add(new Shapes.Ellipse { Stroke = Palette.B(c), StrokeThickness = 2 });
            dot.Children.Add(new Shapes.Ellipse { Fill = Palette.B(c), Margin = new Thickness(4) });
            var h = hex;
            dot.MouseLeftButtonUp += (_, _) => { color = h; BuildSwatches(); };
            UI.AutomationName(dot, "色 " + hex);
            swatches.Children.Add(dot);
        }
    }

    void SyncVisibility()
    {
        bool ad = allDay.IsChecked == true;
        // 終日のときは時刻の欄を隠す
        ((FrameworkElement)timeGrid1.Children[1]).Visibility = ad ? Visibility.Hidden : Visibility.Visible;
        ((FrameworkElement)timeGrid2.Children[1]).Visibility = ad ? Visibility.Hidden : Visibility.Visible;
        untilField.Visibility = SelectedRepeat() == "none" ? Visibility.Hidden : Visibility.Visible;
        var rv = Reminds[Math.Max(remind.SelectedIndex, 0)].value;
        remindCustomField.Visibility = rv == -1 ? Visibility.Visible : Visibility.Hidden;
        remindNote.Visibility = ad && rv != null ? Visibility.Visible : Visibility.Collapsed;
    }

    string SelectedRepeat() => (repeat.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";

    static string? NormTime(string? s)
    {
        s = (s ?? "").Trim().Replace("：", ":");
        if (s == "") return "";
        var m = Regex.Match(s, @"^(\d{1,2}):?(\d{2})$");
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
        if (h > 23 || mi > 59) return null;
        return $"{h:00}:{mi:00}";
    }

    void AdjustEnd()
    {
        var s = NormTime(tStart.Text);
        if (string.IsNullOrEmpty(s)) return;
        var e = NormTime(tEnd.Text);
        bool sameDay = dStart.SelectedDate == dEnd.SelectedDate;
        if (string.IsNullOrEmpty(e) || (sameDay && string.CompareOrdinal(e, s) <= 0))
        {
            int h = Math.Min(int.Parse(s[..2]) + 1, 23);
            tEnd.Text = $"{h:00}:{s[3..]}";
        }
    }

    void Warn(string m) => MessageBox.Show(this, m, Title, MessageBoxButton.OK, MessageBoxImage.Information);

    void Save()
    {
        var t = title.Text.Trim();
        if (t == "") { Warn("タイトルを入れてください"); title.Focus(); return; }
        if (dStart.SelectedDate is not DateTime ds) { Warn("開始日を選んでください"); return; }
        var start = DateOnly.FromDateTime(ds);
        var end = dEnd.SelectedDate is DateTime de ? DateOnly.FromDateTime(de) : start;
        if (end < start) end = start;
        bool ad = allDay.IsChecked == true;
        string? ts = ad ? "" : NormTime(tStart.Text), te = ad ? "" : NormTime(tEnd.Text);
        if (ts == null || te == null) { Warn("時刻は 9:30 や 14:00 のように入れてください"); return; }
        if (!ad && start == end && ts != "" && te != "" && string.CompareOrdinal(te, ts) < 0) { Warn("終了時刻が開始時刻より前になっています"); return; }
        var rep = SelectedRepeat();
        int? rem = Reminds[Math.Max(remind.SelectedIndex, 0)].value;
        if (rem == -1)
        {
            if (!int.TryParse(remindCustom.Text.Trim(), out var n) || n < 0 || n > 43200) { Warn("通知は 0〜43200 分前の間で、数字で入れてください"); remindCustom.Focus(); return; }
            rem = n;
        }
        var until = rep != "none" && dUntil.SelectedDate is DateTime du ? Dates.F(DateOnly.FromDateTime(du)) : "";

        var ev = existing?.Clone() ?? new CalEvent { Id = NewId(), Exdates = new List<string>() };
        ev.Title = t; ev.Date = Dates.F(start); ev.EndDate = Dates.F(end); ev.AllDay = ad;
        ev.Start = ts; ev.End = te; ev.Repeat = rep; ev.Until = until; ev.Color = color; ev.Memo = memo.Text; ev.Remind = rem;
        ev.Deleted = false; ev.UpdatedAt = Store.Now();
        Result = ev; JumpToDate = true;
        Close();
    }

    void DeleteAll()
    {
        if (existing == null) return;
        if (MessageBox.Show(this, $"「{existing.Title}」を削除しますか？" + (string.IsNullOrEmpty(existing.Repeat) || existing.Repeat == "none" ? "" : "\n繰り返しの予定はすべて消えます。"),
            "削除の確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Result = new CalEvent { Id = existing.Id, Deleted = true, UpdatedAt = Store.Now() };
        Close();
    }

    void DeleteOne()
    {
        if (existing == null) return;
        var ev = existing.Clone();
        ev.Exdates ??= new List<string>();
        ev.Exdates.Add(Dates.F(occ));
        ev.UpdatedAt = Store.Now();
        Result = ev;
        Close();
    }

    static string NewId() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x") + Guid.NewGuid().ToString("N")[..6];
}

// ======================= 検索 =======================
public class SearchDialog : BaseDialog
{
    readonly MainWindow main;
    readonly TextBox q;
    readonly StackPanel results = new();

    public SearchDialog(MainWindow main) : base(main, main.P, "予定を検索", 560)
    {
        this.main = main;
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        q = UI.Styled(P, new TextBox());
        root.Children.Add(UI.Field(P, "タイトル・メモに含まれる言葉", q));
        root.Children.Add(new ScrollViewer { Content = results, Height = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var close = UI.Btn(P, "閉じる", Close);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(close);
        Content = root;
        q.TextChanged += (_, _) => Search();
        Loaded += (_, _) => q.Focus();
        Search();
    }

    void Search()
    {
        results.Children.Clear();
        var text = q.Text.Trim();
        if (text == "") { results.Children.Add(Note("言葉を入れると、タイトルとメモから探します")); return; }
        var hits = main.Store.Live
            .Where(e => ((e.Title ?? "") + " " + (e.Memo ?? "")).Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(e => e.Date, StringComparer.Ordinal).Take(100).ToList();
        if (hits.Count == 0) { results.Children.Add(Note("見つかりませんでした")); return; }
        foreach (var e in hits)
        {
            var d = Dates.P(e.Date, Dates.Today);
            results.Children.Add(main.EventItem(e, d, $"{d.Year}/{d.Month}/{d.Day}({Dates.WD[(int)d.DayOfWeek]})",
                () => { main.JumpTo(d); Close(); }, 120));
        }
    }

    TextBlock Note(string s) => new() { Text = s, Foreground = P.Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
}

// ======================= 設定と同期 =======================
public class SettingsDialog : BaseDialog
{
    readonly MainWindow main;
    readonly TextBlock status;
    readonly PasswordBox token;

    public SettingsDialog(MainWindow main) : base(main, main.P, "設定と同期", 520)
    {
        this.main = main;
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

        status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        root.Children.Add(new Border { Background = P.Soft, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 14), Child = status });

        token = UI.Styled(P, new PasswordBox());
        bool has = main.Store.GetToken() != "";
        root.Children.Add(UI.Field(P, has ? "GitHubトークン（保存済み。変えるときだけ入力）" : "GitHubトークン（gist 権限のみ）", token, 6));
        root.Children.Add(new TextBlock
        {
            Text = "スマホと同じトークンを入れると、ネットにつながった時に自動で同期します。ネットが無い間はこのPCに保存され、つながった時にまとめて送られます。トークンはWindowsの機能で暗号化して、このPCだけに保存します。",
            TextWrapping = TextWrapping.Wrap, Foreground = P.Muted, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10),
        });
        var syncRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var unlink = UI.Btn(P, "同期をやめる", Unlink, UI.Kind.Danger);
        DockPanel.SetDock(unlink, Dock.Left);
        var link = UI.Btn(P, "保存して同期", async () => await Link(), UI.Kind.Primary);
        DockPanel.SetDock(link, Dock.Right);
        syncRow.Children.Add(unlink); syncRow.Children.Add(link);
        root.Children.Add(syncRow);
        root.Children.Add(Sep());

        // 表示
        var week = UI.Styled(P, new ComboBox());
        week.Items.Add("日曜日"); week.Items.Add("月曜日");
        week.SelectedIndex = main.Store.Settings.WeekStart == 1 ? 1 : 0;
        week.SelectionChanged += (_, _) => { main.Store.Settings.WeekStart = week.SelectedIndex; main.TrySave(); main.Render(); };

        var themes = new[] { ("light", "ライト"), ("dark", "ダーク"), ("system", "Windowsの設定に合わせる") };
        var theme = UI.Styled(P, new ComboBox());
        foreach (var (v, l) in themes) theme.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        theme.SelectedIndex = Math.Max(0, Array.FindIndex(themes, t => t.Item1 == main.Store.Settings.Theme));
        theme.SelectionChanged += (_, _) =>
        {
            main.Store.Settings.Theme = (theme.SelectedItem as ComboBoxItem)?.Tag as string ?? "light";
            main.TrySave();
            main.ApplyTheme();
            Close();
            main.Dispatcher.InvokeAsync(() => new SettingsDialog(main).ShowDialog());
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var f1 = UI.Field(P, "週のはじまり", week); var f2 = UI.Field(P, "表示テーマ", theme);
        Grid.SetColumn(f2, 2); g.Children.Add(f1); g.Children.Add(f2);
        root.Children.Add(g);

        // 通知とタスクトレイ
        var toTray = new CheckBox { Content = "×で閉じてもタスクトレイで動かす（通知を受け取るため）", IsChecked = main.Store.Settings.CloseToTray, Foreground = P.Ink, Margin = new Thickness(0, 4, 0, 8) };
        toTray.Click += (_, _) => { main.Store.Settings.CloseToTray = toTray.IsChecked == true; main.TrySave(); };
        var auto = new CheckBox { Content = "Windowsの起動時に自動で開く（タスクトレイに入った状態で）", IsChecked = Tray.AutoStart, Foreground = P.Ink, Margin = new Thickness(0, 0, 0, 8) };
        auto.Click += (_, _) => Tray.AutoStart = auto.IsChecked == true;
        root.Children.Add(UI.Label(P, "通知"));
        root.Children.Add(toTray);
        root.Children.Add(auto);
        var test = UI.Btn(P, "通知のテスト", () => main.Tray.Notify("🔔 テスト", "予定の通知はこのように届きます。", main.ShowFromTray));
        test.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(test);
        root.Children.Add(Sep());

        // バックアップ
        root.Children.Add(UI.Label(P, "バックアップ"));
        var bk = new StackPanel { Orientation = Orientation.Horizontal };
        bk.Children.Add(UI.Btn(P, "ファイルに書き出す", Export));
        var imp = UI.Btn(P, "ファイルから読み込む", Import); imp.Margin = new Thickness(8, 0, 0, 0);
        bk.Children.Add(imp);
        root.Children.Add(bk);
        root.Children.Add(new TextBlock { Text = "予定の保存場所：" + Storage.Dir, Foreground = P.Muted, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Sep());

        var close = UI.Btn(P, "閉じる", Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(close);
        Content = root;
        Paint();
    }

    Border Sep() => new() { Height = 1, Background = P.Line, Margin = new Thickness(0, 14, 0, 14) };

    void Paint() => status.Text = $"予定 {main.Store.Live.Count()}件（このPCに保存）\n" + main.SyncSummary();

    async Task Link()
    {
        var v = token.Password.Trim();
        if (v != "") { main.Store.SetToken(v); main.Store.Sync.GistId = ""; token.Clear(); }
        if (main.Store.GetToken() == "") { MessageBox.Show(this, "トークンを入れてください", Title); return; }
        main.Store.Sync.Dirty = true;
        main.TrySave();
        status.Text = "同期しています…";
        await main.DoSync(true);
        Paint();
    }

    void Unlink()
    {
        if (main.Store.GetToken() == "") return;
        if (MessageBox.Show(this, "同期をやめますか？\n予定はこのPCに残ります。", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        main.Store.SetToken(""); main.Store.Sync.GistId = ""; main.Store.Sync.Last = 0; main.Store.Sync.Dirty = false;
        main.SyncStateNow = SyncState.Local;
        main.TrySave(); main.PaintSync(); Paint();
    }

    void Export()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"calendar-backup-{Dates.F(Dates.Today)}.json",
            Filter = "バックアップ (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, Storage.SerializeEvents(main.Store.Events, "exportedAt"), new System.Text.UTF8Encoding(false));
            MessageBox.Show(this, "書き出しました。", Title);
        }
        catch (Exception ex) { MessageBox.Show(this, "書き出せませんでした。\n" + ex.Message, Title); }
    }

    void Import()
    {
        var dlg = new OpenFileDialog { Filter = "バックアップ (*.json)|*.json|すべてのファイル|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var ev = Storage.ParseEvents(File.ReadAllText(dlg.FileName));
            if (ev.Count == 0) { MessageBox.Show(this, "予定が見つかりませんでした。書き出したファイルを選んでください。", Title); return; }
            int n = Store.Merge(main.Store.Events, ev);
            main.Changed(); main.Render(); Paint();
            MessageBox.Show(this, $"{n}件を読み込みました。", Title);
        }
        catch (Exception ex) { MessageBox.Show(this, "読み込めませんでした。\n" + ex.Message, Title); }
    }
}
