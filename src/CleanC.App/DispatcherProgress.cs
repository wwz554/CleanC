using Microsoft.UI.Dispatching;

namespace CleanC.App;

/// <summary>
/// Marshals progress callbacks to the WinUI DispatcherQueue.
/// Progress<T> relies on SynchronizationContext capture, which can vary by host/entry point.
/// This implementation never touches XAML from a worker thread and never lets a progress
/// callback exception escape onto ThreadPool/dispatcher infrastructure.
/// </summary>
internal sealed class DispatcherProgress<T> : IProgress<T>, IDisposable
{
 private readonly DispatcherQueue dispatcher;
 private readonly Action<T> handler;
 private readonly Action<Exception>? onError;
 private int stopped;

 public DispatcherProgress(DispatcherQueue dispatcher,Action<T> handler,Action<Exception>? onError=null)
 {
  this.dispatcher=dispatcher??throw new ArgumentNullException(nameof(dispatcher));
  this.handler=handler??throw new ArgumentNullException(nameof(handler));
  this.onError=onError;
 }

 public void Report(T value)
 {
  if(Volatile.Read(ref stopped)!=0)return;
  try
  {
   var queued=dispatcher.TryEnqueue(DispatcherQueuePriority.Low,()=>
   {
    if(Volatile.Read(ref stopped)!=0)return;
    try{handler(value);}catch(Exception e){SafeError(e);}
   });
   if(!queued)SafeError(new InvalidOperationException("UI dispatcher is shutting down."));
  }
  catch(Exception e){SafeError(e);}
 }

 void SafeError(Exception e)
 {
  try{onError?.Invoke(e);}catch{}
 }

 public void Dispose()=>Interlocked.Exchange(ref stopped,1);
}
