using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Models
{
    // --------------------------------
    // メッセージ（UI→ワーカーへ渡す汎用フォーマット）
    // --------------------------------
    public class WorkMessage
    {
        public string Type { get; set; } // "Mail" | "Chat" | "Toast" ...
        public string PayloadJson { get; set; } // DTO を JSON で格納
    }
}
