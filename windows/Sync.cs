using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Nodes;

namespace MyCalendar;

public enum SyncState { Local, Ok, Pending, Error }

public class SyncException : Exception { public SyncException(string m) : base(m) { } }

/// <summary>GitHub Gist を使った同期。スマホ版と同じファイル名・形式。</summary>
public static class GistSync
{
    public const string FileName = "mycal-data.json";
    static readonly HttpClient http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MyCalendar-Windows/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    static async Task<JsonNode?> Gh(string token, HttpMethod method, string path, JsonNode? body = null)
    {
        using var req = new HttpRequestMessage(method, "https://api.github.com" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req);
        int code = (int)res.StatusCode;
        if (code == 401) throw new SyncException("トークンが正しくないか、期限切れです");
        if (code == 403) throw new SyncException("トークンに gist の権限がないか、GitHubの回数制限にかかりました");
        if (!res.IsSuccessStatusCode) throw new SyncException($"GitHubとの通信に失敗しました（{code}）");
        var text = await res.Content.ReadAsStringAsync();
        return JsonNode.Parse(text);
    }

    static async Task<string> FindGist(string token)
    {
        for (int page = 1; page <= 5; page++)
        {
            var list = await Gh(token, HttpMethod.Get, $"/gists?per_page=100&page={page}") as JsonArray;
            if (list == null) break;
            foreach (var g in list)
            {
                if (g?["files"]?[FileName] != null) return g["id"]?.GetValue<string>() ?? "";
            }
            if (list.Count < 100) break;
        }
        return "";
    }

    /// <summary>同期する。戻り値: (状態, メッセージ, 他の端末から予定が届いたか)</summary>
    public static async Task<(SyncState state, string message, bool changed)> Run(Store store)
    {
        var token = store.GetToken();
        if (string.IsNullOrEmpty(token)) return (SyncState.Local, "", false);
        if (!NetworkInterface.GetIsNetworkAvailable()) return (SyncState.Pending, "オフライン", false);
        try
        {
            if (string.IsNullOrEmpty(store.Sync.GistId)) store.Sync.GistId = await FindGist(token);

            var remote = new Dictionary<string, CalEvent>();
            if (!string.IsNullOrEmpty(store.Sync.GistId))
            {
                var g = await Gh(token, HttpMethod.Get, "/gists/" + store.Sync.GistId);
                var f = g?["files"]?[FileName];
                if (f != null)
                {
                    string content = f["content"]?.GetValue<string>() ?? "";
                    bool truncated = f["truncated"]?.GetValue<bool>() ?? false;
                    var raw = f["raw_url"]?.GetValue<string>();
                    if (truncated && raw != null) content = await http.GetStringAsync(raw);
                    remote = Storage.ParseEvents(content);
                }
            }

            int got = Store.Merge(store.Events, remote);
            bool needPush = Store.HasNewer(store.Events, remote);
            string body = Storage.SerializeEvents(store.Events, "updatedAt");

            if (string.IsNullOrEmpty(store.Sync.GistId))
            {
                var created = await Gh(token, HttpMethod.Post, "/gists", new JsonObject
                {
                    ["description"] = "マイカレンダーの同期データ（消さないでください）",
                    ["public"] = false,
                    ["files"] = new JsonObject { [FileName] = new JsonObject { ["content"] = body } },
                });
                store.Sync.GistId = created?["id"]?.GetValue<string>() ?? "";
            }
            else if (needPush)
            {
                await Gh(token, HttpMethod.Patch, "/gists/" + store.Sync.GistId, new JsonObject
                {
                    ["files"] = new JsonObject { [FileName] = new JsonObject { ["content"] = body } },
                });
            }

            store.Sync.Dirty = false;
            store.Sync.Last = Store.Now();
            return (SyncState.Ok, "", got > 0);
        }
        catch (SyncException ex) { return (SyncState.Error, ex.Message, false); }
        catch (HttpRequestException) { return (SyncState.Pending, "ネットにつながっていません", false); }
        catch (TaskCanceledException) { return (SyncState.Pending, "通信がタイムアウトしました", false); }
        catch (Exception ex) { return (SyncState.Error, "同期できませんでした: " + ex.Message, false); }
    }
}
