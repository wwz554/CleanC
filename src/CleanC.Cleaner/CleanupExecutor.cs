using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CleanC.Core;
using CleanC.Logging;
namespace CleanC.Cleaner;
public interface ICleanupExecutor
{
 Task<CleanupReport> ExecuteAsync(IReadOnlyList<ScanItem> items,bool dryRun,IProgress<CleanupProgress>? progress,CancellationToken token,Action<CleanupOutcome>? outcomeSink=null);
}
public sealed class CleanupExecutor(ICapabilityGate gate,SafetyPolicy policy,AuditLog log) : ICleanupExecutor
{
 public Task<CleanupReport> ExecuteAsync(IReadOnlyList<ScanItem> items,bool dryRun,IProgress<CleanupProgress>? progress,CancellationToken token,Action<CleanupOutcome>? outcomeSink=null)=>Task.Run(()=>Run(items,dryRun,progress,token,outcomeSink));
 CleanupReport Run(IReadOnlyList<ScanItem> items,bool dryRun,IProgress<CleanupProgress>? progress,CancellationToken token,Action<CleanupOutcome>? outcomeSink)
 {
  gate.Demand(FeatureCapability.Cleanup);
  var started=DateTimeOffset.UtcNow;var outcomes=new List<CleanupOutcome>(items.Count);long freed=0;int deleted=0,skipped=0;bool canceled=false;
  void AddOutcome(CleanupOutcome item){outcomes.Add(item);try{outcomeSink?.Invoke(item);}catch(Exception e){log.Write("Cleanup","OutcomeSink","Ignored",detail:e.GetType().Name);}}
  var ui=Stopwatch.StartNew();var ordered=items.OrderBy(x=>Path.GetDirectoryName(x.File.Path),StringComparer.OrdinalIgnoreCase).ToList();
  log.Write("Cleanup",dryRun?"DryRunBatch":"PermanentDeleteBatch","Started",detail:$"items={items.Count}; recycleBin=false; recoveryCopy=false");
  string? activeDirectory=null;PinnedDirectoryChain? directoryPin=null;Exception? directoryPinError=null;var freshMarkers=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
  try
  {
   foreach(var item in ordered)
   {
    if(token.IsCancellationRequested){canceled=true;break;}
    try
    {
     var expected=item.File;var directory=Path.GetDirectoryName(expected.Path)??Path.GetPathRoot(expected.Path)!;long deletedBytes=0;
     if(!string.Equals(directory,activeDirectory,StringComparison.OrdinalIgnoreCase))
     {
      directoryPin?.Dispose();directoryPin=null;directoryPinError=null;activeDirectory=directory;
      try{directoryPin=new PinnedDirectoryChain(directory);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception){directoryPinError=e;}
     }
     if(directoryPinError is not null)throw new IOException("目录安全锁定失败："+directoryPinError.Message,directoryPinError);
     if(item.Classification.Safety==SafetyLevel.Protected)throw new IOException("受保护项目不能清理。");
     using(var pin=new PinnedFile(expected.Path,deleteAccess:!dryRun,directoryPin:directoryPin,cooperativeShare:false))
     {
      var current=pin.Snapshot;
      var kind=policy.Classify(current,DateTime.UtcNow,fresh:true,sessionMarkers:freshMarkers);
      if(kind.Safety==SafetyLevel.Protected||kind.Safety>item.Classification.Safety)throw new IOException("安全级别发生变化，已跳过。");
      if(item.Classification.RuleId is not null&&kind.RuleId!=item.Classification.RuleId)throw new IOException("安全规则复核未通过。");
      // Every permanent deletion, including volatile cache, must remain bound to the same Windows file.
      // Active or changed caches are skipped rather than deleted concurrently with their owner.
      if(expected.FileId==0||expected.Volume==0)
       throw new IOException("扫描时未取得可靠的 Windows 文件身份，已停止删除。");
      if(current.FileId!=expected.FileId||current.Volume!=expected.Volume||current.Links!=1)
       throw new IOException("文件身份已经变化，已停止删除。");
      if(current.Size!=expected.Size||current.LastWriteUtc!=expected.LastWriteUtc||current.CreationUtc!=expected.CreationUtc||current.Attributes!=expected.Attributes)
       throw new IOException("文件时间、大小或属性已经变化，已停止删除。");
      gate.Demand(FeatureCapability.Cleanup);token.ThrowIfCancellationRequested();
      deletedBytes=current.Size;
      if(!dryRun)pin.MarkForDeletion();
     }
     if(!dryRun){freed+=deletedBytes;deleted++;}
     AddOutcome(new(expected.Path,dryRun?"WouldDelete":"Deleted",deletedBytes,$"用户已选择；复核通过（{item.Classification.Safety}）"));
    }
    catch(OperationCanceledException){canceled=true;break;}
    catch(Exception e)when(IsMissingPath(e))
    {
     var detail="扫描后该文件已被系统或应用删除/移动，已从清理结果中移除，不作为 CleanC 删除计数。";
     AddOutcome(new(item.File.Path,"Gone",0,detail));
     log.Write("Cleanup","PermanentDelete","AlreadyGone",item.File.Path,e.GetType().Name);
    }
    catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception)
    {
     var detail=e is Win32Exception w&&w.NativeErrorCode is 32 or 33
      ?"文件正在被浏览器或应用使用；本次不删除，并会移到可选项避免反复自动清理。"
      :e is UnauthorizedAccessException
       ?"当前没有安全删除权限；本次不删除，并会重新评估安全级别。"
       :e.Message;
     skipped++;AddOutcome(new(item.File.Path,"Skipped",0,detail));log.Write("Cleanup","PermanentDelete","Skipped",item.File.Path,detail);
    }
    catch(Exception e)when(e.GetType().Name=="LicenseException"){canceled=true;log.Write("Cleanup","PermanentDelete","LicenseStopped");break;}
    if(progress is not null&&(ui.ElapsedMilliseconds>=140||outcomes.Count==items.Count))
    {
     progress.Report(new(outcomes.Count,items.Count,freed,item.File.Path));ui.Restart();
    }
   }
  }
  finally{directoryPin?.Dispose();}
  progress?.Report(new(outcomes.Count,items.Count,freed,outcomes.Count>0?ordered[Math.Min(outcomes.Count,ordered.Count)-1].File.Path:string.Empty));
  var report=new CleanupReport(started,dryRun,freed,deleted,skipped,canceled,outcomes);
  log.Write("Cleanup",dryRun?"DryRunBatch":"PermanentDeleteBatch",canceled?"Canceled":"Completed",detail:$"deleted={deleted}; skipped={skipped}; freed={freed}; recycleBin=false; recoveryCopy=false");
  WriteReport(report);
  return report;
 }
 static bool IsMissingPath(Exception e)
 {
  if(e is FileNotFoundException or DirectoryNotFoundException)return true;
  if(e is Win32Exception w&&w.NativeErrorCode is 2 or 3)return true;
  var code=e.HResult&0xFFFF;
  return code is 2 or 3;
 }
 void WriteReport(CleanupReport report)
 {
  try
  {
   var reportDir=Path.Combine(log.DirectoryPath,"Reports");Directory.CreateDirectory(reportDir);
   File.WriteAllText(Path.Combine(reportDir,$"{report.StartedAt:yyyyMMdd-HHmmss-ffff}-cleanup.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
  }
  catch(Exception e){log.Write("Cleanup","Report","WriteFailed",detail:e.GetType().Name);}
 }
}
