// =============================================
//  BlockingCollection ワーカー完全版（優先度対応・堅牢化）
//  Target: .NET Framework 4.8 / WinForms
//  用途: メール送信 / Webhook チャット / トースト通知
//  特徴:
//   - 単一/優先度キュー両対応（High/Normal/Low）
//   - 指数バックオフ + 最大リトライ
//   - UI スレッド分離（SynchronizationContext.Post）
//   - Dead-letter 出力
//   - Newtonsoft.Json による安全な JSON 変換
//   - HttpResponse/Smtp/NotifyIcon/Context の確実な Dispose
//   - Pipe 終了のアンブロック（ダミークライアント）
// =============================================
// 参照設定:
//   - System
//   - System.Core
//   - System.Net.Http
//   - System.Windows.Forms
//   - System.Drawing
//   - System.Web (※不要)
//   - Newtonsoft.Json（NuGet: Install-Package Newtonsoft.Json）
// =============================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace TrayWorkerSample
{
    // --------------------------------
    // 優先度
    // --------------------------------
    public enum WorkPriority { Low = 0, Normal = 1, High = 2 }

    // --------------------------------
    // メッセージ（UI→ワーカーへ渡す汎用フォーマット）
    // --------------------------------
    public sealed class WorkMessage
    {
        public string Type { get; set; }              // "Mail" | "Chat" | "Toast" | ...
        public string PayloadJson { get; set; }       // DTO を JSON で格納
        public WorkPriority Priority { get; set; } = WorkPriority.Normal;

        public override string ToString() => $"Type={Type}, Priority={Priority}, PayloadLen={PayloadJson?.Length ?? 0}";
    }

    // --------------------------------
    // ワーカーが使う共通コンテキスト
    // --------------------------------
    public sealed class WorkerCtx : IDisposable
    {
        public SmtpClient Smtp { get; set; }
        public HttpClient Http { get; set; }
        public SynchronizationContext UiCtx { get; set; }
        public CancellationToken Cancellation { get; set; }

        // UI スレッドでトーストを出すためのデリゲート（MainFormで設定）
        public Action<string, string, ToolTipIcon> ShowToast { get; set; }

        public void Dispose()
        {
            try { Smtp?.Dispose(); } catch { }
            try { Http?.Dispose(); } catch { }
        }
    }

    // --------------------------------
    // 仕事アイテムの抽象（Execute だけ）
    // --------------------------------
    public interface IWorkItem
    {
        void Execute(WorkerCtx ctx);
    }

    // --------------------------------
    // 各種ペイロード DTO（JSON化される）
    // --------------------------------
    public sealed class MailPayload
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
    }

    public sealed class ChatPayload
    {
        public string WebhookUrl { get; set; }
        public string JsonBody { get; set; } // そのままPOSTするJSON文字列
    }

    public sealed class ToastPayload
    {
        public string Title { get; set; }
        public string Text { get; set; }
        public string Icon { get; set; } // "Info" | "Warning" | "Error"
    }

    // --------------------------------
    // 仕事の実体：メール
    // --------------------------------
    public sealed class MailJob : IWorkItem
    {
        private readonly MailPayload _p;
        public MailJob(MailPayload p) { _p = p; }

        public void Execute(WorkerCtx ctx)
        {
            // 最低限のバリデーション
            if (string.IsNullOrWhiteSpace(_p.From) || string.IsNullOrWhiteSpace(_p.To))
                throw new ArgumentException("Mail: From/To is required");

            using (var msg = new MailMessage(_p.From, _p.To, _p.Subject ?? string.Empty, _p.Body ?? string.Empty))
            {
                ctx.Smtp.Send(msg); // 失敗時は例外
            }
        }
    }

    // --------------------------------
    // 仕事の実体：チャット(WebHook POST)
    // --------------------------------
    public sealed class ChatJob : IWorkItem
    {
        private readonly ChatPayload _p;
        public ChatJob(ChatPayload p) { _p = p; }

        public void Execute(WorkerCtx ctx)
        {
            if (string.IsNullOrWhiteSpace(_p.WebhookUrl))
                throw new ArgumentException("Chat: WebhookUrl is required");

            // 任意: Webhook ホワイトリスト判定などをここで
            var content = new StringContent(_p.JsonBody ?? "{}", Encoding.UTF8, "application/json");
            using (var req = new HttpRequestMessage(HttpMethod.Post, _p.WebhookUrl) { Content = content })
            using (var resp = ctx.Http.SendAsync(req, ctx.Cancellation).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("Chat post failed: " + (int)resp.StatusCode);
            }
        }
    }

    // --------------------------------
    // 仕事の実体：トースト（NotifyIcon バルーン）
    // --------------------------------
    public sealed class ToastJob : IWorkItem
    {
        private readonly ToastPayload _p;
        public ToastJob(ToastPayload p) { _p = p; }

        public void Execute(WorkerCtx ctx)
        {
            var icon = ToolTipIcon.Info;
            if (string.Equals(_p.Icon, "Warning", StringComparison.OrdinalIgnoreCase)) icon = ToolTipIcon.Warning;
            else if (string.Equals(_p.Icon, "Error", StringComparison.OrdinalIgnoreCase)) icon = ToolTipIcon.Error;

            ctx.UiCtx.Post(_ => ctx.ShowToast?.Invoke(_p.Title ?? "", _p.Text ?? "", icon), null);
        }
    }

    // --------------------------------
    // ファクトリ登録（Type -> WorkItem 生成）
    // --------------------------------
    public sealed class WorkItemFactory
    {
        private readonly Dictionary<string, Func<WorkMessage, IWorkItem>> _registry =
            new Dictionary<string, Func<WorkMessage, IWorkItem>>(StringComparer.OrdinalIgnoreCase);

        private readonly Action<string> _log;
        public WorkItemFactory(Action<string> log) { _log = log ?? (_ => { }); }

        public WorkItemFactory RegisterDefaults()
        {
            Register("Mail", m => new MailJob(Deserialize<MailPayload>(m.PayloadJson)));
            Register("Chat", m => new ChatJob(Deserialize<ChatPayload>(m.PayloadJson)));
            Register("Toast", m => new ToastJob(Deserialize<ToastPayload>(m.PayloadJson)));
            return this;
        }

        public WorkItemFactory Register(string type, Func<WorkMessage, IWorkItem> creator)
        { _registry[type] = creator; return this; }

        public IWorkItem TryCreate(WorkMessage m)
        {
            if (m == null || string.IsNullOrEmpty(m.Type)) return null;
            try
            {
                if (_registry.TryGetValue(m.Type, out var f)) return f(m);
                _log($"Unknown message type: {m.Type}");
            }
            catch (Exception ex)
            {
                _log($"Factory error: {m.Type} - {ex.Message}");
            }
            return null;
        }

        private T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("PayloadJson is empty");

            // サイズ上限（任意）：例 256KB
            if (json.Length > 256 * 1024) throw new InvalidOperationException("Payload too large");

            return JsonConvert.DeserializeObject<T>(json);
        }
    }

    // --------------------------------
    // ワーカー本体
    // --------------------------------
    public sealed class OutboxWorker
    {
        private readonly BlockingCollection<WorkMessage> _queue; // 単一キュー運用時のみ
        private readonly CancellationToken _token;
        private readonly WorkerCtx _ctx;
        private readonly WorkItemFactory _factory;
        private readonly Action<string> _log;

        public int MaxRetry { get; set; } = 3;
        public int BaseBackoffMs { get; set; } = 500; // 0.5s, 1s, 2s ...
        public string DeadLetterPath { get; set; } = "deadletter.log";

        public OutboxWorker(BlockingCollection<WorkMessage> queue,
                            CancellationToken token,
                            WorkerCtx ctx,
                            WorkItemFactory factory,
                            Action<string> log = null)
        {
            _queue = queue; _token = token; _ctx = ctx; _factory = factory; _log = log ?? (_ => { });
        }

        public void Run()
        {
            if (_queue == null) throw new InvalidOperationException("Run() requires a queue. Use ProcessOne() with external scheduling.");
            foreach (var msg in _queue.GetConsumingEnumerable(_token))
            {
                ProcessOne(msg);
            }
        }

        public void ProcessOne(WorkMessage msg)
        {
            var item = _factory.TryCreate(msg);
            if (item == null) { _log($"Skip: cannot create work item for {msg?.Type}"); return; }

            int attempt = 0;
            while (true)
            {
                try
                {
                    item.Execute(_ctx);
                    _log($"Done: {msg.Type}");
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    attempt++;
                    if (attempt >= MaxRetry)
                    {
                        _log($"Failed({attempt}): {msg.Type} - {ex.Message}");
                        DeadLetter(msg, ex);
                        return;
                    }
                    int delay = Math.Min(60_000, BaseBackoffMs * (1 << (attempt - 1))); // 最大60s
                    _log($"Retry in {delay}ms: {msg.Type} - {ex.Message}");
                    Thread.Sleep(delay);
                }
            }
        }

        private void DeadLetter(WorkMessage m, Exception ex)
        {
            try
            {
                File.AppendAllText(DeadLetterPath,
                    $"{DateTime.Now:O}\t{m.Type}\t{ex.Message}\t{m.PayloadJson}\r\n");
            }
            catch { /* ignore */ }
        }
    }

    // --------------------------------
    // パイプリスナ（受信→Enqueue）
    // --------------------------------
    public sealed class PipeListenerWithEnqueue
    {
        private readonly string _pipeName;
        private readonly Action<WorkMessage> _enqueue;
        private readonly CancellationToken _token;
        private readonly Action<string> _log;

        public PipeListenerWithEnqueue(string pipeName, Action<WorkMessage> enqueue, CancellationToken token, Action<string> log = null)
        { _pipeName = pipeName; _enqueue = enqueue; _token = token; _log = log ?? (_ => { }); }

        public void Run()
        {
            while (!_token.IsCancellationRequested)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(_pipeName, PipeDirection.In))
                    {
                        server.WaitForConnection(); // キャンセル不可。終了時はダミークライアントで接続して抜ける
                        using (var reader = new StreamReader(server))
                        {
                            var json = reader.ReadToEnd();
                            if (string.IsNullOrWhiteSpace(json)) continue;

                            // WorkMessage を直接 JSON にしている想定
                            var msg = JsonConvert.DeserializeObject<WorkMessage>(json);
                            if (msg != null) _enqueue(msg);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log($"Pipe error: {ex.Message}");
                    Thread.Sleep(200);
                }
            }
        }
    }

    // --------------------------------
    // メインフォーム
    // --------------------------------
    public partial class MainForm : Form
    {
        // ★ 設定（デモ用）：本番では設定ファイル等へ
        private const string SMTP_HOST = "smtp.example.com";
        private const int SMTP_PORT = 587;
        private const string SMTP_USER = "user@example.com";
        private const string SMTP_PASS = "password";
        private const int SMTP_TIMEOUT_MS = 10000;

        private const string PIPE_NAME = "MyAppPipe";

        // 単一/優先度キュー選択（true: 優先度運用）
        private readonly bool _usePriorityQueues = true;

        // 単一キュー運用時
        private readonly BlockingCollection<WorkMessage> _singleQueue = new BlockingCollection<WorkMessage>(boundedCapacity: 500);

        // 優先度キュー運用時
        private readonly BlockingCollection<WorkMessage> _qHigh = new BlockingCollection<WorkMessage>(300);
        private readonly BlockingCollection<WorkMessage> _qNormal = new BlockingCollection<WorkMessage>(500);
        private readonly BlockingCollection<WorkMessage> _qLow = new BlockingCollection<WorkMessage>(200);

        private CancellationTokenSource _cts;
        private Thread _workerThread;
        private Thread _pipeThread;
        private NotifyIcon _notifyIcon;
        private WorkerCtx _ctx;
        private WorkItemFactory _factory;

        public MainForm()
        {
            InitializeComponent();

            _notifyIcon = new NotifyIcon
            {
                Visible = true,
                Icon = System.Drawing.SystemIcons.Information,
                Text = "My Tray App"
            };
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            _cts = new CancellationTokenSource();

            var smtp = new SmtpClient(SMTP_HOST, SMTP_PORT)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(SMTP_USER, SMTP_PASS),
                Timeout = SMTP_TIMEOUT_MS
            };

            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            _ctx = new WorkerCtx
            {
                Smtp = smtp,
                Http = http,
                UiCtx = SynchronizationContext.Current,
                Cancellation = _cts.Token,
                ShowToast = (title, text, icon) => _notifyIcon.ShowBalloonTip(3000, title, text, icon)
            };

            _factory = new WorkItemFactory(Log).RegisterDefaults();

            if (_usePriorityQueues)
            {
                _workerThread = new Thread(() => PriorityWorkerLoop(_cts.Token)) { IsBackground = true };
                _workerThread.Start();
            }
            else
            {
                _workerThread = new Thread(() => new OutboxWorker(_singleQueue, _cts.Token, _ctx, _factory, Log).Run())
                { IsBackground = true };
                _workerThread.Start();
            }

            _pipeThread = new Thread(() => new PipeListenerWithEnqueue(PIPE_NAME, Enqueue, _cts.Token, Log).Run())
            { IsBackground = true };
            _pipeThread.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 生産終了宣言
            _singleQueue.CompleteAdding();
            _qHigh.CompleteAdding();
            _qNormal.CompleteAdding();
            _qLow.CompleteAdding();

            // キャンセル
            _cts.Cancel();

            // Pipe の WaitForConnection を抜けるためダミー接続
            try { KickPipeToExit(); } catch { }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _workerThread?.Join(2000); } catch { }
            try { _pipeThread?.Join(1000); } catch { }
            try { _notifyIcon?.Dispose(); } catch { }
            try { _ctx?.Dispose(); } catch { }
            _cts?.Dispose();
            base.OnFormClosed(e);
        }

        // ----------------------------
        // Enqueue ラッパ（優先度で仕分け）
        // ----------------------------
        private void Enqueue(WorkMessage m)
        {
            if (m == null) return;
            if (_usePriorityQueues)
            {
                switch (m.Priority)
                {
                    case WorkPriority.High: _qHigh.Add(m); break;
                    case WorkPriority.Low: _qLow.Add(m); break;
                    default: _qNormal.Add(m); break;
                }
            }
            else
            {
                _singleQueue.Add(m);
            }
            Log($"Enqueued: {m}");
        }

        // ----------------------------
        // UI からの投入例（ボタンハンドラなど）
        //   ※ テキストボックス: txtMailTo, txtSubject, txtBody, txtWebhook を想定
        // ----------------------------
        private void btnSendMail_Click(object sender, EventArgs e)
        {
            var payload = new MailPayload
            {
                From = "from@example.com",
                To = txtMailTo.Text,
                Subject = txtSubject.Text,
                Body = txtBody.Text
            };
            var msg = new WorkMessage
            {
                Type = "Mail",
                PayloadJson = JsonConvert.SerializeObject(payload),
                Priority = WorkPriority.Normal
            };
            Enqueue(msg);
        }

        private void btnSendChat_Click(object sender, EventArgs e)
        {
            var payload = new ChatPayload
            {
                WebhookUrl = txtWebhook.Text,
                JsonBody = "{\"text\":\"Hello from app\"}"
            };
            var msg = new WorkMessage
            {
                Type = "Chat",
                PayloadJson = JsonConvert.SerializeObject(payload),
                Priority = WorkPriority.Normal
            };
            Enqueue(msg);
        }

        private void btnToast_Click(object sender, EventArgs e)
        {
            var payload = new ToastPayload { Title = "通知", Text = "処理が完了しました", Icon = "Info" };
            var msg = new WorkMessage
            {
                Type = "Toast",
                PayloadJson = JsonConvert.SerializeObject(payload),
                Priority = WorkPriority.High
            };
            Enqueue(msg);
        }

        // ----------------------------
        // 優先度付きのデキューループ（フェアネス込み）
        // ----------------------------
        private void PriorityWorkerLoop(CancellationToken token)
        {
            var worker = new OutboxWorker(null, token, _ctx, _factory, Log)
            { MaxRetry = 3, BaseBackoffMs = 500 };

            int highStreak = 0; // 高優先度の連続処理数
            const int highBurst = 10; // 高優先度を何件処理したら他を1件挿むか

            var all = new[] { _qHigh, _qNormal, _qLow };

            while (!token.IsCancellationRequested)
            {
                WorkMessage msg;

                // 1) まずは高→中→低の順で即時取り出し
                if (_qHigh.TryTake(out msg, 0))
                {
                    worker.ProcessOne(msg);
                    highStreak++;
                    continue;
                }

                // 高優先度が一定連続したら、飢餓防止で Normal/Low を1件だけ挟む
                if (highStreak >= highBurst)
                {
                    if (_qNormal.TryTake(out msg, 0)) { worker.ProcessOne(msg); highStreak = 0; continue; }
                    if (_qLow.TryTake(out msg, 0)) { worker.ProcessOne(msg); highStreak = 0; continue; }
                    highStreak = 0; // どちらも無ければリセット
                }

                if (_qNormal.TryTake(out msg, 0)) { worker.ProcessOne(msg); continue; }
                if (_qLow.TryTake(out msg, 0)) { worker.ProcessOne(msg); continue; }

                // 2) すべて空なら、どれかに届くまでブロック
                int idx = BlockingCollection<WorkMessage>.TakeFromAny(all, out msg, -1);
                if (idx < 0) break; // 全てが CompleteAdding され空になった
                worker.ProcessOne(msg);
            }
        }

        // ----------------------------
        // Pipe の WaitForConnection を抜けるためのダミークライアント
        // ----------------------------
        private void KickPipeToExit()
        {
            // 何度か試す（複数待受けの可能性に備える）
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    using (var client = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.Out))
                    {
                        client.Connect(200);
                        using (var writer = new StreamWriter(client))
                        {
                            writer.Write("{}\") ; // 空 JSON を送ってすぐ閉じる");
                        }
                    }
                }
                catch { }
            }
        }

        // ----------------------------
        // ログ
        // ----------------------------
        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:T}] {message}");
            // ListBox 等に流したい場合はここで UI 反映
        }
    }
}
