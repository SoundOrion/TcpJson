using BlockingCollection.Models;
using BlockingCollection.Models.Payloads;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Workers
{
    // --------------------------------
    // ファクトリ登録（Type -> WorkItem 生成）
    // → switch を排除し、拡張容易に
    // --------------------------------
    public sealed class WorkItemFactory
    {
        private readonly Dictionary<string, Func<WorkMessage, IWorkItem>> _registry =
        new Dictionary<string, Func<WorkMessage, IWorkItem>>(StringComparer.OrdinalIgnoreCase);


        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();


        public WorkItemFactory RegisterDefaults()
        {
            Register("Mail", m => new MailJob(_json.Deserialize<MailPayload>(m.PayloadJson)));
            Register("Chat", m => new ChatJob(_json.Deserialize<ChatPayload>(m.PayloadJson)));
            Register("Toast", m => new ToastJob(_json.Deserialize<ToastPayload>(m.PayloadJson)));
            return this;
        }


        public WorkItemFactory Register(string type, Func<WorkMessage, IWorkItem> creator)
        {
            _registry[type] = creator; return this;
        }


        public IWorkItem TryCreate(WorkMessage m)
        {
            if (m == null || string.IsNullOrEmpty(m.Type)) return null;
            Func<WorkMessage, IWorkItem> f;
            if (_registry.TryGetValue(m.Type, out f)) return f(m);
            return null;
        }
    }
}
