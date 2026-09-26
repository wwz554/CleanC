using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using CleanC.Core;
using CleanC.Logging;

namespace CleanC.Repair;

public sealed record ComponentStoreAnalysis(bool Available,int ExitCode,string ExplorerSize,string ActualSize,
 string SharedSize,string BackupSize,string CacheSize,int ReclaimablePackages,bool CleanupRecommended,string Message,string Raw);
public sealed record ComponentCleanupResult(bool Completed,bool RequiresRestart,int ExitCode,string Message,ComponentStoreAnalysis? After);
public interface IComponentStoreHost
{
 bool IsAdministrator{get;}
 bool? RestartPending{get;}
 Task<(int ExitCode,string Output)> RunAsync(string arguments,IProgress<RepairProgress>? progress);
}

public sealed class ComponentStoreService
{
 public const string AnalyzeArguments="/English /Online /Cleanup-Image /AnalyzeComponentStore";
 public const string CleanupArguments="/English /Online /Cleanup-Image /StartComponentCleanup";
 readonly ICapabilityGate gate;readonly AuditLog log;readonly IComponentStoreHost host;
 int running;
 public bool IsRunning=>Volatile.Read(ref running)!=0;
 public ComponentStoreService(ICapabilityGate gate,AuditLog log,IComponentStoreHost? host=null)
 {this.gate=gate;this.log=log;this.host=host??new WindowsComponentStoreHost();}

 public async Task<ComponentStoreAnalysis> AnalyzeAsync(IProgress<RepairProgress>? progress=null)
 {
  gate.Demand(FeatureCapability.Scan);
  if(!host.IsAdministrator)return Unavailable(740,"需要管理员权限才能读取 Windows 组件存储；未将未知占用算作垃圾。","");
  using var lease=MaintenanceLock.Enter();
  Interlocked.Exchange(ref running,1);
  try{return await AnalyzeCore(progress).ConfigureAwait(false);}
  finally{Interlocked.Exchange(ref running,0);}
 }
 async Task<ComponentStoreAnalysis> AnalyzeCore(IProgress<RepairProgress>? progress)
 {
  var result=await host.RunAsync(AnalyzeArguments,progress).ConfigureAwait(false);
  var analysis=ParseAnalysis(result.ExitCode,result.Output);
  SaveReport("Analyze",analysis);return analysis;
 }
 public async Task<ComponentCleanupResult> CleanupAsync(IProgress<RepairProgress>? progress=null)
 {
  gate.Demand(FeatureCapability.Cleanup);
  if(!host.IsAdministrator)throw new UnauthorizedAccessException("Windows 组件清理需要管理员权限。");
  using var lease=MaintenanceLock.Enter();
  Interlocked.Exchange(ref running,1);
  try
  {
   // Never trust the stale UI report when making a servicing decision.
   if(host.RestartPending!=false)return Finish(new(false,true,3010,"Windows 待重启，或无法确认待重启状态。请重启后重新分析；本次未执行组件清理。",null));
   var before=await AnalyzeCore(progress).ConfigureAwait(false);
   if(!before.Available)return Finish(new(false,false,before.ExitCode,before.Message,before));
   if(!before.CleanupRecommended||before.ReclaimablePackages==0)
    return Finish(new(false,false,0,"Windows 当前没有建议清理的过期组件包；未执行删除。",before));
   gate.Demand(FeatureCapability.Cleanup);
   if(host.RestartPending!=false)return Finish(new(false,true,3010,"Windows 状态已变化，请重启后重试；未执行清理。",before));
   progress?.Report(new(0,"Windows 正在清理已被替代的组件，请勿关机"));
   var result=await host.RunAsync(CleanupArguments,progress).ConfigureAwait(false);
   SaveReport("CleanupCommand",new{result.ExitCode,result.Output});
   if(result.ExitCode==3010||result.ExitCode==1641||host.RestartPending!=false)
    return Finish(new(false,true,result.ExitCode,"Windows 要求重启或重启状态未确认；请重启后重新分析，不将本次标记为最终清理完成。",null));
   if(result.ExitCode!=0)return Finish(new(false,false,result.ExitCode,$"Windows 组件清理失败（错误码 {result.ExitCode}）；未将组件存储全部大小计为释放量。",null));
   var after=await AnalyzeCore(progress).ConfigureAwait(false);
   return Finish(new(after.Available,false,0,after.Available
    ?$"Windows 组件清理已完成并重新分析。组件存储实际大小：{before.ActualSize} → {after.ActualSize}；剩余可回收包 {after.ReclaimablePackages} 个。此大小变化不是精确磁盘释放量。"
    :"清理命令完成，但二次分析失败；结果待确认，请重新分析。",after));
  }
  finally{Interlocked.Exchange(ref running,0);}
 }
 ComponentCleanupResult Finish(ComponentCleanupResult result){SaveReport("Cleanup",result);return result;}
 void SaveReport<T>(string action,T result)
 {
  log.Write("ComponentStore",action,"Finished",detail:JsonSerializer.Serialize(result));
  try{File.WriteAllText(Path.Combine(log.DirectoryPath,$"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-component-store-{action}.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException){log.Write("ComponentStore","Report","Failed",detail:e.GetType().Name);}
 }
 static ComponentStoreAnalysis Unavailable(int code,string message,string raw)=>new(false,code,"—","—","—","—","—",0,false,message,raw);
 public static ComponentStoreAnalysis ParseAnalysis(int exitCode,string output)
 {
  if(exitCode!=0)return Unavailable(exitCode,$"组件存储分析未完成（DISM {exitCode}），不能据此判断可清理空间。",output);
  string Field(string label)
  {
   var matches=Regex.Matches(output,@"(?m)^\s*"+Regex.Escape(label)+@"\s*:\s*([^\r\n]+)\r?$",RegexOptions.CultureInvariant);
   return matches.Count==1?matches[0].Groups[1].Value.Trim():"";
  }
  var explorer=Field("Windows Explorer Reported Size of Component Store");var actual=Field("Actual Size of Component Store");
  var shared=Field("Shared with Windows");var backup=Field("Backups and Disabled Features");var cache=Field("Cache and Temporary Data");
  var count=Field("Number of Reclaimable Packages");var recommendation=Field("Component Store Cleanup Recommended");
  if(!int.TryParse(count,NumberStyles.None,CultureInfo.InvariantCulture,out var packages)||packages<0||
    recommendation is not ("Yes" or "No")||new[]{explorer,actual,shared,backup,cache}.Any(x=>!Regex.IsMatch(x,@"^\d+(?:[.,]\d+)?\s+(?:bytes|B|KB|MB|GB|TB)$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase)))
   return Unavailable(0,"Windows 返回了无法完整识别的组件存储报告；保留原始报告，不把未知状态标记为可清理。",output);
  return new(true,0,explorer,actual,shared,backup,cache,packages,recommendation=="Yes",
   recommendation=="Yes"?$"Windows 建议清理，发现 {packages} 个可回收组件包。":"Windows 当前不建议组件清理。",output);
 }
}

sealed class WindowsComponentStoreHost:IComponentStoreHost
{
 public bool IsAdministrator=>RepairService.IsAdministrator;
 public bool? RestartPending=>WindowsMaintenanceState.RestartPending;
 public async Task<(int ExitCode,string Output)> RunAsync(string arguments,IProgress<RepairProgress>? progress)
 {
  // Only the two fixed commands are admitted; no paths/user text or ResetBase are accepted.
  if(arguments!=ComponentStoreService.AnalyzeArguments&&arguments!=ComponentStoreService.CleanupArguments)throw new ArgumentException("不支持的组件维护命令。");
  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
  var encoding=Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
  using var process=new Process{StartInfo=new(){FileName=Path.Combine(Environment.SystemDirectory,"dism.exe"),Arguments=arguments,
   UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=encoding,StandardErrorEncoding=encoding}};
  // Service operations are not force-killed on UI navigation, license expiry or shutdown.
  process.Start();var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
  await process.WaitForExitAsync().ConfigureAwait(false);
  return(process.ExitCode,(await stdout.ConfigureAwait(false))+Environment.NewLine+(await stderr.ConfigureAwait(false)));
 }
}
public static class WindowsMaintenanceState
{
 public static bool? RestartPending
 {
  get
  {
   try
   {
    using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
    foreach(var path in new[]{@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"})
    {using var key=machine.OpenSubKey(path);if(key is not null)return true;}
    using var session=machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
    if(session is null)return null;
    return session?.GetValue("PendingFileRenameOperations") is string[] changes&&changes.Any(x=>!string.IsNullOrWhiteSpace(x));
   }
   catch(System.Security.SecurityException){return null;}catch(UnauthorizedAccessException){return null;}catch(IOException){return null;}
  }
 }
}
