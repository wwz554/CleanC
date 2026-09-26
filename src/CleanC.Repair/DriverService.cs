using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CleanC.Core;
using CleanC.Logging;

namespace CleanC.Repair;

public enum DriverHealth { Normal, UpdateAvailable, Problem, Missing }
public sealed record DriverProgress(int Percent,string Stage);
public sealed record DriverUpdateCandidate(
 string UpdateId,string Title,string HardwareId,string Manufacturer,string Model,string Provider,string DriverClass,
 string Version,DateTimeOffset? DriverDate,bool RequiresRestart);
public sealed record DriverDevice(
 string DeviceId,string Name,string DeviceClass,string Manufacturer,string Provider,string DriverVersion,DateTimeOffset? DriverDate,
 string HardwareId,string InfName,bool IsSigned,int ProblemCode,string ProblemText,DriverUpdateCandidate? Update,string? OfficialSupportUrl,bool OfficialCheckSucceeded=true)
{
 public bool IsMissingDriver=>ProblemCode==28;
 public DriverHealth Health=>IsMissingDriver?DriverHealth.Missing:ProblemCode!=0?DriverHealth.Problem:Update is not null?DriverHealth.UpdateAvailable:DriverHealth.Normal;
 public string StatusText=>Health switch{DriverHealth.Missing=>"驱动缺失",DriverHealth.Problem=>"驱动异常",DriverHealth.UpdateAvailable=>"可升级",_=>"驱动正常"};
 public string ActionText=>Health switch{DriverHealth.Missing=>"安装",DriverHealth.Problem=>"修复安装",DriverHealth.UpdateAvailable=>"升级",_=>"驱动正常"};
 public bool CanAutoInstall=>Update is not null;
}
public sealed record DriverScanResult(DateTimeOffset StartedAt,DateTimeOffset EndedAt,IReadOnlyList<DriverDevice> Devices,int UpdateCount,int ProblemCount,int UnmatchedUpdateCount,bool OfficialCheckSucceeded=true,string? OfficialCheckWarning=null)
{
 public int NormalCount=>Devices.Count(x=>x.Health==DriverHealth.Normal);
 public int MissingCount=>Devices.Count(x=>x.Health==DriverHealth.Missing);
 public int RepairCount=>Devices.Count(x=>x.Health is DriverHealth.Missing or DriverHealth.Problem);
 public int UpgradeableCount=>Devices.Count(x=>x.Health==DriverHealth.UpdateAvailable);
}
public sealed record DriverInstallResult(bool Success,bool RequiresRestart,string Message,int ResultCode);
public sealed record DriverDownloadResult(bool Success,string Message,int ResultCode);
public sealed record DriverRollbackResult(bool Success,bool RequiresRestart,string Message,int ErrorCode);
public sealed record DriverBackupInfo(
 string DeviceId,string DeviceName,string HardwareId,string DriverVersion,string OriginalInfName,
 string BackupDirectory,string BackupInfPath,DateTimeOffset CreatedAt);
public sealed record DriverBackupResult(bool Success,DriverBackupInfo? Backup,string Message,int ErrorCode);


public sealed class DriverService(ICapabilityGate gate,AuditLog log)
{
 const string BackupProtectionMarker="CleanC-backup-protected.flag";
 readonly SemaphoreSlim scanGate=new(1,1);
 readonly SemaphoreSlim installGate=new(1,1);
 int activeTasks,activeInstalls;
 public bool IsRunning=>Volatile.Read(ref activeTasks)>0;
 public bool IsInstalling=>Volatile.Read(ref activeInstalls)>0;
 public bool IsScanning{get;private set;}

 sealed record RawDevice(string DeviceId,string Name,string DeviceClass,string Manufacturer,string Provider,string DriverVersion,DateTimeOffset? DriverDate,string HardwareId,IReadOnlyList<string> HardwareIds,IReadOnlyList<string> CompatibleIds,string InfName,bool IsSigned,int ProblemCode,string ProblemText);

 public async Task<DriverScanResult> ScanAsync(IProgress<DriverProgress>? progress=null,CancellationToken token=default)
 {
  gate.Demand(FeatureCapability.SystemRepair);
  if(IsRunning)throw new InvalidOperationException("已有驱动任务正在运行。");
  if(!await scanGate.WaitAsync(0,token).ConfigureAwait(false))throw new InvalidOperationException("驱动全面扫描已经在运行。");
  Interlocked.Increment(ref activeTasks);IsScanning=true;var started=DateTimeOffset.UtcNow;
  try
  {
   log.Write("Drivers","Scan","Started");
   var result=await Task.Run(()=>ScanCore(started,progress,token),token).ConfigureAwait(false);
   log.Write("Drivers","Scan","Completed",detail:$"devices={result.Devices.Count}; updates={result.UpdateCount}; problems={result.ProblemCount}; unmatched={result.UnmatchedUpdateCount}");
   return result;
  }
  catch(Exception e){log.Write("Drivers","Scan","Failed",detail:e.ToString());throw;}
  finally{IsScanning=false;Interlocked.Decrement(ref activeTasks);scanGate.Release();}
 }

 public async Task<DriverDownloadResult> DownloadAsync(string updateId,IProgress<DriverProgress>? progress=null,CancellationToken token=default)
 {
  if(string.IsNullOrWhiteSpace(updateId))throw new ArgumentException("缺少驱动更新标识。",nameof(updateId));
  gate.Demand(FeatureCapability.SystemRepair);
  Interlocked.Increment(ref activeTasks);
  try
  {
   log.Write("Drivers","Download","Started",updateId);
   var result=await Task.Run(()=>DownloadCore(updateId,progress,token),token).ConfigureAwait(false);
   log.Write("Drivers","Download",result.Success?"Completed":"Failed",updateId,result.Message);
   return result;
  }
  catch(Exception e){log.Write("Drivers","Download","Failed",updateId,e.ToString());throw;}
  finally{Interlocked.Decrement(ref activeTasks);}
 }

 public async Task<DriverInstallResult> InstallDownloadedAsync(string updateId,string deviceId,string deviceName,IProgress<DriverProgress>? progress=null,CancellationToken token=default)
 {
  if(string.IsNullOrWhiteSpace(updateId))throw new ArgumentException("缺少驱动更新标识。",nameof(updateId));
  gate.Demand(FeatureCapability.SystemRepair);
  if(!RepairService.IsAdministrator)throw new UnauthorizedAccessException("安装驱动需要管理员权限。");
  await installGate.WaitAsync(token).ConfigureAwait(false);
  Interlocked.Increment(ref activeTasks);Interlocked.Increment(ref activeInstalls);
  try
  {
   using var maintenance=MaintenanceLock.Enter();
   DemandNoPendingRestart();
   log.Write("Drivers","Install","Started",updateId);
   var result=await Task.Run(()=>InstallDownloadedCore(updateId,deviceId,deviceName,progress,token),token).ConfigureAwait(false);
   log.Write("Drivers","Install",result.Success?"Completed":"Failed",updateId,$"code={result.ResultCode}; restart={result.RequiresRestart}; {result.Message}");
   return result;
  }
  catch(Exception e){log.Write("Drivers","Install","Failed",updateId,e.ToString());throw;}
  finally{Interlocked.Decrement(ref activeInstalls);Interlocked.Decrement(ref activeTasks);installGate.Release();}
 }

 public async Task<DriverDevice?> VerifyDeviceAsync(DriverDevice previous,CancellationToken token=default)
 {
  gate.Demand(FeatureCapability.SystemRepair);
  Interlocked.Increment(ref activeTasks);
  try
  {
   var verified=await Task.Run(()=>
   {
    var raw=ReadLocalDrivers(token).FirstOrDefault(x=>x.DeviceId.Equals(previous.DeviceId,StringComparison.OrdinalIgnoreCase));
    if(raw is null)return null;
    DriverUpdateCandidate? update=previous.Update;var officialCheckSucceeded=true;
    try{update=BestUpdate(raw,SearchOfficialDriverUpdates(token));}
    catch(Exception e)when(e is InvalidOperationException or COMException or PlatformNotSupportedException)
    {
     // Online verification is unknown, not "no update". Preserve the last known official candidate.
     log.Write("Drivers","VerifyOfficialUpdate","Unavailable",raw.DeviceId,e.Message);
     officialCheckSucceeded=false;
    }
    return new DriverDevice(raw.DeviceId,raw.Name,raw.DeviceClass,raw.Manufacturer,raw.Provider,raw.DriverVersion,raw.DriverDate,raw.HardwareId,raw.InfName,raw.IsSigned,raw.ProblemCode,raw.ProblemText,update,OfficialSupportUrl(raw.Manufacturer,raw.Provider),officialCheckSucceeded);
   },token).ConfigureAwait(false);
   return verified;
  }
  finally{Interlocked.Decrement(ref activeTasks);}
 }

 public async Task<DriverBackupResult> BackupCurrentDriverAsync(DriverDevice device,IProgress<DriverProgress>? progress=null,CancellationToken token=default)
 {
  gate.Demand(FeatureCapability.SystemRepair);
  if(!RepairService.IsAdministrator)throw new UnauthorizedAccessException("备份驱动需要管理员权限。");
  await installGate.WaitAsync(token).ConfigureAwait(false);
  Interlocked.Increment(ref activeTasks);Interlocked.Increment(ref activeInstalls);
  try
  {
   using var maintenance=MaintenanceLock.Enter();
   log.Write("Drivers","Backup","Started",device.DeviceId,$"{device.DriverVersion}; {device.InfName}");
   var result=await Task.Run(()=>BackupCurrentDriverCore(device,progress,token),token).ConfigureAwait(false);
   log.Write("Drivers","Backup",result.Success?"Completed":"Failed",device.DeviceId,result.Message);
   return result;
  }
  catch(Exception e){log.Write("Drivers","Backup","Failed",device.DeviceId,e.ToString());throw;}
  finally{Interlocked.Decrement(ref activeInstalls);Interlocked.Decrement(ref activeTasks);installGate.Release();}
 }

 public DriverBackupInfo? FindLatestBackup(string deviceId)
 {
  try
  {
   if(!Directory.Exists(AppPaths.DriverBackups))return null;
   foreach(var metadata in Directory.EnumerateFiles(AppPaths.DriverBackups,"CleanC-driver-backup.json",SearchOption.AllDirectories)
    .OrderByDescending(File.GetLastWriteTimeUtc))
   {
    try
    {
     var info=JsonSerializer.Deserialize<DriverBackupInfo>(File.ReadAllText(metadata));
     if(info is null||!info.DeviceId.Equals(deviceId,StringComparison.OrdinalIgnoreCase))continue;
     var directory=Path.GetDirectoryName(metadata)!;
     var inf=File.Exists(info.BackupInfPath)?info.BackupInfPath:Directory.EnumerateFiles(directory,"*.inf",SearchOption.AllDirectories).FirstOrDefault();
     if(string.IsNullOrWhiteSpace(inf)||!File.Exists(inf))continue;
     if(Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Count()<2)continue;
     return info with{BackupDirectory=directory,BackupInfPath=inf};
    }
    catch{}
   }
  }
  catch{}
  return null;
 }

 public bool IsBackupProtected(DriverBackupInfo backup)
 {
  try
  {
   var folder=ValidatedBackupFolder(backup);
   return File.Exists(Path.Combine(folder,BackupProtectionMarker));
  }
  catch{return true;}
 }

 public void ProtectBackup(DriverBackupInfo backup,string reason)
 {
  var folder=ValidatedBackupFolder(backup);
  Directory.CreateDirectory(folder);
  File.WriteAllText(Path.Combine(folder,BackupProtectionMarker),
   $"Protected by CleanC\nDevice: {backup.DeviceName}\nVersion: {backup.DriverVersion}\nReason: {reason}\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n",Encoding.UTF8);
  WriteBackupStatus(folder,backup,$"受保护：{reason}\n此备份当前用于故障回退，不会进入安全清理。");
  log.Write("Drivers","BackupProtection","Protected",folder,reason);
 }

 public void ReleaseBackupProtection(DriverBackupInfo backup,string reason)
 {
  var folder=ValidatedBackupFolder(backup);
  var marker=Path.Combine(folder,BackupProtectionMarker);
  try{if(File.Exists(marker))File.Delete(marker);var pending=Path.Combine(folder,PendingRestartMarker);if(File.Exists(pending))File.Delete(pending);}catch(Exception e){log.Write("Drivers","BackupProtection","ReleaseFailed",folder,e.Message);return;}
  WriteBackupStatus(folder,backup,$"可清理：{reason}\n此备份已经不再承担当前回退保护，可在“安全清理”中由用户选择删除。");
  log.Write("Drivers","BackupProtection","Released",folder,reason);
 }

 const string PendingRestartMarker="CleanC-backup-reboot-pending.flag";
 public void MarkBackupPendingRestart(DriverBackupInfo backup,string reason)
 {
  var folder=ValidatedBackupFolder(backup);ProtectBackup(backup,reason);
  var boot=CurrentBootUtc();
  if(boot is null)throw new IOException("无法读取 Windows LastBootUpTime，旧驱动备份将继续保持保护。");
  File.WriteAllText(Path.Combine(folder,PendingRestartMarker),boot.Value.ToString("O"),Encoding.UTF8);
  WriteBackupStatus(folder,backup,$"受保护：{reason}\n必须在 Windows 真正重启后重新检测设备正常，才会解除回退保护。");
 }
 public bool TryReleasePendingRestartProtection(DriverBackupInfo backup,bool deviceHealthy,string reason)
 {
  if(!deviceHealthy)return false;var folder=ValidatedBackupFolder(backup);var marker=Path.Combine(folder,PendingRestartMarker);
  if(!File.Exists(marker))return false;
  try
  {
   if(!DateTimeOffset.TryParse(File.ReadAllText(marker).Trim(),out var beforeBoot))return false;
   var currentBoot=CurrentBootUtc();
   if(currentBoot is null)return false;
   if(Math.Abs((currentBoot.Value-beforeBoot).TotalMinutes)<2)return false;
   try{File.Delete(marker);}catch{return false;}
   ReleaseBackupProtection(backup,reason);return !IsBackupProtected(backup);
  }
  catch{return false;}
 }
 static DateTimeOffset? CurrentBootUtc()
 {
  object? locator=null,service=null,results=null;
  try
  {
   var type=Type.GetTypeFromProgID("WbemScripting.SWbemLocator");if(type is null)return null;
   dynamic l=Activator.CreateInstance(type)!;locator=l;dynamic svc=l.ConnectServer(".",@"root\cimv2");service=svc;
   dynamic rows=svc.ExecQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem");results=rows;
   foreach(var row in rows)
   {
    var value=SafeString(()=>row.LastBootUpTime);var parsed=ParseDmtfBoot(value);if(parsed is not null)return parsed.Value.ToUniversalTime();
   }
  }
  catch{}
  finally{ReleaseCom(results);ReleaseCom(service);ReleaseCom(locator);}
  return null;
 }

 string ValidatedBackupFolder(DriverBackupInfo backup)
 {
  var root=Path.GetFullPath(AppPaths.DriverBackups).TrimEnd('\\');
  var folder=Path.GetFullPath(backup.BackupDirectory).TrimEnd('\\');
  if(!SafetyPolicy.Within(folder,root)||string.Equals(folder,root,StringComparison.OrdinalIgnoreCase))
   throw new IOException("驱动备份目录不在 CleanC DriverBackups 子目录中。");
  return folder;
 }

 static void WriteBackupStatus(string folder,DriverBackupInfo backup,string status)
 {
  try
  {
   File.WriteAllText(Path.Combine(folder,"备份说明.txt"),
    $"CleanC 驱动备份\n设备：{backup.DeviceName}\n硬件 ID：{backup.HardwareId}\n版本：{backup.DriverVersion}\n原 INF：{backup.OriginalInfName}\n备份时间：{backup.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n\n{status}\n",Encoding.UTF8);
   var old=Path.Combine(folder,"请勿删除.txt");if(File.Exists(old))File.Delete(old);
  }
  catch{}
 }


 public async Task<DriverRollbackResult> RestoreBackupAsync(DriverBackupInfo backup,IProgress<DriverProgress>? progress=null,CancellationToken token=default)
 {
  gate.Demand(FeatureCapability.SystemRepair);
  if(!RepairService.IsAdministrator)throw new UnauthorizedAccessException("恢复备份驱动需要管理员权限。");
  await installGate.WaitAsync(token).ConfigureAwait(false);
  Interlocked.Increment(ref activeTasks);Interlocked.Increment(ref activeInstalls);
  try
  {
   using var maintenance=MaintenanceLock.Enter();
   DemandNoPendingRestart();
   log.Write("Drivers","BackupRestore","Started",backup.DeviceId,$"{backup.DriverVersion}; {backup.BackupInfPath}");
   var result=await Task.Run(()=>RestoreBackupCore(backup,progress,token),token).ConfigureAwait(false);
   log.Write("Drivers","BackupRestore",result.Success?"Completed":"Failed",backup.DeviceId,result.Message);
   return result;
  }
  catch(Exception e){log.Write("Drivers","BackupRestore","Failed",backup.DeviceId,e.ToString());throw;}
  finally{Interlocked.Decrement(ref activeInstalls);Interlocked.Decrement(ref activeTasks);installGate.Release();}
 }

 public async Task<DriverRollbackResult> RollbackAsync(string deviceId,string deviceName,IProgress<DriverProgress>? progress=null,CancellationToken token=default)
 {
  if(string.IsNullOrWhiteSpace(deviceId))throw new ArgumentException("缺少设备 ID。",nameof(deviceId));
  gate.Demand(FeatureCapability.SystemRepair);
  if(!RepairService.IsAdministrator)throw new UnauthorizedAccessException("回退驱动需要管理员权限。");
  await installGate.WaitAsync(token).ConfigureAwait(false);
  Interlocked.Increment(ref activeTasks);Interlocked.Increment(ref activeInstalls);
  try
  {
   using var maintenance=MaintenanceLock.Enter();
   DemandNoPendingRestart();
   log.Write("Drivers","Rollback","Started",deviceId,deviceName);
   var result=await Task.Run(()=>RollbackCore(deviceId,progress,token),token).ConfigureAwait(false);
   log.Write("Drivers","Rollback",result.Success?"Completed":"Failed",deviceId,result.Message);
   return result;
  }
  catch(Exception e){log.Write("Drivers","Rollback","Failed",deviceId,e.ToString());throw;}
  finally{Interlocked.Decrement(ref activeInstalls);Interlocked.Decrement(ref activeTasks);installGate.Release();}
 }


 DriverScanResult ScanCore(DateTimeOffset started,IProgress<DriverProgress>? progress,CancellationToken token)
 {
  progress?.Report(new(4,"正在读取本机硬件与已安装驱动"));
  var local=ReadLocalDrivers(token);
  token.ThrowIfCancellationRequested();
  progress?.Report(new(24,$"已识别 {local.Count:N0} 个硬件设备 · 正在检查官方驱动"));
  List<DriverUpdateCandidate> updates;string? warning=null;
  try{updates=SearchOfficialDriverUpdates(token);}
  catch(Exception e)when(e is InvalidOperationException or COMException or PlatformNotSupportedException)
  {
   updates=[];log.Write("Drivers","OfficialUpdateSearch","Unavailable",detail:e.Message);
   warning="已完成本机检测，但未能检查官方更新。请检查网络与 Windows Update 服务后重新扫描；不能据此判断驱动已是最新。";
   progress?.Report(new(58,$"已识别 {local.Count:N0} 个硬件设备 · 官方在线检查暂不可用"));
  }
  token.ThrowIfCancellationRequested();
  progress?.Report(new(74,"正在匹配硬件 ID、厂家与适用版本"));

  var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var devices=new List<DriverDevice>(local.Count);
  foreach(var item in local)
  {
   token.ThrowIfCancellationRequested();
   var update=BestUpdate(item,updates);
   if(update is not null)used.Add(update.UpdateId);
   devices.Add(new(item.DeviceId,item.Name,item.DeviceClass,item.Manufacturer,item.Provider,item.DriverVersion,item.DriverDate,item.HardwareId,item.InfName,item.IsSigned,item.ProblemCode,item.ProblemText,update,OfficialSupportUrl(item.Manufacturer,item.Provider)));
  }
  devices=devices.OrderBy(x=>x.Health==DriverHealth.Missing?0:x.Health==DriverHealth.Problem?1:x.Health==DriverHealth.UpdateAvailable?2:3).ThenBy(x=>x.DeviceClass,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToList();
  var updateCount=devices.Count(x=>x.Update is not null);var problemCount=devices.Count(x=>x.Health is DriverHealth.Problem or DriverHealth.Missing);var unmatched=updates.Count(x=>!used.Contains(x.UpdateId));
  progress?.Report(new(100,warning??$"扫描完成 · {devices.Count:N0} 个驱动 · {updateCount:N0} 个可更新"));
  return new(started,DateTimeOffset.UtcNow,devices,updateCount,problemCount,unmatched,warning is null,warning);
 }

 List<RawDevice> ReadLocalDrivers(CancellationToken token)
 {
  try
  {
   var viaPs=ReadLocalDriversPowerShell(token);
   if(viaPs.Count>0){log.Write("Drivers","LocalInventory","PowerShell",detail:$"{viaPs.Count} devices");return viaPs;}
   log.Write("Drivers","LocalInventory","PowerShellEmpty");
  }
  catch(Exception e)when(e is not OperationCanceledException){log.Write("Drivers","LocalInventory","PowerShellFallback",detail:e.Message);}
  token.ThrowIfCancellationRequested();
  var viaWmi=ReadLocalDriversWmi(token);
  if(viaWmi.Count>0){log.Write("Drivers","LocalInventory","WMI",detail:$"{viaWmi.Count} devices");return viaWmi;}
  throw new InvalidOperationException("没有枚举到任何 PnP 硬件。请确认 Windows Management Instrumentation (WMI) 服务正常后重试。");
 }

 List<RawDevice> ReadLocalDriversPowerShell(CancellationToken token)
 {
  const string script="""
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.Encoding]::UTF8
$drivers=@{}
Get-CimInstance Win32_PnPSignedDriver | ForEach-Object {
  if($_.DeviceID){$drivers[[string]$_.DeviceID]=$_}
}
$result=Get-CimInstance Win32_PnPEntity | Where-Object {
  $_.DeviceID -and $_.Name -and $_.PNPClass -notin @('SOFTWAREDEVICE','AUDIOENDPOINT','PRINTQUEUE')
} | ForEach-Object {
  $p=$_
  $d=$drivers[[string]$p.DeviceID]
  $date=''
  if($d -and $d.DriverDate){
    try{$date=([datetime]$d.DriverDate).ToString('o')}catch{$date=[string]$d.DriverDate}
  }
  [pscustomobject]@{
    DeviceId=[string]$p.DeviceID
    Name=[string]$p.Name
    DeviceClass=[string]$p.PNPClass
    Manufacturer=[string]$p.Manufacturer
    Provider=if($d){[string]$d.DriverProviderName}else{''}
    DriverVersion=if($d){[string]$d.DriverVersion}else{''}
    DriverDate=$date
    HardwareId=if($p.HardwareID){[string]($p.HardwareID|Select-Object -First 1)}elseif($d){[string]$d.HardWareID}else{''}
    HardwareIds=@($p.HardwareID | ForEach-Object {[string]$_})
    CompatibleIds=@($p.CompatibleID | ForEach-Object {[string]$_})
    InfName=if($d){[string]$d.InfName}else{''}
    IsSigned=if($d){[bool]$d.IsSigned}else{$false}
    ProblemCode=if($null -eq $p.ConfigManagerErrorCode){0}else{[int]$p.ConfigManagerErrorCode}
  }
}
@($result)|ConvertTo-Json -Compress -Depth 4
""";
  var encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
  var shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
  var psi=new ProcessStartInfo{FileName=shell,Arguments=$"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
  using var process=Process.Start(psi)??throw new InvalidOperationException("无法启动 PowerShell 硬件枚举。");
  var outputTask=process.StandardOutput.ReadToEndAsync();var errorTask=process.StandardError.ReadToEndAsync();
  WaitForReadOnlyProcess(process,token);
  var json=outputTask.GetAwaiter().GetResult();var stderr=errorTask.GetAwaiter().GetResult();
  if(process.ExitCode!=0)throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)?"PowerShell 硬件枚举失败。":stderr.Trim());
  if(string.IsNullOrWhiteSpace(json)||json.Trim()=="[]")return [];
  using var doc=JsonDocument.Parse(json);
  var list=new List<RawDevice>();
  foreach(var e in doc.RootElement.EnumerateArray())
  {
   token.ThrowIfCancellationRequested();
   string S(string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind!=JsonValueKind.Null?v.ToString():"";
   IReadOnlyList<string> SA(string name)
   {
    if(!e.TryGetProperty(name,out var v)||v.ValueKind==JsonValueKind.Null)return Array.Empty<string>();
    if(v.ValueKind==JsonValueKind.Array)return v.EnumerateArray().Select(x=>x.ToString()).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var one=v.ToString();return string.IsNullOrWhiteSpace(one)?Array.Empty<string>():new[]{one};
   }
   int I(string name)=>e.TryGetProperty(name,out var v)&&v.TryGetInt32(out var n)?n:0;
   bool B(string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False&&v.GetBoolean();
   var id=S("DeviceId");var name=S("Name");if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(name))continue;
   var code=I("ProblemCode");
   DateTimeOffset? date=DateTimeOffset.TryParse(S("DriverDate"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var parsed)?parsed:null;
   var hardwareId=S("HardwareId");var hardwareIds=SA("HardwareIds");if(hardwareIds.Count==0&&!string.IsNullOrWhiteSpace(hardwareId))hardwareIds=new[]{hardwareId};
   list.Add(new(id,name,S("DeviceClass"),S("Manufacturer"),S("Provider"),S("DriverVersion"),date,hardwareId,hardwareIds,SA("CompatibleIds"),S("InfName"),B("IsSigned"),code,ProblemText(code)));
  }
  return list;
 }

 List<RawDevice> ReadLocalDriversWmi(CancellationToken token)
 {
  var locatorType=Type.GetTypeFromProgID("WbemScripting.SWbemLocator")??throw new PlatformNotSupportedException("Windows WMI 服务不可用。");
  dynamic locator=Activator.CreateInstance(locatorType)!;dynamic? service=null;object? driverRows=null;object? pnpRows=null;
  try
  {
   service=locator.ConnectServer(".",@"root\cimv2");
   var problems=new Dictionary<string,(int Code,string Text,string Name,string Class,string Manufacturer,string HardwareId,IReadOnlyList<string> HardwareIds,IReadOnlyList<string> CompatibleIds)>(StringComparer.OrdinalIgnoreCase);
   pnpRows=service.ExecQuery("SELECT DeviceID,Name,Manufacturer,PNPClass,HardwareID,CompatibleID,ConfigManagerErrorCode FROM Win32_PnPEntity");
   foreach(var raw in (IEnumerable)pnpRows)
   {
    token.ThrowIfCancellationRequested();dynamic d=raw;var id=SafeString(()=>d.DeviceID);if(string.IsNullOrWhiteSpace(id))continue;var code=SafeInt(()=>d.ConfigManagerErrorCode);var hids=SafeStrings(()=>d.HardwareID);var cids=SafeStrings(()=>d.CompatibleID);var primary=hids.FirstOrDefault()??"";problems[id]=(code,ProblemText(code),SafeString(()=>d.Name),SafeString(()=>d.PNPClass),SafeString(()=>d.Manufacturer),primary,hids,cids);
   }
   var list=new List<RawDevice>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   driverRows=service.ExecQuery("SELECT DeviceID,DeviceName,DriverVersion,DriverDate,DriverProviderName,Manufacturer,IsSigned,InfName,DeviceClass,HardWareID FROM Win32_PnPSignedDriver");
   foreach(var raw in (IEnumerable)driverRows)
   {
    token.ThrowIfCancellationRequested();dynamic d=raw;
    var id=SafeString(()=>d.DeviceID);var name=SafeString(()=>d.DeviceName);if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(name)||!seen.Add(id))continue;
    var cls=SafeString(()=>d.DeviceClass);if(cls.Equals("SOFTWAREDEVICE",StringComparison.OrdinalIgnoreCase)||cls.Equals("AUDIOENDPOINT",StringComparison.OrdinalIgnoreCase)||cls.Equals("PRINTQUEUE",StringComparison.OrdinalIgnoreCase))continue;
    var hasState=problems.TryGetValue(id,out var state);var problem=hasState?(state.Code,state.Text):(0,"正常");
    var primary=hasState&&!string.IsNullOrWhiteSpace(state.HardwareId)?state.HardwareId:SafeString(()=>d.HardWareID);
    var hids=hasState&&state.HardwareIds.Count>0?state.HardwareIds:(string.IsNullOrWhiteSpace(primary)?Array.Empty<string>():new[]{primary});
    var cids=hasState?state.CompatibleIds:Array.Empty<string>();
    list.Add(new(id,name,cls,SafeString(()=>d.Manufacturer),SafeString(()=>d.DriverProviderName),SafeString(()=>d.DriverVersion),ParseDmtf(SafeString(()=>d.DriverDate)),primary,hids,cids,SafeString(()=>d.InfName),SafeBool(()=>d.IsSigned),problem.Item1,problem.Item2));
   }
   foreach(var pair in problems)
   {
    token.ThrowIfCancellationRequested();var d=pair.Value;if(d.Code==0||seen.Contains(pair.Key)||string.IsNullOrWhiteSpace(d.Name))continue;
    if(d.Class.Equals("SOFTWAREDEVICE",StringComparison.OrdinalIgnoreCase)||d.Class.Equals("AUDIOENDPOINT",StringComparison.OrdinalIgnoreCase)||d.Class.Equals("PRINTQUEUE",StringComparison.OrdinalIgnoreCase))continue;
    list.Add(new(pair.Key,d.Name,d.Class,d.Manufacturer,"","",null,d.HardwareId,d.HardwareIds,d.CompatibleIds,"",false,d.Code,d.Text));
   }
   return list;
  }
  finally{ReleaseCom(driverRows);ReleaseCom(pnpRows);ReleaseCom(service);ReleaseCom(locator);}
 }

 List<DriverUpdateCandidate> SearchOfficialDriverUpdates(CancellationToken token)
 {
  token.ThrowIfCancellationRequested();var sessionType=Type.GetTypeFromProgID("Microsoft.Update.Session")??throw new PlatformNotSupportedException("Windows Update Agent 不可用。");
  dynamic session=Activator.CreateInstance(sessionType)!;dynamic? searcher=null;object? result=null;
  try
  {
   session.ClientApplicationID="CleanC Driver Repair";searcher=session.CreateUpdateSearcher();searcher.Online=true;searcher.IncludePotentiallySupersededUpdates=false;
   result=searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");dynamic r=result;var list=new List<DriverUpdateCandidate>();
   if((int)r.ResultCode!=2)throw new InvalidOperationException($"官方驱动查询未完整成功（结果 {(int)r.ResultCode}），请重试。");
   for(var i=0;i<(int)r.Updates.Count;i++)
   {
    token.ThrowIfCancellationRequested();dynamic u=r.Updates.Item(i);
    var id=SafeString(()=>u.Identity.UpdateID);if(string.IsNullOrWhiteSpace(id))continue;
    var title=SafeString(()=>u.Title);var hardware=SafeString(()=>u.DriverHardwareID);var manufacturer=SafeString(()=>u.DriverManufacturer);var model=SafeString(()=>u.DriverModel);var provider=SafeString(()=>u.DriverProvider);var cls=SafeString(()=>u.DriverClass);var date=SafeDate(()=>u.DriverVerDate);var restart=SafeBool(()=>u.InstallationBehavior.RebootBehavior!=0);
    list.Add(new(id,title,hardware,manufacturer,model,provider,cls,ExtractVersion(title),date,restart));
   }
   return list;
  }
  catch(COMException e){throw new InvalidOperationException("Windows Update 官方驱动检查失败。请确认 Windows Update 服务和网络可用。",e);}
  finally{ReleaseCom(result);ReleaseCom(searcher);ReleaseCom(session);}
 }

 DriverBackupResult BackupCurrentDriverCore(DriverDevice requested,IProgress<DriverProgress>? progress,CancellationToken token)
 {
  progress?.Report(new(5,"正在确认更新前驱动"));
  token.ThrowIfCancellationRequested();
  var current=ReadLocalDrivers(token).FirstOrDefault(x=>x.DeviceId.Equals(requested.DeviceId,StringComparison.OrdinalIgnoreCase));
  if(current is null)return new(false,null,"没有重新枚举到当前设备，已取消升级。",1168);
  if(current.ProblemCode!=0)return new(false,null,$"当前设备已经处于异常状态（{current.ProblemText}），不把它作为健康版本备份。",current.ProblemCode);
  if(string.IsNullOrWhiteSpace(current.InfName)||!current.InfName.StartsWith("oem",StringComparison.OrdinalIgnoreCase))
   return new(false,null,"当前使用的是 Windows 内置驱动，PnPUtil 无法导出为可独立恢复的 OEM 驱动包。为了保证可恢复，CleanC 已取消这次自动升级。",259);
  if(string.IsNullOrWhiteSpace(current.HardwareId))
   return new(false,null,"没有读取到当前设备的硬件 ID，无法建立可验证的驱动备份，已取消升级。",87);

  progress?.Report(new(16,"正在备份更新前驱动"));
  Directory.CreateDirectory(AppPaths.DriverBackups);
  var englishName=SafeEnglishDriverName(current.Name,current.Provider,current.DeviceClass,current.DeviceId);
  var folder=Path.Combine(AppPaths.DriverBackups,$"{englishName}_{DateTime.Now:yyyyMMdd-HHmmss}");
  if(Directory.Exists(folder))folder+="_"+Guid.NewGuid().ToString("N")[..6];
  Directory.CreateDirectory(folder);
  File.WriteAllText(Path.Combine(folder,BackupProtectionMarker),
   $"Protected by CleanC\nState: upgrade-pending\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n",Encoding.UTF8);

  var pnputil=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"pnputil.exe");
  var psi=new ProcessStartInfo
  {
   FileName=pnputil,Arguments=$"/export-driver \"{current.InfName}\" \"{folder}\"",
   UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
   StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
  };
  using var process=Process.Start(psi);
  if(process is null){TryDeleteDirectory(folder);return new(false,null,"无法启动 Windows PnPUtil，已取消升级。",2);}
  var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
  WaitForReadOnlyProcess(process,token);
  var output=stdout.GetAwaiter().GetResult();var error=stderr.GetAwaiter().GetResult();
  if(process.ExitCode!=0)
  {
   TryDeleteDirectory(folder);
   var detail=string.IsNullOrWhiteSpace(error)?output:error;
   return new(false,null,$"原驱动导出失败，已取消升级。{(string.IsNullOrWhiteSpace(detail)?"":$" {detail.Trim()}")}",process.ExitCode);
  }

  var files=Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories).ToList();
  var inf=files.FirstOrDefault(x=>x.EndsWith(".inf",StringComparison.OrdinalIgnoreCase));
  var payloadCount=files.Count(x=>!x.EndsWith(".inf",StringComparison.OrdinalIgnoreCase)&&!DriverBackupIntegrity.IsMetadata(x));
  if(string.IsNullOrWhiteSpace(inf)||payloadCount==0)
  {
   TryDeleteDirectory(folder);
   return new(false,null,"Windows 报告驱动已导出，但备份包不完整（缺少 INF 或驱动载荷文件）。",2);
  }

  WriteBackupHashManifest(folder);
  var info=new DriverBackupInfo(current.DeviceId,current.Name,current.HardwareId,current.DriverVersion,current.InfName,folder,inf,DateTimeOffset.Now);
  File.WriteAllText(Path.Combine(folder,"CleanC-driver-backup.json"),JsonSerializer.Serialize(info,new JsonSerializerOptions{WriteIndented=true}),Encoding.UTF8);
  WriteBackupStatus(folder,info,"受保护：新驱动尚未安装并完成验证。\n安装成功且检测正常后会自动转为“备份的驱动 · 可清理”。");
  progress?.Report(new(100,"原驱动备份完成"));
  return new(true,info,$"已备份更新前驱动 {current.DriverVersion}。",0);
 }

 DriverRollbackResult RestoreBackupCore(DriverBackupInfo backup,IProgress<DriverProgress>? progress,CancellationToken token)
 {
  progress?.Report(new(6,"正在验证本地驱动备份"));
  token.ThrowIfCancellationRequested();
  var backupRoot=Path.GetFullPath(AppPaths.DriverBackups).TrimEnd('\\');
  var directory=Path.GetFullPath(backup.BackupDirectory).TrimEnd('\\');
  var inf=Path.GetFullPath(backup.BackupInfPath);
  if(!SafetyPolicy.Within(directory,backupRoot)||directory.Equals(backupRoot,StringComparison.OrdinalIgnoreCase)||!SafetyPolicy.Within(inf,directory))
   return new(false,false,"备份路径不在 CleanC DriverBackups 目录中，拒绝恢复。",5);
  if(!Directory.Exists(directory)||!File.Exists(inf))
   return new(false,false,"更新前驱动备份已经不存在，无法恢复。",2);
  if(string.IsNullOrWhiteSpace(backup.HardwareId))
   return new(false,false,"驱动备份缺少硬件 ID，无法安全恢复。",87);
  if(!DriverBackupIntegrity.Verify(directory,inf,out var integrityError))
   return new(false,false,"驱动备份完整性校验失败："+integrityError,13);

  var allCurrent=ReadLocalDrivers(token);
  var current=allCurrent.FirstOrDefault(x=>x.DeviceId.Equals(backup.DeviceId,StringComparison.OrdinalIgnoreCase));
  if(current is null)return new(false,false,"没有找到原设备，无法恢复备份驱动。",1168);
  var expected=NormalizeHardware(backup.HardwareId);var currentIds=new HashSet<string>(current.HardwareIds.Select(NormalizeHardware).Where(x=>!string.IsNullOrWhiteSpace(x)),StringComparer.OrdinalIgnoreCase);
  if(string.IsNullOrWhiteSpace(expected)||!currentIds.Contains(expected))
   return new(false,false,"当前设备硬件 ID 列表与备份不一致，已拒绝强制恢复。",87);
  var sameHardware=allCurrent.Where(x=>x.HardwareIds.Select(NormalizeHardware).Any(id=>id.Equals(expected,StringComparison.OrdinalIgnoreCase))).ToList();
  if(sameHardware.Count!=1||!sameHardware[0].DeviceId.Equals(backup.DeviceId,StringComparison.OrdinalIgnoreCase))
   return new(false,false,"当前存在多个相同硬件 ID 的设备实例。Windows UpdateDriverForPlugAndPlayDevices 会作用于匹配该硬件 ID 的设备，因此 CleanC 已拒绝自动强制恢复；请在设备管理器中针对目标设备手动恢复。",87);

  progress?.Report(new(22,$"正在恢复 {backup.DriverVersion}"));
  var ok=UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero,backup.HardwareId,inf,InstallFlagForce,out var reboot);
  if(!ok)
  {
   var error=Marshal.GetLastWin32Error();
   var message=error switch
   {
    5=>"Windows 拒绝安装备份驱动，请确认 CleanC 以管理员身份运行。",
    2=>"备份驱动 INF 或其依赖文件不存在。",
    259=>"Windows 没有把备份驱动应用到目标硬件。",
    433=>"目标设备当前未连接。",
    _=>$"恢复更新前驱动失败（Win32 {error}）。"
   };
   return new(false,false,message,error);
  }
  progress?.Report(new(100,reboot?"已恢复原驱动 · 需要重启":"已恢复原驱动"));
  return new(true,reboot,reboot
   ?$"已强制恢复更新前版本 {backup.DriverVersion}，需要重启后完全生效。"
   :$"已恢复到更新前版本 {backup.DriverVersion}。",0);
 }

 static string SafeEnglishDriverName(string name,string provider,string deviceClass,string deviceId)
 {
  static string Clean(string value)
  {
   var chars=(value??"").Where(c=>c<=127&&(char.IsLetterOrDigit(c)||c is ' ' or '-' or '_' or '.')).ToArray();
   var text=new string(chars).Trim();
   while(text.Contains("  ",StringComparison.Ordinal))text=text.Replace("  "," ",StringComparison.Ordinal);
   return text.Replace(' ','_');
  }
  var candidate=Clean(name);
  if(candidate.Length<3)candidate=Clean($"{provider}_{deviceClass}");
  if(candidate.Length<3)candidate="Driver_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..8];
  return candidate.Length>72?candidate[..72]:candidate;
 }
 static void TryDeleteDirectory(string path){try{if(Directory.Exists(path))Directory.Delete(path,true);}catch{}}

 DriverRollbackResult RollbackCore(string deviceId,IProgress<DriverProgress>? progress,CancellationToken token)
 {
  progress?.Report(new(8,"正在定位设备"));
  token.ThrowIfCancellationRequested();
  var set=SetupDiGetClassDevsW(IntPtr.Zero,null,IntPtr.Zero,DigcfPresent|DigcfAllClasses);
  if(set==InvalidHandleValue)throw new InvalidOperationException($"无法打开设备信息集（Win32 {Marshal.GetLastWin32Error()}）。");
  try
  {
   for(uint index=0;;index++)
   {
    token.ThrowIfCancellationRequested();
    var data=new SP_DEVINFO_DATA{cbSize=(uint)Marshal.SizeOf<SP_DEVINFO_DATA>()};
    if(!SetupDiEnumDeviceInfo(set,index,ref data))
    {
     var enumError=Marshal.GetLastWin32Error();
     if(enumError==ErrorNoMoreItems)break;
     throw new InvalidOperationException($"枚举设备失败（Win32 {enumError}）。");
    }
    var id=new StringBuilder(1024);
    if(!SetupDiGetDeviceInstanceIdW(set,ref data,id,id.Capacity,out _))continue;
    if(!id.ToString().Equals(deviceId,StringComparison.OrdinalIgnoreCase))continue;
    progress?.Report(new(28,"正在回退到 Windows 备份驱动"));
    var ok=DiRollbackDriver(set,ref data,IntPtr.Zero,RollbackFlagNoUi,out var reboot);
    if(ok)
    {
     progress?.Report(new(100,reboot?"回退完成 · 需要重启":"回退完成"));
     return new(true,reboot,reboot?"驱动已回退到 Windows 保存的上一版本，需要重启后完全生效。":"驱动已回退到 Windows 保存的上一版本。",0);
    }
    var error=Marshal.GetLastWin32Error();
    var message=error switch
    {
     ErrorNoMoreItems=>"Windows 没有为这个设备保留可回退的上一版驱动。",
     5=>"Windows 拒绝回退驱动，请确认 CleanC 以管理员身份运行。",
     _=>$"驱动回退失败（Win32 {error}）。"
    };
    return new(false,false,message,error);
   }
   return new(false,false,"没有找到需要回退的设备实例。",ErrorNoMoreItems);
  }
  finally{SetupDiDestroyDeviceInfoList(set);}
 }

 DriverDownloadResult DownloadCore(string updateId,IProgress<DriverProgress>? progress,CancellationToken token)
 {
  progress?.Report(new(4,"正在确认官方驱动包"));
  token.ThrowIfCancellationRequested();
  var sessionType=Type.GetTypeFromProgID("Microsoft.Update.Session")??throw new PlatformNotSupportedException("Windows Update Agent 不可用。");
  dynamic session=Activator.CreateInstance(sessionType)!;dynamic? searcher=null;object? result=null;dynamic? collection=null;dynamic? downloader=null;
  try
  {
   session.ClientApplicationID="CleanC Driver Repair";
   searcher=session.CreateUpdateSearcher();searcher.Online=true;
   result=searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
   dynamic r=result;dynamic? target=null;
   if((int)r.ResultCode!=2)return new(false,"官方查询未完整成功，已停止下载，请重新扫描。",(int)r.ResultCode);
   for(var i=0;i<(int)r.Updates.Count;i++)
   {
    token.ThrowIfCancellationRequested();
    dynamic u=r.Updates.Item(i);
    if(SafeString(()=>u.Identity.UpdateID).Equals(updateId,StringComparison.OrdinalIgnoreCase)){target=u;break;}
   }
   if(target is null)return new(false,"该驱动更新已经不再适用。",0);
   try{if(!SafeBool(()=>target.EulaAccepted))target.AcceptEula();}catch{}
   if(SafeBool(()=>target.IsDownloaded)){progress?.Report(new(100,"下载完成"));return new(true,"官方驱动包已就绪。",2);}
   var collType=Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")??throw new PlatformNotSupportedException("Windows Update 集合组件不可用。");
   collection=Activator.CreateInstance(collType)!;collection.Add(target);
   progress?.Report(new(18,"正在下载官方驱动包"));
   downloader=session.CreateUpdateDownloader();downloader.Updates=collection;
   var download=downloader.Download();var code=SafeInt(()=>download.ResultCode);
   dynamic? perUpdate=null;var perCode=0;var perHResult=unchecked((int)0x80004005);var overallHResult=unchecked((int)0x80004005);
   try{overallHResult=Convert.ToInt32(download.HResult,CultureInfo.InvariantCulture);perUpdate=download.GetUpdateResult(0);perCode=Convert.ToInt32(perUpdate.ResultCode,CultureInfo.InvariantCulture);perHResult=Convert.ToInt32(perUpdate.HResult,CultureInfo.InvariantCulture);}catch{}
   var ok=DriverOperationStatus.IsSuccess(code,perCode,perHResult,overallHResult);
   progress?.Report(new(ok?100:0,ok?"下载完成":"下载失败"));
   var resultCode=ok?2:(perCode!=0?perCode:code);
   return new(ok,ok?"官方驱动包下载完成。":code==3||perCode==3?$"驱动下载部分完成但包含错误（overall={code}, update={perCode}, hresult=0x{perHResult:X8}）。":$"驱动下载失败（overall={code}, update={perCode}, hresult=0x{perHResult:X8}）。",resultCode);
  }
  catch(COMException e){throw new InvalidOperationException("Windows Update 驱动下载失败。请确认 Windows Update 服务和网络可用。",e);}
  finally{ReleaseCom(downloader);ReleaseCom(collection);ReleaseCom(result);ReleaseCom(searcher);ReleaseCom(session);}
 }

 DriverInstallResult InstallDownloadedCore(string updateId,string deviceId,string deviceName,IProgress<DriverProgress>? progress,CancellationToken token)
 {
  progress?.Report(new(3,"正在确认已下载驱动"));
  token.ThrowIfCancellationRequested();
  var sessionType=Type.GetTypeFromProgID("Microsoft.Update.Session")??throw new PlatformNotSupportedException("Windows Update Agent 不可用。");
  dynamic session=Activator.CreateInstance(sessionType)!;dynamic? searcher=null;object? result=null;dynamic? collection=null;dynamic? installer=null;
  try
  {
   session.ClientApplicationID="CleanC Driver Repair";
   searcher=session.CreateUpdateSearcher();searcher.Online=true;
   result=searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
   dynamic r=result;dynamic? target=null;
   if((int)r.ResultCode!=2)return new(false,false,"官方查询未完整成功，已停止安装，请重新扫描。",(int)r.ResultCode);
   for(var i=0;i<(int)r.Updates.Count;i++)
   {
    token.ThrowIfCancellationRequested();dynamic u=r.Updates.Item(i);
    if(SafeString(()=>u.Identity.UpdateID).Equals(updateId,StringComparison.OrdinalIgnoreCase)){target=u;break;}
   }
   if(target is null)return new(false,false,"该驱动更新已经不再适用。",0);
   if(!SafeBool(()=>target.IsDownloaded))return new(false,false,"驱动包尚未下载完成。",0);
   var currentDevice=ReadLocalDrivers(token).FirstOrDefault(x=>x.DeviceId.Equals(deviceId,StringComparison.OrdinalIgnoreCase));
   if(currentDevice is null)return new(false,false,"安装前没有重新枚举到目标设备，已取消安装并要求重新扫描。",1168);
   var updateHardware=NormalizeHardware(SafeString(()=>target.DriverHardwareID));
   var deviceHardware=new HashSet<string>(currentDevice.HardwareIds.Concat(currentDevice.CompatibleIds).Select(NormalizeHardware).Where(x=>!string.IsNullOrWhiteSpace(x)),StringComparer.OrdinalIgnoreCase);
   if(string.IsNullOrWhiteSpace(updateHardware)||!deviceHardware.Contains(updateHardware))
    return new(false,false,"安装前硬件 ID 复核失败：Windows Update 驱动已不再与当前目标设备匹配，请重新扫描驱动。",87);
   progress?.Report(new(12,"正在创建系统还原点"));
   var restorePoint=CreateRestorePointBestEffort();log.Write("Drivers","RestorePoint",restorePoint?"Ready":"Unavailable",detail:"驱动安装前保护");
   var collType=Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")??throw new PlatformNotSupportedException("Windows Update 集合组件不可用。");
   collection=Activator.CreateInstance(collType)!;collection.Add(target);
   progress?.Report(new(28,"正在安装驱动"));
   installer=session.CreateUpdateInstaller();installer.Updates=collection;
   var installed=installer.Install();var code=SafeInt(()=>installed.ResultCode);var restart=true;
   dynamic? perUpdate=null;var perCode=0;var perHResult=unchecked((int)0x80004005);var overallHResult=unchecked((int)0x80004005);var perRestart=false;
   try{restart=Convert.ToBoolean(installed.RebootRequired,CultureInfo.InvariantCulture);overallHResult=Convert.ToInt32(installed.HResult,CultureInfo.InvariantCulture);perUpdate=installed.GetUpdateResult(0);perCode=Convert.ToInt32(perUpdate.ResultCode,CultureInfo.InvariantCulture);perHResult=Convert.ToInt32(perUpdate.HResult,CultureInfo.InvariantCulture);perRestart=Convert.ToBoolean(perUpdate.RebootRequired,CultureInfo.InvariantCulture);}catch{perHResult=unchecked((int)0x80004005);perRestart=true;}
   restart|=perRestart;
   var success=DriverOperationStatus.IsSuccess(code,perCode,perHResult,overallHResult);string packageNote="";
   if(success)
   {
    progress?.Report(new(90,"正在保存驱动安装程序副本"));
    var exported=ExportInstalledDriverPackageBestEffort(deviceId,deviceName,token);
    if(exported is not null)packageNote=$"\n驱动安装程序副本已保存到：{exported}";
   }
   progress?.Report(new(100,success?(restart?"安装完成 · 需要重启":"安装完成"):code==3?"安装部分完成":"安装失败"));
   var resultCode=success?2:(perCode!=0?perCode:code);
   return new(success,restart,success?((restart?"驱动安装成功，需要重新启动 Windows 后生效。":"驱动安装成功。")+packageNote):code==3||perCode==3?$"Windows Update 报告驱动安装部分完成但包含错误（overall={code}, update={perCode}, hresult=0x{perHResult:X8}）。":$"驱动安装失败（overall={code}, update={perCode}, hresult=0x{perHResult:X8}）。",resultCode);
  }
  catch(COMException e){throw new InvalidOperationException("Windows Update 驱动安装失败。请确认 Windows Update 服务正常。",e);}
  finally{ReleaseCom(installer);ReleaseCom(collection);ReleaseCom(result);ReleaseCom(searcher);ReleaseCom(session);}
 }

 static void WriteBackupHashManifest(string folder)
 {
  var manifest=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))
  {
   var name=Path.GetFileName(file);
   if(DriverBackupIntegrity.IsMetadata(file))continue;
   var relative=Path.GetRelativePath(folder,file);
   using var stream=File.OpenRead(file);manifest[relative]=Convert.ToHexString(SHA256.HashData(stream));
  }
  File.WriteAllText(Path.Combine(folder,"CleanC-driver-hashes.json"),JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}),Encoding.UTF8);
 }
 static bool VerifyBackupHashManifest(string folder,out string error)
 {
  error="";
  try
  {
   var path=Path.Combine(folder,"CleanC-driver-hashes.json");if(!File.Exists(path)){error="缺少 SHA-256 清单";return false;}
   var manifest=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path))??new();if(manifest.Count==0){error="SHA-256 清单为空";return false;}
   foreach(var pair in manifest)
   {
    var file=Path.GetFullPath(Path.Combine(folder,pair.Key));if(!SafetyPolicy.Within(file,folder)||!File.Exists(file)){error="备份文件缺失："+pair.Key;return false;}
    using var stream=File.OpenRead(file);var actual=Convert.ToHexString(SHA256.HashData(stream));if(!actual.Equals(pair.Value,StringComparison.OrdinalIgnoreCase)){error="备份文件已变化："+pair.Key;return false;}
   }
   return true;
  }
  catch(Exception e){error=e.Message;return false;}
 }

 string? ExportInstalledDriverPackageBestEffort(string deviceId,string deviceName,CancellationToken token)
 {
  try
  {
   var device=ReadLocalDriversPowerShell(token).FirstOrDefault(x=>x.DeviceId.Equals(deviceId,StringComparison.OrdinalIgnoreCase));
   if(device is null||string.IsNullOrWhiteSpace(device.InfName)||!device.InfName.StartsWith("oem",StringComparison.OrdinalIgnoreCase))return null;
   Directory.CreateDirectory(AppPaths.DriverPackages);
   var folder=Path.Combine(AppPaths.DriverPackages,$"{DateTime.Now:yyyyMMdd-HHmmss}_{SafeFileName(deviceName)}_{SafeFileName(device.DriverVersion)}");
   Directory.CreateDirectory(folder);
   var pnputil=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"pnputil.exe");
   var psi=new ProcessStartInfo{FileName=pnputil,Arguments=$"/export-driver \"{device.InfName}\" \"{folder}\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   using var process=Process.Start(psi);if(process is null)return null;
   var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
   WaitForReadOnlyProcess(process,token);stdout.GetAwaiter().GetResult();stderr.GetAwaiter().GetResult();
   if(process.ExitCode!=0){try{Directory.Delete(folder,true);}catch{}return null;}
   File.WriteAllText(Path.Combine(folder,"CleanC-驱动安装程序.txt"),$"设备：{device.Name}\n厂商：{device.Manufacturer}\n提供方：{device.Provider}\n版本：{device.DriverVersion}\nINF：{device.InfName}\n保存时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n这是已安装驱动包的副本，可以在 CleanC 安全清理中删除。\n");
   log.Write("Drivers","PackageExport","Completed",folder,$"{device.InfName}; {device.DriverVersion}");
   return folder;
  }
  catch(Exception e){log.Write("Drivers","PackageExport","Ignored",deviceId,e.Message);return null;}
 }
 static string SafeFileName(string value)
 {
  var invalid=Path.GetInvalidFileNameChars();var chars=(value??"driver").Select(c=>invalid.Contains(c)?'_':c).ToArray();var text=new string(chars).Trim();return string.IsNullOrWhiteSpace(text)?"driver":text.Length>60?text[..60]:text;
 }
 static void DemandNoPendingRestart()
 {
  if(WindowsMaintenanceState.RestartPending!=false)throw new InvalidOperationException("Windows 待重启或无法确认重启状态，请先重启后再安装/回退驱动。");
 }
 static void WaitForReadOnlyProcess(Process process,CancellationToken token)
 {
  var watch=Stopwatch.StartNew();
  try
  {
   while(!process.WaitForExit(150))
   {
    token.ThrowIfCancellationRequested();
    if(watch.Elapsed>TimeSpan.FromMinutes(5))throw new TimeoutException("硬件枚举或驱动导出超时，未继续安装。");
   }
  }
  catch
  {
   try{if(!process.HasExited){process.Kill(entireProcessTree:true);process.WaitForExit(5000);}}catch{}
   throw;
  }
 }

 static DriverUpdateCandidate? BestUpdate(RawDevice device,IReadOnlyList<DriverUpdateCandidate> updates)
 {
  // Windows driver matching is identifier based. Device display names are never an eligibility signal.
  var hardware=new HashSet<string>(device.HardwareIds.Select(NormalizeHardware).Where(x=>!string.IsNullOrWhiteSpace(x)),StringComparer.OrdinalIgnoreCase);
  if(hardware.Count==0&&!string.IsNullOrWhiteSpace(device.HardwareId))hardware.Add(NormalizeHardware(device.HardwareId));
  var compatible=new HashSet<string>(device.CompatibleIds.Select(NormalizeHardware).Where(x=>!string.IsNullOrWhiteSpace(x)),StringComparer.OrdinalIgnoreCase);
  DriverUpdateCandidate? best=null;var bestRank=0;var bestTie=-1;
  foreach(var update in updates)
  {
   var id=NormalizeHardware(update.HardwareId);if(string.IsNullOrWhiteSpace(id))continue;
   var rank=hardware.Contains(id)?2:compatible.Contains(id)?1:0;if(rank==0)continue;
   var tie=(device.DeviceClass.Equals(update.DriverClass,StringComparison.OrdinalIgnoreCase)?2:0)+(Similar(device.Manufacturer,update.Manufacturer)||Similar(device.Provider,update.Provider)?1:0);
   if(rank>bestRank||(rank==bestRank&&tie>bestTie)){best=update;bestRank=rank;bestTie=tie;}
  }
  return best;
 }

 static bool Similar(string a,string b)
 {
  if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b))return false;var x=NormalizeText(a);var y=NormalizeText(b);if(x.Length<4||y.Length<4)return false;return x.Contains(y,StringComparison.OrdinalIgnoreCase)||y.Contains(x,StringComparison.OrdinalIgnoreCase);
 }
 static string NormalizeText(string s)=>Regex.Replace(s.ToUpperInvariant(),@"[^A-Z0-9]+"," ").Trim();
 static string NormalizeHardware(string s)=>Regex.Replace((s??"").ToUpperInvariant(),@"\s+","");
 static string ExtractVersion(string title){var m=Regex.Matches(title??"",@"\b\d+(?:\.\d+){2,}\b");return m.Count==0?"":m[m.Count-1].Value;}
 static string ProblemText(int code)=>code switch{0=>"正常",1=>"设备配置不正确",10=>"设备无法启动",18=>"需要重新安装驱动",22=>"设备已禁用",28=>"未安装驱动",31=>"Windows 无法加载驱动",43=>"Windows 已停止该设备",_=>$"设备管理器错误代码 {code}"};
 static bool CreateRestorePointBestEffort()
 {
  object? locator=null,service=null,restore=null;
  try
  {
   var type=Type.GetTypeFromProgID("WbemScripting.SWbemLocator");if(type is null)return false;dynamic l=Activator.CreateInstance(type)!;locator=l;dynamic s=l.ConnectServer(".",@"root\default");service=s;dynamic r=s.Get("SystemRestore");restore=r;var code=SafeInt(()=>r.CreateRestorePoint("CleanC 驱动更新",10,100));return code==0;
  }
  catch{return false;}
  finally{ReleaseCom(restore);ReleaseCom(service);ReleaseCom(locator);}
 }
 static string? OfficialSupportUrl(string manufacturer,string provider)
 {
  var s=(manufacturer+" "+provider).ToUpperInvariant();
  if(s.Contains("LENOVO"))return "https://support.lenovo.com/";
  if(s.Contains("NVIDIA"))return "https://www.nvidia.com/Download/index.aspx";
  if(s.Contains("INTEL"))return "https://www.intel.com/content/www/us/en/support/detect.html";
  if(s.Contains("ADVANCED MICRO DEVICES")||Regex.IsMatch(s,@"\bAMD\b"))return "https://www.amd.com/en/support/download/drivers.html";
  if(s.Contains("REALTEK"))return "https://www.realtek.com/Download/Index";
  if(s.Contains("DELL"))return "https://www.dell.com/support/home/drivers";
  if(s.Contains("HEWLETT")||Regex.IsMatch(s,@"\bHP\b"))return "https://support.hp.com/drivers";
  if(s.Contains("ASUSTEK")||s.Contains("ASUS"))return "https://www.asus.com/support/download-center/";
  if(s.Contains("ACER"))return "https://www.acer.com/support/drivers-and-manuals";
  if(s.Contains("MICRO-STAR")||Regex.IsMatch(s,@"\bMSI\b"))return "https://www.msi.com/support/download";
  if(s.Contains("QUALCOMM"))return "https://www.qualcomm.com/support";
  if(s.Contains("MICROSOFT"))return "https://support.microsoft.com/windows";
  return null;
 }

 const uint DigcfPresent=0x00000002,DigcfAllClasses=0x00000004,RollbackFlagNoUi=0x00000001,InstallFlagForce=0x00000001;
 const int ErrorNoMoreItems=259;
 static readonly IntPtr InvalidHandleValue=new(-1);
 [StructLayout(LayoutKind.Sequential)]
 struct SP_DEVINFO_DATA
 {
  public uint cbSize;public Guid ClassGuid;public uint DevInst;public UIntPtr Reserved;
 }
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid,string? enumerator,IntPtr hwndParent,uint flags);
 [DllImport("setupapi.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet,uint memberIndex,ref SP_DEVINFO_DATA deviceInfoData);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet,ref SP_DEVINFO_DATA deviceInfoData,StringBuilder deviceInstanceId,int deviceInstanceIdSize,out int requiredSize);
 [DllImport("setupapi.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
 [DllImport("newdev.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool DiRollbackDriver(IntPtr deviceInfoSet,ref SP_DEVINFO_DATA deviceInfoData,IntPtr hwndParent,uint flags,[MarshalAs(UnmanagedType.Bool)] out bool needReboot);
 [DllImport("newdev.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent,string hardwareId,string fullInfPath,uint installFlags,[MarshalAs(UnmanagedType.Bool)] out bool needReboot);


 static DateTimeOffset? ParseDmtfBoot(string value)
 {
  try
  {
   if(value.Length<22)return null;
   var dt=DateTime.ParseExact(value[..14],"yyyyMMddHHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None);
   var sign=value[21]=='-'?-1:1;
   var minutes=int.Parse(value.Substring(22,3),CultureInfo.InvariantCulture)*sign;
   return new DateTimeOffset(DateTime.SpecifyKind(dt,DateTimeKind.Unspecified),TimeSpan.FromMinutes(minutes));
  }
  catch{return null;}
 }
 static string SafeString(Func<object?> get){try{var v=get();if(v is null)return "";if(v is Array a&&a.Length>0)return Convert.ToString(a.GetValue(0),CultureInfo.InvariantCulture)??"";return Convert.ToString(v,CultureInfo.InvariantCulture)??"";}catch{return "";}}
 static IReadOnlyList<string> SafeStrings(Func<object?> get)
 {
  try
  {
   var v=get();if(v is null)return Array.Empty<string>();
   if(v is IEnumerable seq&&v is not string){var values=new List<string>();foreach(var item in seq){var text=Convert.ToString(item,CultureInfo.InvariantCulture);if(!string.IsNullOrWhiteSpace(text))values.Add(text);}return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
   var one=Convert.ToString(v,CultureInfo.InvariantCulture);return string.IsNullOrWhiteSpace(one)?Array.Empty<string>():new[]{one};
  }
  catch{return Array.Empty<string>();}
 }
 static int SafeInt(Func<object?> get){try{return Convert.ToInt32(get(),CultureInfo.InvariantCulture);}catch{return 0;}}
 static bool SafeBool(Func<object?> get){try{return Convert.ToBoolean(get(),CultureInfo.InvariantCulture);}catch{return false;}}
 static DateTimeOffset? SafeDate(Func<object?> get){try{var v=get();if(v is DateTime dt)return new DateTimeOffset(dt);return DateTimeOffset.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var parsed)?parsed:null;}catch{return null;}}
 static DateTimeOffset? ParseDmtf(string value){try{if(value.Length<14)return null;var dt=DateTime.ParseExact(value[..14],"yyyyMMddHHmmss",CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal);return new DateTimeOffset(dt);}catch{return null;}}
 static void ReleaseCom(object? value){try{if(value is not null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}catch{}}
}
