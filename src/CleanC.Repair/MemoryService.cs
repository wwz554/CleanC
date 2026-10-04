using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CleanC.Core;
using CleanC.Logging;
namespace CleanC.Repair;

public sealed record MemorySnapshot(ulong Physical,ulong Available,ulong Commit,ulong CommitLimit,ulong Cache,ulong PagedPool,ulong NonPagedPool);
public sealed record MemoryTarget(int Pid,string Name,string Path,long Started,bool Eligible,long WorkingSet,string Reason,long PrivateBytes=0);
public sealed record MemorySample(DateTimeOffset At,MemorySnapshot Snapshot,IReadOnlyList<MemoryTarget> Targets);
public sealed record MemoryGrowth(int Pid,string Name,long Started,long PrivateBytesDelta,long WorkingSetDelta);
public sealed record MemoryTrimResult(MemorySnapshot Before,MemorySnapshot After,int Succeeded,int Skipped,long WorkingSetReduction);
public sealed class MemoryService(ICapabilityGate gate,AuditLog log)
{
 public MemorySnapshot Read()
 {
  var m=new MEMORYSTATUSEX{Length=(uint)Marshal.SizeOf<MEMORYSTATUSEX>()};
  var p=new PERFORMANCE_INFORMATION{Size=(uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()};
  if(!GlobalMemoryStatusEx(ref m)||!GetPerformanceInfo(ref p,p.Size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  ulong Bytes(nuint pages)=>(ulong)pages*(ulong)p.PageSize;
  return new(m.TotalPhys,m.AvailPhys,Bytes(p.CommitTotal),Bytes(p.CommitLimit),Bytes(p.SystemCache),Bytes(p.KernelPaged),Bytes(p.KernelNonpaged));
 }
 public IReadOnlyList<MemoryTarget> Analyze(int limit=40)
 {
  gate.Demand(FeatureCapability.SystemRepair);
  var list=new List<MemoryTarget>();
  foreach(var process in Process.GetProcesses()){
   using(process)try{
    var target=Inspect(process.Id,false,out var _);if(target is not null)list.Add(target);
   }catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException){}
  }
  return list.OrderByDescending(x=>x.PrivateBytes).ThenByDescending(x=>x.WorkingSet).Take(Math.Clamp(limit,1,100)).ToArray();
 }
 public MemorySample Sample()=>new(DateTimeOffset.UtcNow,Read(),Analyze(100));
 public static IReadOnlyList<MemoryGrowth> Compare(MemorySample first,MemorySample last)
 {
  var old=first.Targets.ToDictionary(x=>(x.Pid,x.Started));
  return last.Targets.Where(x=>old.ContainsKey((x.Pid,x.Started))).Select(x=>new MemoryGrowth(x.Pid,x.Name,x.Started,
   x.PrivateBytes-old[(x.Pid,x.Started)].PrivateBytes,x.WorkingSet-old[(x.Pid,x.Started)].WorkingSet))
   .OrderByDescending(x=>x.PrivateBytesDelta).Take(10).ToArray();
 }
 public static string Explain(MemorySnapshot m)=>
  $"物理内存与提交额度是两回事。提交上限 {Display.Bytes((long)m.CommitLimit)} 是 Windows 可承诺的总额度（物理内存 + 分页文件），不是已占用的 RAM。"+
  $"当前提交 {Display.Bytes((long)m.Commit)}；工作集回收不会取消程序的这些分配。可用内存 {Display.Bytes((long)m.Available)}，缓存不是额外扣掉的一份内存。"+
  (m.CommitLimit>0&&m.Commit>=m.CommitLimit*0.9?"提交已接近上限：保存工作，检查占用持续增长的程序；保留系统管理分页文件。":"单张截图不能诊断泄漏，应观察同一进程的私有提交是否持续增长。");
 static readonly HashSet<string> Excluded=new(StringComparer.OrdinalIgnoreCase){
  "CleanC","System","Registry","smss","csrss","wininit","winlogon","services","lsass","svchost","dwm","explorer","MsMpEng","NisSrv","SecurityHealthService",
  "TiWorker","TrustedInstaller","MoUsoCoreWorker","dism","sfc","chkdsk","WUDFHost","Memory Compression","vmwp","vmmem","vmmemWSL"};
 public static bool Allowed(string name,bool critical,bool sameUser,bool sameSession,bool foreground,bool visible,bool systemPath,long workingSet)
  =>!Excluded.Contains(name)&&!critical&&sameUser&&sameSession&&!foreground&&!visible&&!systemPath&&workingSet>=32L*1024*1024;
 MemoryTarget? Inspect(int pid,bool write,out SafeProcessHandle? retained)
 {
  retained=null;var handle=OpenProcess(0x1000u|(write?0x100u:0),false,pid);
  if(handle.IsInvalid){handle.Dispose();return null;}
  try{
   var path=new StringBuilder(32768);uint capacity=32768;
   if(!GetProcessTimes(handle,out var created,out _,out _,out _)||!QueryFullProcessImageName(handle,0,path,ref capacity))return null;
   var name=System.IO.Path.GetFileNameWithoutExtension(path.ToString());
   bool critical=!IsProcessCritical(handle,out var c)||c;
   bool sameUser=false;
   if(OpenProcessToken(handle,8,out var token)){using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))using(var self=WindowsIdentity.GetCurrent())sameUser=Equals(identity.User,self.User);}
   using var process=Process.GetProcessById(pid);
   using var selfProcess=Process.GetCurrentProcess();
   GetWindowThreadProcessId(GetForegroundWindow(),out var front);
   string? frontName=null;try{using var f=Process.GetProcessById((int)front);frontName=f.ProcessName;}catch(ArgumentException){}catch(InvalidOperationException){}
   var memory=new PROCESS_MEMORY_COUNTERS{Size=(uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>()};
   if(!GetProcessMemoryInfo(handle,ref memory,memory.Size))return null;
   var bytes=(long)memory.WorkingSet;var system=SafetyPolicy.Within(path.ToString(),Environment.GetFolderPath(Environment.SpecialFolder.Windows));
   var eligible=Allowed(name,critical,sameUser,process.SessionId==selfProcess.SessionId,pid==(int)front||name.Equals(frontName,StringComparison.OrdinalIgnoreCase),
    process.MainWindowHandle!=IntPtr.Zero,system,bytes);
   var target=new MemoryTarget(pid,name,path.ToString(),created,eligible,bytes,eligible?"可手动回收的后台工作集":"前台 / 可见窗口 / 系统或关键进程 / 权限不足 / 占用较小，跳过",(long)memory.PrivateUsage);
   if(write){retained=handle;handle=null!;}return target;
  }finally{handle?.Dispose();}
 }
 public async Task<MemoryTrimResult> TrimAsync(IEnumerable<MemoryTarget> selected,CancellationToken token=default)
 {
  gate.Demand(FeatureCapability.SystemRepair);var targets=selected.DistinctBy(x=>x.Pid).ToArray();
  return await Task.Run(()=>{
   using var maintenance=MaintenanceLock.Enter();
   var before=Read();int ok=0,skipped=0;long reduction=0;
   foreach(var target in targets){
    token.ThrowIfCancellationRequested();gate.Demand(FeatureCapability.SystemRepair);
    SafeProcessHandle? handle=null;
    try{
     var current=Inspect(target.Pid,true,out handle);
     if(current is null||!target.Eligible||!current.Eligible||current.Started!=target.Started||!current.Path.Equals(target.Path,StringComparison.OrdinalIgnoreCase)||handle is null){skipped++;continue;}
     if(!EmptyWorkingSet(handle)){skipped++;continue;}
     ok++;var memory=new PROCESS_MEMORY_COUNTERS{Size=(uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>()};
     if(GetProcessMemoryInfo(handle,ref memory,memory.Size))reduction+=Math.Max(0,current.WorkingSet-(long)memory.WorkingSet);
    }catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException){skipped++;}
    finally{handle?.Dispose();}
   }
   var result=new MemoryTrimResult(before,Read(),ok,skipped,reduction);
   log.Write("Memory","SelectedWorkingSet","Completed",detail:$"success={ok}; skipped={skipped}; workingSetReduction={reduction}; availableBefore={before.Available}; availableAfter={result.After.Available}");
   return result;
  },token).ConfigureAwait(false);
 }
 [StructLayout(LayoutKind.Sequential)]struct MEMORYSTATUSEX{public uint Length,Load;public ulong TotalPhys,AvailPhys,TotalPageFile,AvailPageFile,TotalVirtual,AvailVirtual,AvailExtended;}
 [StructLayout(LayoutKind.Sequential)]struct PERFORMANCE_INFORMATION{public uint Size;public nuint CommitTotal,CommitLimit,CommitPeak,PhysicalTotal,PhysicalAvailable,SystemCache,KernelTotal,KernelPaged,KernelNonpaged,PageSize;public uint Handles,Processes,Threads;}
 [StructLayout(LayoutKind.Sequential)]struct PROCESS_MEMORY_COUNTERS{public uint Size,PageFaultCount;public nuint PeakWorkingSet,WorkingSet,PeakPaged,QuotaPaged,PeakNonPaged,QuotaNonPaged,Pagefile,PeakPagefile,PrivateUsage;}
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX value);
 [DllImport("psapi.dll",SetLastError=true)]static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION value,uint size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessTimes(SafeProcessHandle p,out long created,out long exited,out long kernel,out long user);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(SafeProcessHandle p,uint flags,StringBuilder path,ref uint size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool IsProcessCritical(SafeProcessHandle p,out bool critical);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(SafeProcessHandle p,uint access,out SafeAccessTokenHandle token);
 [DllImport("psapi.dll",SetLastError=true)]static extern bool GetProcessMemoryInfo(SafeProcessHandle p,ref PROCESS_MEMORY_COUNTERS value,uint size);
 [DllImport("psapi.dll",SetLastError=true)]static extern bool EmptyWorkingSet(SafeProcessHandle p);
 [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
}
