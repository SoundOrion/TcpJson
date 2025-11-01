using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Models.Payloads
{
    public sealed class ToastPayload
    {
        public string Title { get; set; }
        public string Text { get; set; }
        public string Icon { get; set; } // "Info" | "Warning" | "Error"
    }
}
