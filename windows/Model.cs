using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyCalendar;

/// <summary>予定1件。スマホ版（index.html）と同じ項目名でGistに保存する。</summary>
public class CalEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("endDate")] public string? EndDate { get; set; }
    [JsonPropertyName("allDay")] public bool AllDay { get; set; }
    [JsonPropertyName("start")] public string? Start { get; set; }
    [JsonPropertyName("end")] public string? End { get; set; }
    [JsonPropertyName("repeat")] public string? Repeat { get; set; }
    [JsonPropertyName("until")] public string? Until { get; set; }
    [JsonPropertyName("exdates")] public List<string>? Exdates { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("memo")] public string? Memo { get; set; }
    /// <summary>何分前に通知するか（null＝通知しない）</summary>
    [JsonPropertyName("remind")] public int? Remind { get; set; }
    [JsonPropertyName("updatedAt")] public double UpdatedAt { get; set; }
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
    /// <summary>"task" ならタスク（それ以外は予定）</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("doneAt")] public double? DoneAt { get; set; }

    [JsonIgnore] public bool IsTask => Kind == "task";

    public CalEvent Clone()
    {
        var c = (CalEvent)MemberwiseClone();
        c.Exdates = Exdates == null ? new List<string>() : new List<string>(Exdates);
        return c;
    }
}

public class AppSettings
{
    public int WeekStart { get; set; } = 0;           // 0=日曜 1=月曜
    public string Theme { get; set; } = "light";      // light / dark / system
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = 1180;
    public double WinHeight { get; set; } = 760;
    public bool Maximized { get; set; }
    public bool CloseToTray { get; set; } = true;     // 閉じてもタスクトレイで動かす
    public bool TrayHintShown { get; set; }
}

public class SyncInfo
{
    public string TokenProtected { get; set; } = "";  // Windowsの機能で暗号化して保存
    public string GistId { get; set; } = "";
    public double Last { get; set; }
    public bool Dirty { get; set; }
}

public class Store
{
    public Dictionary<string, CalEvent> Events { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public SyncInfo Sync { get; set; } = new();
    /// <summary>このPCで通知済みのもの（キー → 通知予定時刻）</summary>
    public Dictionary<string, double> Notified { get; set; } = new();

    /// <summary>予定（タスクは含まない）</summary>
    public IEnumerable<CalEvent> Live => Events.Values.Where(e => !e.Deleted && !e.IsTask && Dates.TryParse(e.Date, out _));
    /// <summary>タスク（期限なしも含む）</summary>
    public IEnumerable<CalEvent> Tasks => Events.Values.Where(e => !e.Deleted && e.IsTask);

    public static int TaskOrder(CalEvent a, CalEvent b)
    {
        int c = a.Done.CompareTo(b.Done);
        if (c == 0) c = string.CompareOrdinal(string.IsNullOrEmpty(a.Date) ? "9999" : a.Date, string.IsNullOrEmpty(b.Date) ? "9999" : b.Date);
        if (c == 0) c = string.CompareOrdinal(string.IsNullOrEmpty(a.Start) ? "99" : a.Start, string.IsNullOrEmpty(b.Start) ? "99" : b.Start);
        if (c == 0) c = string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
        return c;
    }

    public List<CalEvent> TasksOn(DateOnly d)
    {
        var s = Dates.F(d);
        var list = Tasks.Where(t => t.Date == s).ToList();
        list.Sort(TaskOrder);
        return list;
    }

    public string GetToken()
    {
        if (string.IsNullOrEmpty(Sync.TokenProtected)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(Sync.TokenProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    public void SetToken(string token)
    {
        if (string.IsNullOrEmpty(token)) { Sync.TokenProtected = ""; return; }
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        Sync.TokenProtected = Convert.ToBase64String(bytes);
    }

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>新しい方（updatedAtが大きい方）を残して取り込む。取り込んだ件数を返す。</summary>
    public static int Merge(Dictionary<string, CalEvent> target, Dictionary<string, CalEvent>? src)
    {
        if (src == null) return 0;
        int n = 0;
        foreach (var (id, e) in src)
        {
            if (e == null || string.IsNullOrEmpty(e.Id)) continue;
            if (!target.TryGetValue(id, out var cur) || e.UpdatedAt > cur.UpdatedAt) { target[id] = e; n++; }
        }
        return n;
    }

    public static bool HasNewer(Dictionary<string, CalEvent> local, Dictionary<string, CalEvent> remote)
    {
        foreach (var (id, e) in local)
            if (!remote.TryGetValue(id, out var r) || e.UpdatedAt > r.UpdatedAt) return true;
        return false;
    }
}

public static class Storage
{
    public static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyCalendar");
    static readonly string FilePath = System.IO.Path.Combine(Dir, "data.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Store Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath, Encoding.UTF8), Json);
                if (s != null)
                {
                    s.Events ??= new();
                    s.Settings ??= new();
                    s.Sync ??= new();
                    s.Notified ??= new();
                    return s;
                }
            }
        }
        catch
        {
            // 壊れていた場合は退避して新しく始める
            try { File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
        return new Store();
    }

    public static void Save(Store s)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json), new UTF8Encoding(false));
        File.Move(tmp, FilePath, true);
    }

    /// <summary>{"events":{...}} 形式（Gist・書き出しファイル共通）から予定を読む</summary>
    public static Dictionary<string, CalEvent> ParseEvents(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Object)
            {
                var result = new Dictionary<string, CalEvent>();
                foreach (var p in ev.EnumerateObject())
                {
                    try
                    {
                        var e = p.Value.Deserialize<CalEvent>(Json);
                        if (e != null && !string.IsNullOrEmpty(e.Id)) result[p.Name] = e;
                    }
                    catch { /* 1件だけ壊れていても他は読む */ }
                }
                return result;
            }
        }
        catch { }
        return new Dictionary<string, CalEvent>();
    }

    public static string SerializeEvents(Dictionary<string, CalEvent> events, string stampKey)
    {
        var payload = new Dictionary<string, object>
        {
            ["app"] = "mycal",
            ["version"] = 1,
            [stampKey] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["events"] = events,
        };
        return JsonSerializer.Serialize(payload, Json);
    }
}

/// <summary>連日の予定を1週間の中で切り出したもの（バー1本分）</summary>
public record SpanSeg(CalEvent E, DateOnly St, DateOnly En, int C0, int C1, bool Head, bool Tail)
{
    public int Lane { get; set; }
}

/// <summary>日付の計算・祝日・繰り返し</summary>
public static class Dates
{
    public static readonly string[] WD = { "日", "月", "火", "水", "木", "金", "土" };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string F(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    public static bool TryParse(string? s, out DateOnly d)
    {
        d = default;
        return !string.IsNullOrEmpty(s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d);
    }

    public static DateOnly P(string? s, DateOnly fallback) => TryParse(s, out var d) ? d : fallback;

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // ---- 日本の祝日（2020年以降の法律に基づく計算） ----
    static readonly Dictionary<int, Dictionary<DateOnly, string>> cache = new();

    public static string HolidayName(DateOnly d) => Holidays(d.Year).TryGetValue(d, out var n) ? n : "";

    public static Dictionary<DateOnly, string> Holidays(int y)
    {
        if (cache.TryGetValue(y, out var c)) return c;
        var h = new Dictionary<DateOnly, string>();
        DateOnly NthMon(int m, int n)
        {
            var first = new DateOnly(y, m, 1);
            int off = (8 - (int)first.DayOfWeek) % 7;
            return first.AddDays(off + 7 * (n - 1));
        }
        void Fx(int m, int d, string name) => h[new DateOnly(y, m, d)] = name;
        int t = y - 1980, q = (int)Math.Floor(t / 4.0);

        Fx(1, 1, "元日"); h[NthMon(1, 2)] = "成人の日"; Fx(2, 11, "建国記念の日");
        if (y >= 2020) Fx(2, 23, "天皇誕生日");
        Fx(3, (int)Math.Floor(20.8431 + 0.242194 * t - q), "春分の日");
        Fx(4, 29, "昭和の日"); Fx(5, 3, "憲法記念日"); Fx(5, 4, "みどりの日"); Fx(5, 5, "こどもの日");
        h[NthMon(7, 3)] = "海の日"; Fx(8, 11, "山の日"); h[NthMon(9, 3)] = "敬老の日";
        Fx(9, (int)Math.Floor(23.2488 + 0.242194 * t - q), "秋分の日");
        h[NthMon(10, 2)] = "スポーツの日"; Fx(11, 3, "文化の日"); Fx(11, 23, "勤労感謝の日");

        // 国民の休日（祝日に挟まれた平日）
        foreach (var k in h.Keys.ToList())
        {
            var mid = k.AddDays(1); var nx = k.AddDays(2);
            if (h.ContainsKey(nx) && !h.ContainsKey(mid) && mid.DayOfWeek != DayOfWeek.Sunday) h[mid] = "国民の休日";
        }
        // 振替休日
        foreach (var k in h.Keys.OrderBy(x => x).ToList())
        {
            if (k.DayOfWeek == DayOfWeek.Sunday && h[k] != "振替休日")
            {
                var d = k.AddDays(1);
                while (h.ContainsKey(d)) d = d.AddDays(1);
                h[d] = "振替休日";
            }
        }
        cache[y] = h;
        return h;
    }

    // ---- 繰り返し・複数日の予定 ----
    public static bool IsStart(CalEvent e, DateOnly s)
    {
        if (!TryParse(e.Date, out var d0)) return false;
        if (s < d0) return false;
        if (TryParse(e.Until, out var until) && s > until) return false;
        if (e.Exdates != null && e.Exdates.Contains(F(s))) return false;
        if (s == d0) return true;
        return e.Repeat switch
        {
            "daily" => true,
            "weekly" => s.DayOfWeek == d0.DayOfWeek,
            "monthly" => s.Day == d0.Day,
            "yearly" => s.Day == d0.Day && s.Month == d0.Month,
            _ => false,
        };
    }

    public static int Span(CalEvent e)
    {
        var d0 = P(e.Date, Today);
        var d1 = P(e.EndDate, d0);
        return Math.Clamp(d1.DayNumber - d0.DayNumber, 0, 62);
    }

    /// <summary>日付sに表示されるなら、その回の開始日を返す</summary>
    public static DateOnly? OccurrenceStart(CalEvent e, DateOnly s)
    {
        int span = Span(e);
        for (int k = 0; k <= span; k++)
        {
            var st = s.AddDays(-k);
            if (IsStart(e, st)) return st;
        }
        return null;
    }

    public static List<(CalEvent e, DateOnly st)> EventsOn(Store store, DateOnly s)
    {
        string Key((CalEvent e, DateOnly st) x) => x.e.AllDay ? "0" : x.st < s ? "1" : "2" + (x.e.Start ?? "");
        return store.Live
            .Select(e => (e, st: OccurrenceStart(e, s)))
            .Where(x => x.st.HasValue)
            .Select(x => (x.e, st: x.st!.Value))
            .OrderBy(x => Key(x), StringComparer.Ordinal)
            .ThenBy(x => x.e.Title ?? "", StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>1週間分の「連日の予定」を、重ならないように段（レーン）に並べる</summary>
    public static (List<SpanSeg> segs, int lanes) WeekLayout(Store store, DateOnly wStart)
    {
        var wEnd = wStart.AddDays(6);
        var segs = new List<SpanSeg>();
        var seen = new HashSet<string>();
        foreach (var e in store.Live)
        {
            int span = Span(e);
            if (span == 0) continue;
            for (int c = 0; c < 7; c++)
            {
                var st0 = OccurrenceStart(e, wStart.AddDays(c));
                if (st0 == null) continue;
                var st = st0.Value;
                if (!seen.Add(e.Id + "|" + F(st))) continue;
                var en = st.AddDays(span);
                segs.Add(new SpanSeg(e, st, en,
                    Math.Max(0, st.DayNumber - wStart.DayNumber), Math.Min(6, en.DayNumber - wStart.DayNumber),
                    st >= wStart, en <= wEnd));
            }
        }
        var ordered = segs.OrderBy(x => x.St).ThenByDescending(x => x.En).ThenByDescending(x => x.E.AllDay).ToList();
        var lanes = new List<List<(int a, int b)>>();
        foreach (var x in ordered)
        {
            int i = 0;
            while (i < lanes.Count && lanes[i].Any(r => !(x.C1 < r.a || x.C0 > r.b))) i++;
            if (i == lanes.Count) lanes.Add(new List<(int a, int b)>());
            lanes[i].Add((x.C0, x.C1));
            x.Lane = i;
        }
        return (ordered, lanes.Count);
    }

    public static string TimeLabel(CalEvent e, DateOnly st, DateOnly s)
    {
        if (e.AllDay) return "終日";
        int span = Span(e);
        var en = st.AddDays(span);
        if (span == 0) return (e.Start ?? "") + (string.IsNullOrEmpty(e.End) ? "" : "–" + e.End);
        if (s == st) return (e.Start ?? "") + "〜";
        if (s == en) return "〜" + (e.End ?? "");
        return "終日";
    }

    // ---- 通知 ----
    public static string RemindLabel(int m)
    {
        if (m == 0) return "開始時刻";
        if (m % 10080 == 0) return $"{m / 10080}週間前";
        if (m % 1440 == 0) return $"{m / 1440}日前";
        if (m % 60 == 0) return $"{m / 60}時間前";
        if (m > 60) return $"{m / 60}時間{m % 60}分前";
        return $"{m}分前";
    }

    /// <summary>終日の予定はその日の9:00を基準に通知する（Discord通知と同じ）</summary>
    public static readonly TimeOnly AllDayBase = new(9, 0);

    /// <summary>今、通知すべきもの（通知時刻を過ぎてから window 以内）</summary>
    public static IEnumerable<(CalEvent e, DateOnly occ, DateTime fire)> DueReminders(Store store, DateTime now, TimeSpan window)
    {
        var today = DateOnly.FromDateTime(now);
        foreach (var e in store.Live.Concat(store.Tasks.Where(t => !t.Done)).ToList())
        {
            if (e.Remind is not int m || m < 0) continue;
            int ahead = m / 1440 + 2;
            for (int k = -1; k <= ahead; k++)
            {
                var d = today.AddDays(k);
                if (!IsStart(e, d)) continue;
                var t = !e.AllDay && TimeOnly.TryParseExact(e.Start ?? "", "HH:mm", Inv, DateTimeStyles.None, out var tt) ? tt : AllDayBase;
                var fire = d.ToDateTime(t).AddMinutes(-m);
                if (fire <= now && now - fire < window) yield return (e, d, fire);
            }
        }
    }

    public static string ReminderText(CalEvent e, DateOnly occ)
    {
        var s = $"{occ.Month}/{occ.Day}({WD[(int)occ.DayOfWeek]}) " +
            (e.IsTask ? (string.IsNullOrEmpty(e.Start) ? "が期限" : e.Start + "まで") : e.AllDay ? "終日" : TimeLabel(e, occ, occ));
        int m = e.Remind ?? 0;
        s += m == 0 ? "（開始時刻）" : $"（{RemindLabel(m)}のお知らせ）";
        if (!string.IsNullOrWhiteSpace(e.Memo)) s += "\n" + e.Memo.Trim();
        return s;
    }

    public static string RepeatLabel(string? r) => r switch
    {
        "daily" => "毎日", "weekly" => "毎週", "monthly" => "毎月", "yearly" => "毎年", _ => "",
    };
}
