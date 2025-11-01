using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Models.Payloads
{
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
}
