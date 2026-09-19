using System;
using System.Threading.Tasks;

namespace RHelper.Compact;

// UI-thread only. Coalesce edits, keep one write in flight and invalidate queued writes.
internal sealed class LatestSliderCommand
{
    readonly Func<int,Func<bool>,Task> send;
    readonly Func<bool> allowed;
    readonly Func<Task> delay;
    int revision, value;
    bool pending, running;
    public LatestSliderCommand(Func<int,Func<bool>,Task> send,Func<bool> allowed,Func<Task>? delay=null)
    { this.send=send;this.allowed=allowed;this.delay=delay??(()=>Task.Delay(180)); }
    public void Cancel() { revision++;pending=false; }
    public Task Change(int next) {
        value=next;revision++;pending=true;
        return running?Task.CompletedTask:Drain();
    }
    async Task Drain() {
        running=true;
        try {
            while(pending) {
                int observed=revision;
                await delay();
                if(!pending)break;
                if(observed!=revision)continue;
                if(!allowed()) { pending=false;break; }
                int target=value;
                await send(target,()=>pending&&revision==observed&&allowed());
                if(revision==observed)pending=false;
            }
        } finally { running=false; }
    }
}
