using System.Security.Cryptography;
using System.Text.Json;
using CleanC.Core;
using CleanC.Repair;

sealed partial class Tests
{
 const string ComponentFixture="""
 Component Store (WinSxS) information:
 Windows Explorer Reported Size of Component Store : 15.70 GB
 Actual Size of Component Store : 9.40 GB
 Shared with Windows : 6.80 GB
 Backups and Disabled Features : 2.50 GB
 Cache and Temporary Data : 100.00 MB
 Number of Reclaimable Packages : 2
 Component Store Cleanup Recommended : Yes
 The operation completed successfully.
 """;
 async Task RunMaintenanceTests()
 {
  await Test("WinSxS remains protected in both classification paths",()=>{
   var p=new SafetyPolicy();var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"WinSxS","old","payload.tmp");
   Check(p.Classify(SnapshotAge(file,365),DateTime.UtcNow).Safety==SafetyLevel.Protected);
   Check(p.ClassifyPath(file,false).Safety==SafetyLevel.Protected);
  });
  await Test("SystemTemp and vendor shader roots have conservative rules",()=>{
   var rules=new SafetyPolicy().Rules;Check(rules.Any(x=>x.Id=="windows-system-temp"&&x.MinimumAge==TimeSpan.FromDays(7)));
   Check(rules.Count(x=>x.Id.StartsWith("shader-",StringComparison.Ordinal)&&x.MinimumAge==TimeSpan.FromDays(30))==4);
  });
  await Test("Cache safety is case insensitive for models, keys and database sidecars",()=>{
   var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CleanCTestState","Cache");
   foreach(var p in new[]{new SafetyPolicy(),new SafetyPolicy([new("test",folder,"cache",TimeSpan.Zero)])})
    foreach(var name in new[]{"MODEL.GGUF","disk.VHDX","state.SQLITE3","state.db-WAL","state.sqlite-SHM","key.PEM","cert.PFX","settings.CONFIG",".env","Login Data","Cookies"})
     Check(p.Classify(SnapshotAge(Path.Combine(folder,name),60),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("System WER whitelist finds aged diagnostics but protects attachments",()=>{
   var p=new SafetyPolicy();var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"Microsoft\Windows\WER\ReportArchive\fixture");
   foreach(var ext in new[]{".wer",".dmp",".hdmp",".cab"})Check(p.Classify(SnapshotAge(Path.Combine(folder,"Report"+ext),30),DateTime.UtcNow).DefaultSelected);
   Check(p.Classify(SnapshotAge(Path.Combine(folder,"Report.wer"),1),DateTime.UtcNow).Safety==SafetyLevel.Optional);
   foreach(var name in new[]{"report.json","account.db","attachment.docx","app.exe"})Check(p.Classify(SnapshotAge(Path.Combine(folder,name),30),DateTime.UtcNow).Safety==SafetyLevel.Protected);
   Check(p.Classify(SnapshotAge(Path.Combine(folder,"Report.wer"),30) with{Links=2},DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("Only aged rotated CBS logs qualify, never the live log",()=>{
   var p=new SafetyPolicy();var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"Logs\CBS");
   Check(p.Classify(SnapshotAge(Path.Combine(folder,"CbsPersist_20260901010101.cab"),40),DateTime.UtcNow).DefaultSelected);
   Check(p.Classify(SnapshotAge(Path.Combine(folder,"CbsPersist_20260901010101.log"),4),DateTime.UtcNow).Safety==SafetyLevel.Optional);
   foreach(var name in new[]{"CBS.log","CbsPersist_secrets.cab","other.cab"})Check(p.Classify(SnapshotAge(Path.Combine(folder,name),40),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("DISM analysis distinguishes Explorer, actual and shared sizes",()=>{
   var a=ComponentStoreService.ParseAnalysis(0,ComponentFixture);
   Check(a.Available&&a.ExplorerSize=="15.70 GB"&&a.ActualSize=="9.40 GB"&&a.SharedSize=="6.80 GB"&&a.ReclaimablePackages==2&&a.CleanupRecommended);
  });
  await Test("DISM parses real Windows CRLF and localized decimal comma",()=>{
   var windowsText=ComponentFixture.Replace("\r\n","\n").Replace("\n","\r\n").Replace("9.40 GB","9,40 GB");
   var a=ComponentStoreService.ParseAnalysis(0,windowsText);Check(a.Available&&a.ActualSize=="9,40 GB");
  });
  await Test("DISM analysis fails closed on error, missing, duplicate or malformed fields",()=>{
   Check(!ComponentStoreService.ParseAnalysis(5,ComponentFixture).Available);
   foreach(var raw in new[]{"",ComponentFixture.Replace("Recommended : Yes","Recommended : Maybe"),ComponentFixture.Replace("Packages : 2","Packages : -2"),ComponentFixture+"\nActual Size of Component Store : 99 GB",ComponentFixture.Replace("9.40 GB","unknown")})
    Check(!ComponentStoreService.ParseAnalysis(0,raw).Available);
  });
  await Test("DISM cleanup commands never ResetBase or recursively delete WinSxS",()=>{
   Check(ComponentStoreService.CleanupArguments=="/English /Online /Cleanup-Image /StartComponentCleanup");
   Check(ComponentStoreService.AnalyzeArguments.EndsWith("/AnalyzeComponentStore",StringComparison.Ordinal));
  });
  await Test("Component analysis does not run unelevated",async()=>{
   var host=new FakeComponentHost{IsAdministrator=false};var result=await new ComponentStoreService(gate,log,host).AnalyzeAsync();
   Check(!result.Available&&result.ExitCode==740&&host.Calls.Count==0);
  });
  await Test("Component writes require capability before starting commands",async()=>{
   var host=new FakeComponentHost();var rejected=false;
   try{await new ComponentStoreService(new Gate{Allowed=false},log,host).CleanupAsync();}catch(UnauthorizedAccessException){rejected=true;}
   Check(rejected&&host.Calls.Count==0);
  });
  await Test("Pending or unknown reboot blocks component cleanup",async()=>{
   foreach(bool? pending in new bool?[]{true,null}){
    var host=new FakeComponentHost{RestartPending=pending};var result=await new ComponentStoreService(gate,log,host).CleanupAsync();
    Check(!result.Completed&&result.RequiresRestart&&host.Calls.Count==0);
   }
  });
  await Test("Component cleanup reanalyzes before and after fixed Windows command",async()=>{
   var host=new FakeComponentHost();host.Results.Enqueue((0,ComponentFixture));host.Results.Enqueue((0,"done"));host.Results.Enqueue((0,ComponentFixture.Replace("Packages : 2","Packages : 0").Replace("Recommended : Yes","Recommended : No")));
   var result=await new ComponentStoreService(gate,log,host).CleanupAsync();
   Check(result.Completed&&!result.RequiresRestart&&result.After?.ReclaimablePackages==0);
   Check(host.Calls.SequenceEqual(new[]{ComponentStoreService.AnalyzeArguments,ComponentStoreService.CleanupArguments,ComponentStoreService.AnalyzeArguments}));
  });
  await Test("No recommendation means no component deletion",async()=>{
   var host=new FakeComponentHost();host.Results.Enqueue((0,ComponentFixture.Replace("Recommended : Yes","Recommended : No")));
   var result=await new ComponentStoreService(gate,log,host).CleanupAsync();Check(!result.Completed&&host.Calls.Count==1);
  });
  await Test("Component servicing error and restart are never successful",async()=>{
   foreach(var code in new[]{5,3010,1641}){
    var host=new FakeComponentHost();host.Results.Enqueue((0,ComponentFixture));host.Results.Enqueue((code,"failed or reboot"));
    var result=await new ComponentStoreService(gate,log,host).CleanupAsync();Check(!result.Completed&&host.Calls.Count==2&&result.RequiresRestart==(code is 3010 or 1641));
   }
  });
  await Test("Component postcheck failure remains unverified",async()=>{
   var host=new FakeComponentHost();host.Results.Enqueue((0,ComponentFixture));host.Results.Enqueue((0,"done"));host.Results.Enqueue((0,"truncated"));
   Check(!(await new ComponentStoreService(gate,log,host).CleanupAsync()).Completed);
  });
  await Test("Maintenance lock rejects overlap and releases exactly once across await",async()=>{
   var first=MaintenanceLock.Enter();var blocked=false;
   try{using var other=MaintenanceLock.Enter();}catch(InvalidOperationException){blocked=true;}
   Check(blocked);await Task.Run(()=>first.Dispose());first.Dispose();
   using var next=MaintenanceLock.Enter();first.Dispose();Check(MaintenanceLock.IsHeld);
  });
  await Test("Maintenance lock also blocks component commands",async()=>{
   using var lease=MaintenanceLock.Enter();var host=new FakeComponentHost();var blocked=false;
   try{await new ComponentStoreService(gate,log,host).AnalyzeAsync();}catch(InvalidOperationException){blocked=true;}
   Check(blocked&&host.Calls.Count==0);
  });
  await Test("Driver overall and individual outcomes both must fully succeed",()=>{
   Check(DriverOperationStatus.IsSuccess(2,2,0));Check(!DriverOperationStatus.IsSuccess(3,2,0));Check(!DriverOperationStatus.IsSuccess(2,3,0));Check(!DriverOperationStatus.IsSuccess(2,2,unchecked((int)0x80004005)));Check(!DriverOperationStatus.IsSuccess(2,0,0));
  });
  await Test("Driver backup validates target INF and all payloads",()=>{
   var dir=NewDriverFixture();Check(DriverBackupIntegrity.Verify(dir,Path.Combine(dir,"test.inf"),out _));
   File.WriteAllText(Path.Combine(dir,"extra.sys"),"unverified");Check(!DriverBackupIntegrity.Verify(dir,Path.Combine(dir,"test.inf"),out _));
  });
  await Test("Driver backup rejects sibling INF even inside common backups root",()=>{
   var one=NewDriverFixture();var two=NewDriverFixture();Check(!DriverBackupIntegrity.Verify(one,Path.Combine(two,"test.inf"),out _));
  });
  await Test("Driver backup rejects tampered and missing payload",()=>{
   var dir=NewDriverFixture();File.AppendAllText(Path.Combine(dir,"test.sys"),"changed");Check(!DriverBackupIntegrity.Verify(dir,Path.Combine(dir,"test.inf"),out _));
  });
  await Test("CleanC prefix does not exempt a driver payload from verification",()=>Check(!DriverBackupIntegrity.IsMetadata("CleanC-malicious.sys")&&!DriverBackupIntegrity.IsMetadata("CleanC-test.inf")));
 }
 string NewDriverFixture()
 {
  var dir=Path.Combine(root,"driver-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  var hashes=new Dictionary<string,string>();foreach(var name in new[]{"test.inf","test.sys","test.cat"}){
   var file=Path.Combine(dir,name);File.WriteAllText(file,"test fixture, not a driver");hashes[name]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
  }
  File.WriteAllText(Path.Combine(dir,"CleanC-driver-hashes.json"),JsonSerializer.Serialize(hashes));return dir;
 }
}
sealed class FakeComponentHost:IComponentStoreHost
{
 public bool IsAdministrator{get;set;}=true;
 public bool? RestartPending{get;set;}=false;
 public List<string> Calls{get;}=[];
 public Queue<(int ExitCode,string Output)> Results{get;}=new();
 public Task<(int ExitCode,string Output)> RunAsync(string arguments,IProgress<RepairProgress>? progress)
 {Calls.Add(arguments);if(Results.Count==0)throw new InvalidOperationException("Unexpected servicing call in fake");return Task.FromResult(Results.Dequeue());}
}
