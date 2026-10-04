using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CleanC.Repair;

// WUA's documented automation callback (DISPID 0). No references back to the job:
// cancellation must never leave a job/callback reference cycle behind.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class UpdateSearchCallback
{
 [DispId(0)] public void Invoke(object job,object args) { }
}

public static class BoundedOperation
{
 public static void Wait(Func<bool> completed,TimeSpan timeout,CancellationToken token,Action<TimeSpan>? heartbeat=null)
 {
  var timer=Stopwatch.StartNew();var last=TimeSpan.FromSeconds(-1);
  while(true){
   token.ThrowIfCancellationRequested();
   if(completed())return;
   if(timer.Elapsed>=timeout)throw new TimeoutException("Windows 官方更新查询超过 120 秒，本机检测结果已保留，请稍后重试在线检查。");
   if(timer.Elapsed-last>=TimeSpan.FromSeconds(1)){last=timer.Elapsed;heartbeat?.Invoke(last);}
   token.WaitHandle.WaitOne(100);
  }
 }
}

public static class WindowsUpdateSearch
{
 public static object Run(object searcher,string criteria,CancellationToken token,Action<TimeSpan>? heartbeat=null)
 {
  dynamic query=searcher;dynamic? job=null;bool completed=false;var callback=new UpdateSearchCallback();
  try{
   token.ThrowIfCancellationRequested();
   job=query.BeginSearch(criteria,callback,null);
   BoundedOperation.Wait(()=>Convert.ToBoolean(job.IsCompleted),TimeSpan.FromSeconds(120),token,heartbeat);
   completed=true;
   return query.EndSearch(job);
  }
  finally{
   if(job is not null){
    if(!completed){try{job.RequestAbort();}catch(COMException){}}
    // CleanUp waits for completion. Never call it on an unresponsive canceled
    // job or from the callback; WUA retains its own callback reference if needed.
    else{try{job.CleanUp();}catch(COMException){}}
    try{if(Marshal.IsComObject(job))Marshal.ReleaseComObject(job);}catch(COMException){}
   }
   GC.KeepAlive(callback);
  }
 }
}
