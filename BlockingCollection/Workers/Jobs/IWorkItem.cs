using BlockingCollection.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BlockingCollection.Workers
{
    // --------------------------------
    // 仕事アイテムの抽象（Execute だけ）
    // --------------------------------
    public interface IWorkItem
    {
        void Execute(WorkerCtx ctx);
    }
}
