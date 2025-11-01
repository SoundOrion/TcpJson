using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BlockingCollection.Models
{
    // --------------------------------
    // ワーカーが使う共通コンテキスト
    // --------------------------------
    public sealed class WorkerCtx : IDisposable
    {
        public SmtpClient Smtp { get; set; }
        public HttpClient Http { get; set; }
        public SynchronizationContext UiCtx { get; set; }


        // UI スレッドでトーストを出すためのデリゲート（MainFormで設定）
        public Action<string, string, ToolTipIcon> ShowToast { get; set; }


        public void Dispose()
        {
            if (Smtp != null) Smtp.Dispose();
            if (Http != null) Http.Dispose();
        }
    }
}
