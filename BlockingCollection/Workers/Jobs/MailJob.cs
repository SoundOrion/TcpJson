using BlockingCollection.Models;
using BlockingCollection.Models.Payloads;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Workers
{
    // --------------------------------
    // 仕事の実体：メール
    // --------------------------------
    public sealed class MailJob : IWorkItem
    {
        private readonly MailPayload _p;
        public MailJob(MailPayload p) { _p = p; }


        public void Execute(WorkerCtx ctx)
        {
            using (var msg = new MailMessage(_p.From, _p.To, _p.Subject, _p.Body))
            {
                ctx.Smtp.Send(msg); // 失敗時は例外
            }
        }
    }
}
