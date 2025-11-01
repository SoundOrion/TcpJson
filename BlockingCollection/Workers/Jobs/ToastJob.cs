using BlockingCollection.Models;
using BlockingCollection.Models.Payloads;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BlockingCollection.Workers
{
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


            // UI スレッドで表示
            ctx.UiCtx.Post(_ => ctx.ShowToast?.Invoke(_p.Title, _p.Text, icon), null);
        }
    }
}
