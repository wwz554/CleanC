namespace CleanC.Core;

// A process-wide lease across cleanup, component servicing, system repair and driver writes.
// Unlike Mutex, disposal is safe after an await resumes on a different thread.
public static class MaintenanceLock
{
 static int held;
 public static bool IsHeld=>Volatile.Read(ref held)!=0;
 public static IDisposable Enter()
 {
  if(Interlocked.CompareExchange(ref held,1,0)!=0)
   throw new InvalidOperationException("其他清理、系统维护或驱动写入任务正在运行，请等待完成后重试。");
  return new Lease();
 }
 sealed class Lease:IDisposable
 {
  int disposed;
  public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)Volatile.Write(ref held,0);}
 }
}
