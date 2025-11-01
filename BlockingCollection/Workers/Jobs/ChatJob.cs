using BlockingCollection.Models;
using BlockingCollection.Models.Payloads;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Workers
{
    // --------------------------------
    // 仕事の実体：チャット(WebHook POST)
    // --------------------------------
    public sealed class ChatJob : IWorkItem
    {
        private readonly ChatPayload _p;
        public ChatJob(ChatPayload p) { _p = p; }


        public void Execute(WorkerCtx ctx)
        {
            var content = new StringContent(_p.JsonBody, Encoding.UTF8, "application/json");
            using (var req = new HttpRequestMessage(HttpMethod.Post, _p.WebhookUrl) { Content = content })
            {
                var resp = ctx.Http.SendAsync(req).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("Chat post failed: " + (int)resp.StatusCode);
            }
        }
    }
}
