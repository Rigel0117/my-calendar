/**
 * マイカレンダー → Discord 通知
 *
 * 1分おきに同期データ（GitHub Gist）を読み、予定ごとに決めた「何分前」になったら
 * Discord のチャンネルに通知を送ります。PCやスマホの電源が切れていても届きます。
 * タスクにも対応しています（完了にしたタスクは通知しません）。
 *
 * 使い方は notify/Discord通知の手順.md を見てください。
 * 下の2つ（GITHUB_TOKEN と DISCORD_WEBHOOK_URL）だけ、自分のものに書き換えます。
 */

// ▼ ここを書き換える ▼
const GITHUB_TOKEN = 'ここにGitHubのトークン（ghp_で始まる文字列）';
const DISCORD_WEBHOOK_URL = 'ここにDiscordのWebhook URL';
// ▲ ここまで ▲

const FILE_NAME = 'mycal-data.json';
const ALLDAY_BASE = '09:00';     // 終日の予定は、その日の9:00を基準に通知する（アプリと同じ）
const LOOKBACK_MIN = 15;         // 通知時刻から15分以内なら送る（少し遅れても取りこぼさない）
const JST = 9 * 60 * 60 * 1000;  // 日本時間
const WD = ['日', '月', '火', '水', '木', '金', '土'];

/** 最初に1回だけ実行：1分おきに動くように設定し、テスト通知を送る */
function setup() {
  ScriptApp.getProjectTriggers()
    .filter(t => t.getHandlerFunction() === 'checkReminders')
    .forEach(t => ScriptApp.deleteTrigger(t));
  ScriptApp.newTrigger('checkReminders').timeBased().everyMinutes(1).create();
  const events = loadEvents();
  const n = Object.values(events).filter(e => e && !e.deleted).length;
  postDiscord({
    content: '✅ マイカレンダーの通知を設定しました（予定 ' + n + ' 件を確認できました）。これから予定の通知がここに届きます。',
  });
}

/** 1分おきに自動で動く */
function checkReminders() {
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(5000)) return;
  try {
    const events = loadEvents();
    const props = PropertiesService.getScriptProperties();
    const sent = JSON.parse(props.getProperty('sent') || '{}');
    const now = Date.now();
    const today = todayJST();

    for (const e of Object.values(events)) {
      if (!e || e.deleted || !e.date || !hasRemind(e)) continue;
      if (e.kind === 'task' && e.done) continue; // 完了したタスクは通知しない
      const m = Number(e.remind);
      const ahead = Math.floor(m / 1440) + 2;
      for (let k = -1; k <= ahead; k++) {
        const d = addDays(today, k);
        if (!isStart(e, d)) continue;
        const base = !e.allDay && /^\d{2}:\d{2}$/.test(e.start || '') ? e.start : ALLDAY_BASE;
        const fire = jstMs(d, base) - m * 60000;
        if (fire > now || now - fire >= LOOKBACK_MIN * 60000) continue;
        const key = [e.id, d, m, e.start || ''].join('|');
        if (sent[key]) continue;
        try {
          postDiscord(message(e, d, m));
          sent[key] = fire;
        } catch (err) {
          console.warn('Discordに送れませんでした（次の回にもう一度送ります）: ' + err);
        }
      }
    }

    // 3日より前の記録は消す
    const cutoff = now - 3 * 24 * 60 * 60 * 1000;
    Object.keys(sent).forEach(k => { if (sent[k] < cutoff) delete sent[k]; });
    props.setProperty('sent', JSON.stringify(sent));
  } finally {
    lock.releaseLock();
  }
}

/** 試しに通知を1件送る（手順の確認用） */
function sendTest() {
  postDiscord({ content: '🔔 テスト通知です。予定の通知はこのように届きます。' });
}

// ---------------- メッセージ ----------------
function message(e, d, m) {
  const md = Number(d.slice(5, 7)) + '/' + Number(d.slice(8, 10)) + '(' + WD[dow(d)] + ')';
  const task = e.kind === 'task';
  let when;
  if (task) when = e.start ? e.start + 'まで' : '期限の日';
  else if (e.allDay) when = '終日';
  else {
    const span = spanOf(e);
    when = span === 0 ? (e.start || '') + (e.end ? '–' + e.end : '') : (e.start || '') + '〜' + fmtMD(addDays(d, span)) + ' ' + (e.end || '');
  }
  const lead = m === 0 ? '今から' : remindLabel(m) + 'のお知らせ';
  const lines = ['**' + md + ' ' + when + '**（' + lead + '）'];
  if (e.memo && String(e.memo).trim()) lines.push(String(e.memo).trim().slice(0, 1500));
  return {
    // スマホの通知に出る文
    content: (task ? '☑️ タスク：' : '🔔 ') + e.title + ' — ' + md + ' ' + when,
    embeds: [{
      title: String(e.title || '').slice(0, 250),
      description: lines.join('\n'),
      color: parseInt(String(e.color || '#3b6fd4').replace('#', ''), 16) || 0x3b6fd4,
    }],
  };
}

function remindLabel(m) {
  if (m === 0) return '開始時刻';
  if (m % 10080 === 0) return (m / 10080) + '週間前';
  if (m % 1440 === 0) return (m / 1440) + '日前';
  if (m % 60 === 0) return (m / 60) + '時間前';
  if (m > 60) return Math.floor(m / 60) + '時間' + (m % 60) + '分前';
  return m + '分前';
}

function postDiscord(payload) {
  const res = UrlFetchApp.fetch(DISCORD_WEBHOOK_URL, {
    method: 'post',
    contentType: 'application/json',
    payload: JSON.stringify(Object.assign({ username: 'マイカレンダー' }, payload)),
    muteHttpExceptions: true,
  });
  const code = res.getResponseCode();
  if (code < 200 || code >= 300) throw new Error('Discord ' + code + ': ' + res.getContentText().slice(0, 200));
}

// ---------------- 同期データ（Gist）を読む ----------------
function gh(path) {
  const res = UrlFetchApp.fetch('https://api.github.com' + path, {
    headers: {
      Authorization: 'Bearer ' + GITHUB_TOKEN,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
    muteHttpExceptions: true,
  });
  return { code: res.getResponseCode(), json: () => JSON.parse(res.getContentText()) };
}

function findGistId() {
  for (let page = 1; page <= 5; page++) {
    const r = gh('/gists?per_page=100&page=' + page);
    if (r.code === 401) throw new Error('GitHubのトークンが正しくないか、期限切れです');
    if (r.code !== 200) throw new Error('GitHubとの通信に失敗しました（' + r.code + '）');
    const list = r.json();
    const g = list.find(x => x.files && x.files[FILE_NAME]);
    if (g) return g.id;
    if (list.length < 100) break;
  }
  throw new Error('同期データが見つかりません。先にアプリで同期を設定してください');
}

function loadEvents() {
  const props = PropertiesService.getScriptProperties();
  let id = props.getProperty('gistId');
  if (!id) { id = findGistId(); props.setProperty('gistId', id); }
  let r = gh('/gists/' + id);
  if (r.code === 404) { id = findGistId(); props.setProperty('gistId', id); r = gh('/gists/' + id); }
  if (r.code !== 200) throw new Error('同期データを読めませんでした（' + r.code + '）');
  const f = r.json().files[FILE_NAME];
  if (!f) return {};
  const text = f.truncated ? UrlFetchApp.fetch(f.raw_url).getContentText() : f.content;
  try { return JSON.parse(text).events || {}; } catch (e) { return {}; }
}

// ---------------- 日付の計算（日本時間・アプリと同じ繰り返しルール） ----------------
const pad = n => String(n).padStart(2, '0');
function todayJST() { const t = new Date(Date.now() + JST); return t.getUTCFullYear() + '-' + pad(t.getUTCMonth() + 1) + '-' + pad(t.getUTCDate()); }
function utc(s) { const [y, m, d] = s.split('-').map(Number); return Date.UTC(y, m - 1, d); }
function addDays(s, n) { const t = new Date(utc(s) + n * 86400000); return t.getUTCFullYear() + '-' + pad(t.getUTCMonth() + 1) + '-' + pad(t.getUTCDate()); }
function diffDays(a, b) { return Math.round((utc(b) - utc(a)) / 86400000); }
function dow(s) { return new Date(utc(s)).getUTCDay(); }
function jstMs(s, hm) { const [h, mi] = hm.split(':').map(Number); return utc(s) + (h * 60 + mi) * 60000 - JST; }
function fmtMD(s) { return Number(s.slice(5, 7)) + '/' + Number(s.slice(8, 10)); }
function spanOf(e) { return Math.min(Math.max(diffDays(e.date, e.endDate || e.date), 0), 62); }
function hasRemind(e) { return e.remind !== null && e.remind !== undefined && e.remind !== '' && Number(e.remind) >= 0; }

function isStart(e, s) {
  if (s < e.date) return false;
  if (e.until && s > e.until) return false;
  if (e.exdates && e.exdates.indexOf(s) >= 0) return false;
  if (s === e.date) return true;
  switch (e.repeat) {
    case 'daily': return true;
    case 'weekly': return dow(s) === dow(e.date);
    case 'monthly': return s.slice(8, 10) === e.date.slice(8, 10);
    case 'yearly': return s.slice(5, 10) === e.date.slice(5, 10);
    default: return false;
  }
}
