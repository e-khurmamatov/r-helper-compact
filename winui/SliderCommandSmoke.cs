using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RHelper.Compact;
internal static class SliderCommandSmoke
{
    // Deterministic fake time/transport. No controller or hardware is created.
    public static async Task Run() {
        var delays=new Queue<TaskCompletionSource<bool>>();
        var sent=new List<int>();
        bool allowed=true;
        Task Delay() { var t=new TaskCompletionSource<bool>();delays.Enqueue(t);return t.Task; }
        async Task Tick() { delays.Dequeue().SetResult(true);await Task.Yield(); }
        var firstWrite=new TaskCompletionSource<bool>();
        var slider=new LatestSliderCommand(async(value,valid)=> {
            if(!valid())return;
            sent.Add(value);
            if(sent.Count==1)await firstWrite.Task;
        },()=>allowed,Delay);
        var drained=slider.Change(10);
        await slider.Change(11);
        await Tick(); // superseded delay
        await Tick(); // only 11 is sent
        if(sent.Count!=1||sent[0]!=11)throw new InvalidOperationException("Slider failed to coalesce edits");
        await slider.Change(12);await slider.Change(13);
        firstWrite.SetResult(true);await Task.Yield();
        await Tick();await drained;
        if(sent.Count!=2||sent[1]!=13)throw new InvalidOperationException("Slider lost final in-flight edit");
        drained=slider.Change(14);slider.Cancel();await Tick();await drained;
        if(sent.Count!=2)throw new InvalidOperationException("Cancelled slider wrote hardware");
        drained=slider.Change(15);allowed=false;await Tick();await drained;
        if(sent.Count!=2)throw new InvalidOperationException("Unavailable slider wrote hardware");
        // A provider/mode change while waiting for the shared transport must invalidate the write.
        allowed=true;
        var gate=new TaskCompletionSource<bool>();
        var queued=new LatestSliderCommand(async(value,valid)=> {
            await gate.Task;if(valid())sent.Add(value);
        },()=>allowed,Delay);
        drained=queued.Change(16);await Tick();queued.Cancel();gate.SetResult(true);await drained;
        if(sent.Count!=2)throw new InvalidOperationException("Stale queued slider command was sent");
    }
}
