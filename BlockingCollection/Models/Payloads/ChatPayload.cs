using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Models.Payloads
{
    public sealed class ChatPayload
    {
        public string WebhookUrl { get; set; }
        public string JsonBody { get; set; } // そのままPOSTするJSON文字列
    }
}
