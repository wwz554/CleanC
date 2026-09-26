using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CleanC.Core;
using CleanC.Logging;
namespace CleanC.Repair;
public enum RepairAction { FullRepair, ImageCheck, ImageScan, ImageRestore, SystemVerify, SystemRepair, DiskScan }
public sealed record RepairCommand(string Executable,string Arguments,string Title,bool ChangesSystem);
public sealed record RepairProgress(int Percent,string Stage);
public sealed record RepairResult(RepairAction Action,int ExitCode,DateTimeOffset StartedUtc,DateTimeOffset EndedUtc,string Output,bool RequiresRestart,string Conclusion)
{
 public bool Success=>!RequiresRestart&&ExitCode==0&&(Conclusion.StartsWith("修复成功",StringComparison.Ordinal)||Conclusion.StartsWith("系统状态良好",StringComparison.Ordinal)||Conclusion.StartsWith("已验证",StringComparison.Ordinal));
 public string Summary=>Conclusion;
}
public static class RepairCommands
{
 public static RepairCommand Get(RepairAction action)=>action switch{
  RepairAction.FullRepair=>new("","","完整修复并验证",true),
  RepairAction.ImageCheck=>new("dism.exe","/Online /Cleanup-Image /CheckHealth","Windows 映像快速检查",false),
  RepairAction.ImageScan=>new("dism.exe","/Online /Cleanup-Image /ScanHealth","Windows 映像完整检查",false),
  RepairAction.ImageRestore=>new("dism.exe","/Online /Cleanup-Image /RestoreHealth","修复 Windows 映像",true),
  RepairAction.SystemVerify=>new("sfc.exe","/verifyonly","系统文件检查",false),
  RepairAction.SystemRepair=>new("sfc.exe","/scannow","修复系统文件",true),
  RepairAction.DiskScan=>new("chkdsk.exe",SystemVolume+" /scan","系统盘文件系统在线检查",true),
  _=>throw new ArgumentOutOfRangeException(nameof(action))};
 public static string SystemVolume=>Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');
}
public sealed class RepairService(ICapabilityGate gate,AuditLog log)
{
 int running;
 public bool IsRunning=>Volatile.Read(ref running)!=0;
 public static bool IsAdministrator=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
 public async Task<RepairResult> RunAsync(RepairAction action,IProgress<RepairProgress>? progress)
 {
  gate.Demand(FeatureCapability.SystemRepair);
  if(IsRunning)throw new InvalidOperationException("已有系统检查或修复正在运行。");
  if(!IsAdministrator)throw new UnauthorizedAccessException("此操作需要管理员权限，请使用管理员身份运行 CleanC。");
  using var maintenance=MaintenanceLock.Enter();
  if(RepairCommands.Get(action).ChangesSystem&&WindowsMaintenanceState.RestartPending!=false)
   throw new InvalidOperationException("Windows 尚待重启或无法确认重启状态，请先重启再执行修复。");
  if(Interlocked.CompareExchange(ref running,1,0)!=0)throw new InvalidOperationException("已有系统检查或修复正在运行。");
  try
  {
   if(action==RepairAction.FullRepair)return await RunFullRepairAsync(progress).ConfigureAwait(false);

   var command=RepairCommands.Get(action);var started=DateTimeOffset.UtcNow;var report=new StringBuilder();
   log.Write("Repair",action.ToString(),"Started",command.Executable,command.Arguments);
   var primaryEnd=command.ChangesSystem?78:86;
   var primary=await RunCommandAsync(action,command,2,primaryEnd,progress).ConfigureAwait(false);
   report.AppendLine("=== PRIMARY ===").AppendLine(primary.Output);
   var requiresRestart=primary.ExitCode is 3010 or 1641;
   string conclusion;int finalExit=primary.ExitCode;

   if(action is RepairAction.ImageCheck or RepairAction.ImageScan)
   {
    if(primary.ExitCode!=0&&primary.ExitCode!=3010)conclusion=$"检查失败：DISM 返回错误码 {primary.ExitCode}。";
    else
    {
     progress?.Report(new(90,"正在读取 Microsoft ImageHealthState"));
     var health=await QueryImageHealthAsync(false).ConfigureAwait(false);
     report.AppendLine("=== IMAGE-STATE ===").AppendLine(health.Raw);
     conclusion=ImageHealthText(health.State,false);finalExit=health.ExitCode;
    }
   }
   else if(action==RepairAction.ImageRestore)
   {
    if(primary.ExitCode!=0&&primary.ExitCode!=3010)
     conclusion=IsDismSourceError(primary.Output,primary.ExitCode)
      ?"修复失败：DISM 无法获得匹配的微软修复源。需要使用与当前 Windows 版本、语言、架构匹配的安装介质作为 /Source。"
      :$"修复失败：DISM 返回错误码 {primary.ExitCode}。";
    else
    {
     progress?.Report(new(82,"正在独立执行 DISM ScanHealth 验证"));
     var health=await QueryImageHealthAsync(true).ConfigureAwait(false);
     report.AppendLine("=== VERIFY-IMAGE ===").AppendLine(health.Raw);
     conclusion=ImageHealthText(health.State,true);finalExit=health.ExitCode;
    }
   }
   else if(action==RepairAction.SystemRepair)
   {
    if(primary.ExitCode!=0)
     conclusion=$"系统文件修复未成功完成（错误码 {primary.ExitCode}）。"+AnalyzeSfcVerify(primary.Output);
    else
    {
     progress?.Report(new(82,"正在独立执行 SFC /verifyonly"));
     var verify=await RunCommandAsync(RepairAction.SystemVerify,RepairCommands.Get(RepairAction.SystemVerify),82,98,progress).ConfigureAwait(false);
     report.AppendLine("=== VERIFY-SFC ===").AppendLine(verify.Output);finalExit=verify.ExitCode;
     conclusion=AnalyzeVerifiedRepair(RepairAction.SystemRepair,verify.ExitCode,verify.Output);
    }
   }
   else if(action==RepairAction.DiskScan)
   {
    conclusion=AnalyzeDiskExit(primary.ExitCode,false);
    if(primary.ExitCode==1)
    {
     progress?.Report(new(88,"正在再次验证 C 盘文件系统"));
     var verify=await RunCommandAsync(RepairAction.DiskScan,RepairCommands.Get(RepairAction.DiskScan),88,98,progress).ConfigureAwait(false);
     report.AppendLine("=== VERIFY-DISK ===").AppendLine(verify.Output);finalExit=verify.ExitCode;
     conclusion=AnalyzeDiskExit(verify.ExitCode,true);
    }
   }
   else
   {
    if(primary.ExitCode!=0&&primary.ExitCode!=3010)conclusion=$"操作失败：Windows 返回错误码 {primary.ExitCode}。";
    else conclusion=AnalyzeCheck(action,primary.ExitCode,primary.Output);
   }

   requiresRestart|=finalExit is 3010 or 1641||WindowsMaintenanceState.RestartPending!=false;
   if(requiresRestart&&conclusion.StartsWith("修复成功",StringComparison.Ordinal))
    conclusion="修复命令已完成，但 Windows 要求重启；重启后必须再次验证，当前不标记为最终修复成功。";

   progress?.Report(new(100,conclusion));
   var result=new RepairResult(action,finalExit,started,DateTimeOffset.UtcNow,report.ToString(),requiresRestart,conclusion);
   SaveResult(result);return result;
  }
  finally{Interlocked.Exchange(ref running,0);}
 }

 async Task<RepairResult> RunFullRepairAsync(IProgress<RepairProgress>? progress)
 {
  var started=DateTimeOffset.UtcNow;var report=new StringBuilder();
  log.Write("Repair","FullRepair","Started",detail:"DISM RestoreHealth -> DISM ScanHealth verify -> SFC scannow -> SFC verifyonly -> CHKDSK /scan");

  progress?.Report(new(2,"正在修复 Windows 组件存储"));
  var dism=await RunCommandAsync(RepairAction.ImageRestore,RepairCommands.Get(RepairAction.ImageRestore),2,42,progress).ConfigureAwait(false);
  report.AppendLine("=== DISM RESTOREHEALTH ===").AppendLine(dism.Output);
  if(dism.ExitCode!=0&&dism.ExitCode!=3010)
  {
   var text=IsDismSourceError(dism.Output,dism.ExitCode)
    ?"完整修复停止：DISM 无法获得匹配的微软修复源。请提供与当前 Windows 匹配的安装介质后再继续。"
    :$"完整修复停止：DISM /RestoreHealth 失败，错误码 {dism.ExitCode}。";
   return FinishFull(started,report,dism.ExitCode,false,text,progress);
  }
  if(dism.ExitCode==3010||WindowsMaintenanceState.RestartPending!=false)
   return FinishFull(started,report,dism.ExitCode,true,"Windows 映像处理完成，但需要重启后再继续系统文件修复与复检。",progress);

  progress?.Report(new(44,"正在独立验证 Windows 映像"));
  var image=await QueryImageHealthAsync(true).ConfigureAwait(false);
  report.AppendLine("=== DISM VERIFY ===").AppendLine(image.Raw);
  if(image.State!=0)return FinishFull(started,report,image.ExitCode,dism.ExitCode==3010,ImageHealthText(image.State,true),progress);

  progress?.Report(new(58,"正在修复受保护的 Windows 系统文件"));
  var sfc=await RunCommandAsync(RepairAction.SystemRepair,RepairCommands.Get(RepairAction.SystemRepair),58,79,progress).ConfigureAwait(false);
  report.AppendLine("=== SFC SCANNOW ===").AppendLine(sfc.Output);
  if(sfc.ExitCode is 3010 or 1641||WindowsMaintenanceState.RestartPending!=false)
   return FinishFull(started,report,sfc.ExitCode,true,"系统文件修复要求重启；重启后重新运行复检。",progress);

  progress?.Report(new(80,"正在独立验证系统文件"));
  var sfcVerify=await RunCommandAsync(RepairAction.SystemVerify,RepairCommands.Get(RepairAction.SystemVerify),80,91,progress).ConfigureAwait(false);
  report.AppendLine("=== SFC VERIFYONLY ===").AppendLine(sfcVerify.Output);
  var sfcConclusion=AnalyzeSfcVerify(sfcVerify.Output);
  if(sfcVerify.ExitCode!=0)
   return FinishFull(started,report,sfcVerify.ExitCode,dism.ExitCode==3010,$"完整修复未通过 SFC 二次验证：验证进程返回错误码 {sfcVerify.ExitCode}。{sfcConclusion}",progress);
  if(!sfcConclusion.StartsWith("系统状态良好",StringComparison.Ordinal))
   return FinishFull(started,report,sfcVerify.ExitCode,dism.ExitCode==3010,"完整修复未通过 SFC 二次验证："+sfcConclusion,progress);

  progress?.Report(new(92,"正在检查 C 盘文件系统"));
  var disk=await RunCommandAsync(RepairAction.DiskScan,RepairCommands.Get(RepairAction.DiskScan),92,98,progress).ConfigureAwait(false);
  report.AppendLine("=== CHKDSK SCAN ===").AppendLine(disk.Output);
  if(disk.ExitCode==1)
  {
   var verifyDisk=await RunCommandAsync(RepairAction.DiskScan,RepairCommands.Get(RepairAction.DiskScan),98,99,progress).ConfigureAwait(false);
   report.AppendLine("=== CHKDSK VERIFY ===").AppendLine(verifyDisk.Output);disk=verifyDisk;
  }
  if(disk.ExitCode!=0)return FinishFull(started,report,disk.ExitCode,dism.ExitCode==3010,"Windows 映像和系统文件已通过验证，但 "+AnalyzeDiskExit(disk.ExitCode,true),progress);

  var restart=dism.ExitCode==3010;
  var sfcActionFailed=sfc.ExitCode!=0;
  var conclusion=restart
   ?"DISM、SFC、CHKDSK 当前检查均已完成，但 Windows 要求重启。重启后再次运行“完整修复并验证”才能得到最终健康结论。"
   :sfcActionFailed
    ?$"最终验证正常：DISM ScanHealth=Healthy；SFC /verifyonly 无完整性冲突；CHKDSK C: /scan 退出码 0。但本轮 SFC /scannow 返回错误码 {sfc.ExitCode}，因此只确认当前系统文件最终状态健康，不把本轮 SFC 修复动作标记为成功。"
    :"修复成功，已验证：DISM ScanHealth=Healthy；SFC /verifyonly 无完整性冲突；CHKDSK C: /scan 复检退出码 0。";
  return FinishFull(started,report,disk.ExitCode,restart,conclusion,progress);
 }

 RepairResult FinishFull(DateTimeOffset started,StringBuilder report,int exitCode,bool restart,string conclusion,IProgress<RepairProgress>? progress)
 {
  restart|=exitCode is 3010 or 1641||WindowsMaintenanceState.RestartPending!=false;
  if(restart&&conclusion.StartsWith("修复成功",StringComparison.Ordinal))conclusion="检查已结束，但 Windows 需要重启；重启后复检才能确认最终状态。";
  progress?.Report(new(100,conclusion));
  var result=new RepairResult(RepairAction.FullRepair,exitCode,started,DateTimeOffset.UtcNow,report.ToString(),restart,conclusion);
  SaveResult(result);return result;
 }

 void SaveResult(RepairResult result)
 {
  log.Write("Repair",result.Action.ToString(),"Finished",detail:$"ExitCode={result.ExitCode}; conclusion={result.Conclusion}; elapsed={result.EndedUtc-result.StartedUtc}");
  try{File.WriteAllText(Path.Combine(log.DirectoryPath,$"{result.StartedUtc:yyyyMMdd-HHmmss}-repair-report.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException){log.Write("Repair",result.Action.ToString(),"ReportWriteFailed",detail:e.GetType().Name);}
 }


 async Task<(int ExitCode,string Output)> RunCommandAsync(RepairAction action,RepairCommand command,int startPercent,int endPercent,IProgress<RepairProgress>? progress)
 {
  var output=new StringBuilder();var sync=new object();int last=startPercent;
  var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),command.Executable);
  Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
  var encoding=action is RepairAction.SystemRepair or RepairAction.SystemVerify?Encoding.Unicode:Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
  using var process=new Process{StartInfo=new(){FileName=path,Arguments=command.Arguments,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=encoding,StandardErrorEncoding=encoding}};
  void Read(object sender,DataReceivedEventArgs e)
  {
   if(e.Data is null)return;lock(sync){output.AppendLine(e.Data);}var raw=TryPercent(e.Data);if(raw is null)return;
   var mapped=startPercent+(int)Math.Round((endPercent-startPercent)*Math.Clamp(raw.Value,0,100)/100d);mapped=Math.Clamp(mapped,startPercent,endPercent);
   if(mapped>last){last=mapped;progress?.Report(new(mapped,command.Title));}
  }
  process.OutputDataReceived+=Read;process.ErrorDataReceived+=Read;progress?.Report(new(startPercent,command.Title));
  process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();await process.WaitForExitAsync().ConfigureAwait(false);process.WaitForExit();
  string rawOutput;lock(sync){rawOutput=output.ToString();}return(process.ExitCode,rawOutput);
 }

 async Task<(int State,int ExitCode,string Raw)> QueryImageHealthAsync(bool scan)
 {
  var ps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
  var mode=scan?"ScanHealth":"CheckHealth";
  var script=$"$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Import-Module Dism; $r=Repair-WindowsImage -Online -{mode}; Write-Output ('CLEANC_HEALTH=' + [int]$r.ImageHealthState)";
  using var process=new Process{StartInfo=new(){FileName=ps,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8}};
  foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-Command",script})process.StartInfo.ArgumentList.Add(arg);
  process.Start();
  var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
  await process.WaitForExitAsync().ConfigureAwait(false);
  var raw=(await stdout.ConfigureAwait(false))+Environment.NewLine+(await stderr.ConfigureAwait(false));
  var m=Regex.Match(raw,@"CLEANC_HEALTH=(\d+)",RegexOptions.CultureInvariant);
  return process.ExitCode==0&&m.Success&&int.TryParse(m.Groups[1].Value,out var state)&&state is >=0 and <=2
   ?(state,process.ExitCode,raw):(99,process.ExitCode,raw);
 }

 static string ImageHealthText(int state,bool afterRepair)=>state switch
 {
  0=>afterRepair?"修复成功，已验证：Microsoft DISM 二次 ScanHealth 返回 Healthy。":"系统状态良好：Microsoft DISM ImageHealthState=Healthy。",
  1=>afterRepair?"修复未完全成功：DISM 二次验证仍为 Repairable。":"发现 Windows 组件存储损坏：ImageHealthState=Repairable，可执行映像修复。",
  2=>"DISM 报告组件存储 NonRepairable。继续重复 RestoreHealth 不能可靠解决，需要匹配的 Windows 安装介质、就地修复安装或系统恢复。",
  _=>"无法确认 Windows 映像状态：没有取得有效的 Microsoft ImageHealthState，CleanC 不会把未知结果标记为正常。"
 };

 static string AnalyzeDiskExit(int exitCode,bool verification)=>exitCode switch
 {
  0=>verification?"系统状态良好：C 盘文件系统复检通过，CHKDSK 官方退出码 0。":"系统状态良好：C 盘文件系统检查通过，CHKDSK 官方退出码 0。",
  1=>"CHKDSK 报告发现并处理了错误，需要再次扫描确认最终状态。",
  2=>"发现 C 盘文件系统问题：CHKDSK 官方退出码 2，在线扫描没有得到最终正常状态。",
  3=>"C 盘文件系统检查失败：CHKDSK 官方退出码 3，无法完成检查或错误未能修复。",
  _=>$"无法确认 C 盘文件系统状态：CHKDSK 返回未记录的退出码 {exitCode}。"
 };

 static bool IsDismSourceError(string output,int exitCode)
 {
  if(exitCode==unchecked((int)0x800F081F)||exitCode==unchecked((int)0x800F0906)||exitCode==unchecked((int)0x800F0954))return true;
  return Has(output,"0x800f081f","0x800f0906","0x800f0954","The source files could not be found","The source files could not be downloaded","找不到源文件","无法下载源文件","无法找到源文件");
 }

 static int? TryPercent(string line)
 {
  var m=Regex.Match(line,@"(?<!\d)(\d{1,3})(?:[\.,]\d+)?\s*%",RegexOptions.CultureInvariant);
  return m.Success&&int.TryParse(m.Groups[1].Value,out var value)?value:null;
 }
 static bool Has(string text,params string[] phrases)=>phrases.Any(p=>text.Contains(p,StringComparison.OrdinalIgnoreCase));
 static string AnalyzeCheck(RepairAction action,int exitCode,string output)
 {
  if(exitCode!=0)return $"检查失败：Windows 返回错误码 {exitCode}。";
  return action switch{
   RepairAction.SystemVerify=>AnalyzeSfcVerify(output),
   RepairAction.DiskScan=>AnalyzeDiskExit(exitCode,false),
   _=>"无法确认结果：该检查没有返回可独立验证的标准状态。"};
 }
 static string AnalyzeImage(string o)
 {
  if(Has(o,"No component store corruption detected","未检测到组件存储损坏","没有检测到组件存储损坏"))return "系统状态良好。";
  if(Has(o,"The component store is repairable","组件存储可以修复","组件存储可修复"))return "发现 Windows 映像组件存储损坏，建议执行“修复 Windows 映像”。";
  if(Has(o,"The component store cannot be repaired","组件存储无法修复","组件存储不可修复"))return "发现严重的 Windows 映像组件存储损坏，DISM 当前无法自动修复。";
  return "无法确认 Windows 映像状态：DISM 文本输出未匹配标准结果。";
 }
 static string AnalyzeSfcVerify(string o)
 {
  if(Has(o,"Windows Resource Protection did not find any integrity violations","Windows 资源保护未找到任何完整性冲突","Windows资源保护未找到任何完整性冲突","Windows 资源保护找不到任何完整性冲突","Windows资源保护找不到任何完整性冲突"))return "系统状态良好：SFC 未发现完整性冲突。";
  if(Has(o,"Windows Resource Protection could not perform the requested operation","Windows 资源保护无法执行请求的操作","Windows资源保护无法执行请求的操作"))return "SFC 无法执行请求的操作。微软建议在安全模式下运行，并检查 WinSxS\\Temp。";
  if(Has(o,"Windows Resource Protection found corrupt files but was unable to fix some of them","Windows 资源保护找到了损坏的文件但无法修复其中某些文件","Windows资源保护找到了损坏的文件但无法修复其中某些文件"))return "发现 Windows 系统文件损坏，并且 SFC 无法修复其中部分文件。";
  if(Has(o,"Windows Resource Protection found integrity violations","Windows 资源保护发现完整性冲突","Windows资源保护发现完整性冲突","found corrupt files","找到了损坏文件","发现损坏文件"))return "发现 Windows 系统文件完整性问题，建议执行“修复系统文件”。";
  return "无法确认 SFC 结果：Windows 没有返回 CleanC 能可靠识别的标准结论，因此不标记为系统正常。";
 }
 static string AnalyzeDisk(string o)
 {
  if(Has(o,"Windows has scanned the file system and found no problems","Windows 已扫描文件系统并且没有发现问题","Windows 已扫描文件系统，没有发现问题"))return "系统状态良好。";
  if(Has(o,"found problems","发现问题","found errors","发现错误","errors found","发现了错误"))return "发现 C 盘文件系统问题。";
  return "无法确认 C 盘文件系统状态：CHKDSK 文本输出未匹配标准结论。";
 }
 static string AnalyzeVerifiedRepair(RepairAction action,int verifyExit,string verifyOutput)
 {
  if(verifyExit!=0)return $"修复命令已执行，但独立验证失败（错误码 {verifyExit}），当前不标记为修复成功。";
  if(action==RepairAction.ImageRestore)return "无法确认映像修复结果：应使用 Microsoft ImageHealthState 进行验证。";
  var verify=AnalyzeSfcVerify(verifyOutput);
  return verify.StartsWith("系统状态良好",StringComparison.Ordinal)
   ?"修复成功，已验证：SFC /verifyonly 未发现完整性冲突。"
   :$"修复未通过独立验证：{verify}";
 }
}
