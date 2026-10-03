using CleanC.Core;
using CleanC.Cleaner;
using CleanC.Repair;
using CleanC.Licensing;

sealed partial class Tests
{
 async Task Run172Tests()
 {
  var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
  var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  await Test("LocalCache persistent opaque app state is not junk",()=>{
   var p=new SafetyPolicy();
   foreach(var name in new[]{"account.dat","saved-session.bin","encrypted-token"})
   {
    var file=Path.Combine(local,"Packages","TestApp_fixture","LocalCache",name);
    Check(p.Classify(SnapshotAge(file,365),DateTime.UtcNow).Safety==SafetyLevel.Protected);
    Check(p.ClassifyPath(file,false).Safety==SafetyLevel.Protected);
   }
   Check(p.ClassifyPath(Path.Combine(local,"Packages","TestApp_fixture","LocalCache","Cache","resource.bin"),false).Safety==SafetyLevel.Optional);
  });
  await Test("Local and system old WER cabinets share narrow diagnostic rules",()=>{
   var p=new SafetyPolicy();
   var folder=Path.Combine(local,@"Microsoft\Windows\WER\ReportQueue\fixture");
   Check(p.Classify(SnapshotAge(Path.Combine(folder,"report.cab"),30),DateTime.UtcNow).DefaultSelected);
   foreach(var name in new[]{"settings.json","attachment.zip","private.dat"})
    Check(p.Classify(SnapshotAge(Path.Combine(folder,name),30),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("Old kernel dumps, Crashpad and rotated DISM logs are found",()=>{
   var p=new SafetyPolicy();
   foreach(var file in new[]{
    Path.Combine(windows,@"Minidump\100126-1.dmp"),
    Path.Combine(windows,"MEMORY.DMP"),
    Path.Combine(windows,@"LiveKernelReports\WATCHDOG\fixture.dmp"),
    Path.Combine(windows,@"Logs\DISM\dism.log.bak"),
    Path.Combine(local,@"TestApp\Crashpad\reports\fixture.dmp"),
    Path.Combine(local,@"TestApp\Crashpad\pending\fixture.dmp")})
   {
    Check(p.Classify(SnapshotAge(file,60),DateTime.UtcNow).DefaultSelected);
    Check(p.Classify(SnapshotAge(file,1),DateTime.UtcNow).Safety==SafetyLevel.Optional);
    Check(p.Classify(SnapshotAge(file,60) with{Links=2},DateTime.UtcNow).Safety==SafetyLevel.Protected);
    Check(p.Classify(SnapshotAge(file,60) with{Attributes=FileAttributes.ReparsePoint},DateTime.UtcNow).Safety==SafetyLevel.Protected);
    Check(!p.ClassifyPath(file,false).DefaultSelected);
   }
  });
  await Test("Live diagnostic logs, unknown attachments and own authorization remain protected",()=>{
   var p=new SafetyPolicy();
   foreach(var file in new[]{
    Path.Combine(windows,@"Logs\DISM\dism.log"),
    Path.Combine(windows,@"Logs\DISM\other.bak"),
    Path.Combine(windows,@"Logs\DISM\nested\dism.log.bak"),
    Path.Combine(windows,@"Minidump\personal.docx"),
    Path.Combine(local,@"TestApp\Crashpad\settings.dat"),
    Path.Combine(AppPaths.UserData,@"Crashpad\reports\keep.dmp")})
    Check(p.Classify(SnapshotAge(file,365),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("Generic app cache with portable executable is never auto deleted",()=>{
   var folder=Path.Combine(root,"generic-cache","Cache");Directory.CreateDirectory(folder);
   File.WriteAllText(Path.Combine(folder,"portable.exe"),"fixture only");
   var p=new SafetyPolicy([]);
   Check(p.Classify(SnapshotAge(Path.Combine(folder,"data.bin"),30),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("A file named Cache is not proof of a cache directory",()=>{
   var p=new SafetyPolicy();var path=Path.Combine(local,"UnknownApp","Cache");
   Check(p.Classify(SnapshotAge(path,90),DateTime.UtcNow).Safety==SafetyLevel.Protected);
   Check(p.ClassifyPath(path,false).Safety==SafetyLevel.Protected);
   Check(p.ClassifyPath(path,true).Safety==SafetyLevel.Optional);
  });
  await Test("Scanning and cleaning an isolated Crashpad fixture removes only aged reports",async()=>{
   var folder=Path.Combine(root,"expanded-fixture","Crashpad","reports");Directory.CreateDirectory(folder);
   var old=Path.Combine(folder,"old.dmp");var recent=Path.Combine(folder,"recent.dmp");var document=Path.Combine(folder,"attachment.docx");
   foreach(var file in new[]{old,recent,document})File.WriteAllText(file,"isolated test fixture");
   File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-30));File.SetCreationTimeUtc(old,DateTime.UtcNow.AddDays(-30));
   var p=new SafetyPolicy();var db=new CleanC.Scanner.ScanDatabase(Path.Combine(root,"expanded-scan.db"));
   await new CleanC.Scanner.DirectoryScanner(gate,p,db,log).ScanAsync(folder,null,CancellationToken.None);
   var scanned=db.ItemsAll();var item=scanned.Single(x=>x.File.Path==old);
   Check(item.Classification.DefaultSelected&&item.Selected);
   Check(scanned.Where(x=>x.File.Path!=old).All(x=>!x.Selected));
   var result=await new CleanupExecutor(gate,p,log).ExecuteAsync([item],false,null,CancellationToken.None);
   Check(result.Deleted==1&&!File.Exists(old)&&File.Exists(recent)&&File.Exists(document));
  });
  await Test("Newly attached recycle volume is not added after confirmation",()=>{
   var host=new FakeRecycleHost();host.Volumes[@"C:\"]=new(@"C:\",2,200);
   var service=new RecycleBinService(gate,log,host);var confirmed=service.QueryAll();
   host.Volumes[@"D:\"]=new(@"D:\",1,100);
   var result=service.EmptyConfirmed(confirmed);
   Check(result.Success&&result.Items==2&&result.FreedBytes==200&&host.Emptied.SequenceEqual(new[]{@"C:\"}));
   Check(result.FinalState.Items==1&&host.Volumes[@"D:\"].Items==1);
  });
  await Test("Recycle content changed since confirmation is skipped",()=>{
   var host=new FakeRecycleHost();host.Volumes[@"C:\"]=new(@"C:\",2,200);
   var service=new RecycleBinService(gate,log,host);var confirmed=service.QueryAll();
   host.Volumes[@"C:\"]=new(@"C:\",3,350);
   var result=service.EmptyConfirmed(confirmed);Check(!result.Success&&host.Emptied.Count==0&&result.FreedBytes==0);
  });
  await Test("Recycle query failures cannot imply empty or freed bytes",()=>{
   var host=new FakeRecycleHost();host.Volumes[@"C:\"]=new(@"C:\",2,200);
   var service=new RecycleBinService(gate,log,host);var confirmed=service.QueryAll();
   host.FailQuery=true;
   var result=service.EmptyConfirmed(confirmed);
   Check(!result.Success&&!result.FinalState.QueryComplete&&result.FreedBytes==0&&host.Emptied.Count==0);
  });
  await Test("Single-volume cleanup verifies only its requested scope",()=>{
   var host=new FakeRecycleHost();host.Volumes[@"C:\"]=new(@"C:\",2,200);host.Volumes[@"D:\"]=new(@"D:\",1,100);
   var result=new RecycleBinService(gate,log,host).Empty(@"C:\");
   Check(result.Success&&result.Items==2&&host.Volumes[@"D:\"].Items==1);
  });
  await Test("Recycle maintenance overlap and cancellation never start deletion",()=>{
   var host=new FakeRecycleHost();host.Volumes[@"C:\"]=new(@"C:\",2,200);
   var service=new RecycleBinService(gate,log,host);var confirmed=service.QueryAll();
   using(MaintenanceLock.Enter()){var rejected=false;try{service.EmptyConfirmed(confirmed);}catch(InvalidOperationException){rejected=true;}Check(rejected);}
   var result=service.EmptyConfirmed(confirmed,new CancellationToken(true));
   Check(!result.Success&&host.Emptied.Count==0);
  });
  await Test("Recycle capability is rechecked between volumes",()=>{
   var restricted=new Gate();var host=new FakeRecycleHost();host.Volumes[@"C:\"]=new(@"C:\",1,100);host.Volumes[@"D:\"]=new(@"D:\",1,100);
   host.AfterEmpty=()=>restricted.Allowed=false;
   var service=new RecycleBinService(restricted,log,host);var result=service.EmptyConfirmed(service.QueryAll());
   Check(!result.Success&&result.Items==1&&host.Emptied.SequenceEqual(new[]{@"C:\"}));
  });
  await Test("Repair stops at reboot result without running followup commands",async()=>{
   foreach(var action in new[]{RepairAction.ImageRestore,RepairAction.SystemRepair,RepairAction.FullRepair})
    foreach(var code in new[]{3010,1641})
    {
     var host=new FakeRepairHost();host.Commands.Enqueue((code,"restart needed"));
     var result=await new RepairService(gate,log,host).RunAsync(action,null);
     Check(!result.Success&&result.RequiresRestart&&host.Calls.Count==1&&host.HealthQueries==0);
    }
  });
  await Test("Repair detects pending reboot even when Windows returned zero",async()=>{
   var host=new FakeRepairHost();host.Commands.Enqueue((0,"completed"));host.AfterCommand=()=>host.RestartPending=true;
   var result=await new RepairService(gate,log,host).RunAsync(RepairAction.ImageRestore,null);
   Check(result.RequiresRestart&&!result.Success&&host.HealthQueries==0);
  });
  await Test("Repair rechecks capability before next modifying phase",async()=>{
   var restricted=new Gate();var host=new FakeRepairHost();host.Commands.Enqueue((0,"done"));
   host.AfterHealth=()=>restricted.Allowed=false;
   host.Commands.Enqueue((0,"Windows Resource Protection did not find any integrity violations"));
   var result=await new RepairService(restricted,log,host).RunAsync(RepairAction.FullRepair,null);
   Check(!result.Success&&!host.Calls.Contains(RepairAction.SystemRepair)&&!host.Calls.Contains(RepairAction.DiskScan));
  });
  await Test("Full repair succeeds only after independent image, SFC and disk verification",async()=>{
   var host=new FakeRepairHost();
   host.Commands.Enqueue((0,"image restored"));host.Commands.Enqueue((0,"SFC done"));
   host.Commands.Enqueue((0,"Windows Resource Protection did not find any integrity violations"));host.Commands.Enqueue((0,"disk healthy"));
   var result=await new RepairService(gate,log,host).RunAsync(RepairAction.FullRepair,null);
   Check(result.Success&&host.HealthQueries==1&&host.Calls.SequenceEqual(new[]{RepairAction.ImageRestore,RepairAction.SystemRepair,RepairAction.SystemVerify,RepairAction.DiskScan}));
  });
  await Test("Repair prerequisites refuse writes with unknown reboot or no elevation",async()=>{
   foreach(var host in new[]{new FakeRepairHost{RestartPending=null},new FakeRepairHost{IsAdministrator=false}})
   {
    var rejected=false;try{await new RepairService(gate,log,host).RunAsync(RepairAction.FullRepair,null);}catch(Exception e)when(e is UnauthorizedAccessException or InvalidOperationException){rejected=true;}
    Check(rejected&&host.Calls.Count==0);
   }
  });
  await Test("Production LicenseException stops maintenance and preserves partial results",async()=>{
   var allowed=true;var realExceptionGate=new CallbackGate(()=>{if(!allowed)throw new LicenseException("CAPABILITY_DENIED","授权已到期");});
   var bins=new FakeRecycleHost();bins.Volumes[@"C:\"]=new(@"C:\",1,10);bins.Volumes[@"D:\"]=new(@"D:\",1,10);
   bins.AfterEmpty=()=>allowed=false;
   var recycle=new RecycleBinService(realExceptionGate,log,bins);var recycled=recycle.EmptyConfirmed(recycle.QueryAll());
   Check(!recycled.Success&&recycled.Items==1&&bins.Emptied.Count==1);
   allowed=true;
   var host=new FakeRepairHost();host.Commands.Enqueue((0,"image restored"));
   host.Commands.Enqueue((0,"Windows Resource Protection did not find any integrity violations"));host.AfterHealth=()=>allowed=false;
   var repaired=await new RepairService(realExceptionGate,log,host).RunAsync(RepairAction.FullRepair,null);
   Check(!repaired.Success&&repaired.Output.Contains("授权校验未通过")&&!host.Calls.Contains(RepairAction.SystemRepair));
  });
 }
 sealed class CallbackGate(Action action):ICapabilityGate{public void Demand(FeatureCapability capability)=>action();}
 sealed class FakeRecycleHost:IRecycleBinHost
 {
  public Dictionary<string,RecycleBinVolumeInfo> Volumes=new(StringComparer.OrdinalIgnoreCase);
  public List<string> Emptied=[];public bool FailQuery;public Action? AfterEmpty;
  public IReadOnlyList<string> LocalRoots()=>Volumes.Keys.ToArray();
  public bool TryQuery(string root,out RecycleBinVolumeInfo volume){volume=Volumes.GetValueOrDefault(root);return !FailQuery&&Volumes.ContainsKey(root);}
  public int Empty(string root){Emptied.Add(root);Volumes[root]=new(root,0,0);AfterEmpty?.Invoke();return 0;}
 }
 sealed class FakeRepairHost:IRepairHost
 {
  public bool IsAdministrator{get;set;}=true;public bool? RestartPending{get;set;}=false;
  public Queue<(int,string)> Commands=new();public List<RepairAction> Calls=[];public int HealthQueries;
  public Action? AfterCommand,AfterHealth;
  public Task<(int ExitCode,string Output)> RunAsync(RepairAction action,RepairCommand command,int start,int end,IProgress<RepairProgress>? progress)
  {Calls.Add(action);var result=Commands.Dequeue();AfterCommand?.Invoke();return Task.FromResult(result);}
  public Task<(int State,int ExitCode,string Raw)> QueryImageHealthAsync(bool scan)
  {HealthQueries++;AfterHealth?.Invoke();return Task.FromResult((0,0,"CLEANC_HEALTH=0"));}
 }
}
