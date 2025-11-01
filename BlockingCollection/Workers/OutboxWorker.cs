using BlockingCollection.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlockingCollection.Workers
{
    // --------------------------------
    // ワーカー本体：BlockingCollection で待機し続ける
    // ・指数バックオフリトライ（最大3回）
    // --------------------------------
    public sealed class OutboxWorker
    {
        private readonly BlockingCollection<WorkMessage> _queue;
        private readonly CancellationToken _token;
        private readonly WorkerCtx _ctx;
        private readonly WorkItemFactory _factory;
        private readonly Action<string> _log;


        public int MaxRetry { get; set; } = 3;
        public int BaseBackoffMs { get; set; } = 500; // 0.5s, 1s, 2s ...


        public OutboxWorker(
        BlockingCollection<WorkMessage> queue,
        CancellationToken token,
        WorkerCtx ctx,
        WorkItemFactory factory,
        Action<string> log = null)
        {
            _queue = queue; _token = token; _ctx = ctx; _factory = factory; _log = log ?? (_ => { });
        }


        public void Run()
        {
            foreach (var msg in _queue.GetConsumingEnumerable(_token))
            {
                var item = _factory.TryCreate(msg);
                if (item == null) { _log($"Unknown message type: {msg?.Type}"); continue; }


                int attempt = 0;
                while (true)
                {
                    try
                    {
                        item.Execute(_ctx);
                        _log($"Done: {msg.Type}");
                        break; // 成功
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        attempt++;
                        if (attempt >= MaxRetry)
                        {
                            _log($"Failed({attempt}): {msg.Type} - {ex.Message}");
                            break; // 諦める（Dead-letter を作るならここでファイルへ）
                        }
                        int delay = Math.Min(60_000, BaseBackoffMs * (1 << (attempt - 1))); // 最大60s
                        _log($"Retry in {delay}ms: {msg.Type} - {ex.Message}");
                        Thread.Sleep(delay);
                    }
                }
            }
        }
    }
}
