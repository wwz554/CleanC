using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanC.Core;
using CleanC.Cleaner;
using CleanC.Licensing;
using CleanC.Logging;
using CleanC.Scanner;
using CleanC.Repair;

var suite=new Tests();
await suite.Run();
return suite.Failed==0?0:1;

sealed partial class Tests
{
 public int Failed;int passed;
 readonly string root=Path.Combine(Path.GetTempPath(),"CleanC-tests-"+Guid.NewGuid().ToString("N"));
 readonly ECDsa key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
 readonly FakeClock clock=new();
 readonly MemoryStore store=new();
 SignatureVerifier verifier=null!;AuditLog log=null!;SafetyPolicy policy=null!;
 readonly Gate gate=new();
 public async Task Run()
 {
  Directory.CreateDirectory(root);verifier=new(key.ExportSubjectPublicKeyInfoPem());log=new(Path.Combine(root,"logs"));
  policy=new([new("test-temp",Path.Combine(root,"temp"),"Test Temp",TimeSpan.FromDays(1),[".tmp"])]);
  Directory.CreateDirectory(Path.Combine(root,"temp"));
  await Test("Offline code preserves permanent and exact minute expiry",()=>{
   using var session=new OfflineActivationSession("DEVICE-TEST","test-public-key",clock.SystemUtc);
   var secret=(byte[])typeof(OfflineActivationSession).GetField("secret",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
   uint meta=0;var mac=HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes("CleanC/offline/v2\n"+session.Request+"\n"+meta));var packed=new byte[10];mac.AsSpan(0,6).CopyTo(packed.AsSpan(4));
   string alphabet="0123456789ABCDEFGHJKMNPQRSTVWXYZ",code="";int bits=0,value=0;foreach(var x in packed){value=(value<<8)|x;bits+=8;while(bits>=5){bits-=5;code+=alphabet[(value>>bits)&31];}}
   Check(session.Verify(code,out var type,out var expiry)&&type=="permanent"&&expiry is null);Check(!session.Verify(code,out _,out _));
   using var next=new OfflineActivationSession("DEVICE-TEST","test-public-key",clock.SystemUtc);Check(!next.Verify(code,out _,out _));
  });
  await Test("Offline attempts lock after five failures",()=>{using var session=new OfflineActivationSession("DEVICE-TEST","test",clock.SystemUtc);for(int i=0;i<5;i++)Check(!session.Verify("0000000000000000",out _,out _));Check(!session.IsValid);});
  await Test("Offline human input normalization",()=>Check(OfflineActivationSession.Normalize("abcd-efgh jkmn-pqrs")=="ABCDEFGHJKMNPQRS"));
  await Test("Repair result requires zero exit and no reboot",()=>{
   foreach(var text in new[]{"修复成功，已验证","系统状态良好","已验证正常"}){
    Check(!new RepairResult(RepairAction.FullRepair,5,clock.Now,clock.Now,"",false,text).Success);
    Check(!new RepairResult(RepairAction.FullRepair,0,clock.Now,clock.Now,"",true,text).Success);
    Check(new RepairResult(RepairAction.FullRepair,0,clock.Now,clock.Now,"",false,text).Success);
   }
  });
  await Test("Persistent app state nested under cache is protected",()=>{
   var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
   foreach(var state in new[]{"IndexedDB","Service Worker","CacheStorage","Local Storage","Session Storage"}){
    var path=Path.Combine(local,"CleanCTestNested","Cache",state,"valuable.bin");
    foreach(var p in new[]{new SafetyPolicy(),new SafetyPolicy([new("Chrome-Test-Cache",Path.Combine(local,"CleanCTestNested","Cache"),"cache",TimeSpan.Zero)])}){
     Check(p.Classify(Snapshot(path),DateTime.UtcNow).Safety==SafetyLevel.Protected);
     Check(p.ClassifyPath(path,false).Safety==SafetyLevel.Protected);
    }
   }
  });
  await Test("Executable and model in browser cache never auto delete",()=>{
   var cache=Path.Combine(root,"browser-sensitive");Directory.CreateDirectory(cache);
   var p=new SafetyPolicy([new("Chrome-Test-Cache",cache,"cache",TimeSpan.Zero)]);
   foreach(var ext in new[]{".dll",".exe",".ps1",".onnx",".gguf",".sqlite",".json"})Check(p.Classify(Snapshot(Path.Combine(cache,"keep"+ext)),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("Driver backup is not one-click garbage",()=>{
   var p=new SafetyPolicy();var path=Path.Combine(AppPaths.DriverBackups,"test-not-created","driver.sys");
   Check(p.Classify(Snapshot(path),DateTime.UtcNow).Safety==SafetyLevel.Optional);
   Check(p.Classify(Snapshot(path) with{Links=2},DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("Driver incomplete query flag survives updates",()=>{
   var scan=new DriverScanResult(clock.Now,clock.Now,[],0,0,0,false,"unavailable");
   Check(!(scan with{EndedAt=clock.Now}).OfficialCheckSucceeded);
  });
  await Test("Offline revocation persists after restart",async()=>{
   var st=new MemoryStore();var lease=Lease(true) with{RenewalProtocol="offline-v2",ExpiresAt=DateTimeOffset.MaxValue,LeaseHours=0};
   var trusted=new TrustedTimeService(clock);trusted.AcceptOffline(lease,TimeSpan.Zero);
   st.Write("offline-license.dat",new OfflineActivationRecord(lease));st.Write("trusted-time.dat",trusted.Snapshot(lease));
   var m=Manager(new FakeApi(this){Error="LICENSE_DISABLED"},st);await m.InitializeAsync();Check(m.Context.State==LicenseState.Active);
   try{await m.RefreshAsync();}catch(LicenseException e){Check(e.Code=="LICENSE_DISABLED");}
   Check(m.Context.State==LicenseState.Suspended);
   var restarted=Manager(new FakeApi(this){Offline=true},st);await restarted.InitializeAsync();Check(restarted.Context.State==LicenseState.Suspended);
  });
  await Test("Changed volatile browser cache is skipped",async()=>{
   var file=Old("volatile/entry.bin");var p=new SafetyPolicy([new("Chrome-Test-Cache",Path.GetDirectoryName(file)!,"cache",TimeSpan.Zero)]);
   var before=Snap(file);var item=new ScanItem(1,before,p.Classify(before,DateTime.UtcNow));Check(item.Classification.Safety==SafetyLevel.Safe);
   File.AppendAllText(file,"changed");File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-9));
   var report=await new CleanupExecutor(gate,p,log).ExecuteAsync([item],false,null,CancellationToken.None);
   Check(report.Deleted==0&&report.Skipped==1&&File.Exists(file));
  });
  await Test("Malformed API objects produce friendly errors",async()=>{
   foreach(var value in new[]{"null","[]","42","{\"success\":false,\"code\":42}"}){
    using var api=new LicenseApi(new(),new RawApi(value));bool rejected=false;
    try{await api.Post("license/activate",new{},CancellationToken.None);}catch(LicenseException){rejected=true;}
    Check(rejected);
   }
  });
  await Test("Lease v4 raw-byte P1363 signature",()=>{var l=verifier.VerifyLease(Envelope(Lease()),"DEVICE-TEST");Check(l.Version==4);});
  await Test("Tampered signed payload rejected",()=>{var e=Envelope(Lease());e=e with{SignedPayload=SignatureVerifier.Encode(Encoding.UTF8.GetBytes("{\"version\":4}"))};Throws(()=>verifier.VerifyLease(e,"DEVICE-TEST"));});
  await Test("Wrong server public key rejected",()=>{using var other=ECDsa.Create(ECCurve.NamedCurves.nistP256);Throws(()=>new SignatureVerifier(other.ExportSubjectPublicKeyInfoPem()).VerifyLease(Envelope(Lease()),"DEVICE-TEST"));});
  await Test("Wrong device rejected",()=>Throws(()=>verifier.VerifyLease(Envelope(Lease()),"ANOTHER-DEVICE")));
  await Test("Unsupported lease version rejected",()=>Throws(()=>verifier.VerifyLease(Envelope(Lease() with{Version=3}),"DEVICE-TEST")));
  await Test("Permanent license consistency enforced",()=>Throws(()=>verifier.VerifyLease(Envelope(Lease() with{IsPermanent=true}),"DEVICE-TEST")));
  await Test("Permanent license has no fake countdown",async()=>{var m=Manager(new FakeApi(this),new MemoryStore());await m.ActivateAsync("CLC-AAAA-BBBB-CCCC-DDDD");Check(m.Context.Countdown=="永久授权");});
  await Test("Lease cannot outlive total expiry",()=>Throws(()=>verifier.VerifyLease(Envelope(Lease() with{LicenseExpiresAt=clock.SystemUtc.AddHours(1)}),"DEVICE-TEST")));
  await Test("Duplicate JSON fields rejected",()=>{var raw=Encoding.UTF8.GetBytes("{\"version\":4,\"version\":4}");Throws(()=>verifier.Verify<JsonElement>(new(SignatureVerifier.Encode(raw),SignatureVerifier.Encode(key.SignData(raw,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation)))));});
  await Test("Monotonic time ignores runtime wall-clock rollback",()=>{var c=new FakeClock();var time=new TrustedTimeService(c);time.Accept(Lease());var start=time.Now;c.Now=c.Now.AddDays(-5);c.Elapsed+=TimeSpan.FromMinutes(15);Check(time.Now==start.AddMinutes(15));});
  await Test("Same-boot restart recovers elapsed time",()=>{var c=new FakeClock();var time=new TrustedTimeService(c);time.Accept(Lease());var saved=time.Snapshot(Lease());c.Elapsed+=TimeSpan.FromHours(2);var restored=new TrustedTimeService(c);restored.Restore(Lease(),saved);Check(restored.Now==Lease().ServerTime.AddHours(2));});
  await Test("Reboot clock rollback is locked",()=>{var c=new FakeClock();var time=new TrustedTimeService(c);time.Accept(Lease());var state=time.Snapshot(Lease());c.Boot="next";c.Now-=TimeSpan.FromHours(2);var restored=new TrustedTimeService(c);restored.Restore(Lease(),state);Check(restored.RollbackSuspected);});
  await Test("Missing trusted checkpoint requires online validation",()=>{var time=new TrustedTimeService(new FakeClock());time.Restore(Lease(),null);Check(time.RollbackSuspected);});
  await Test("First activation makes exactly one request",async()=>{var api=new FakeApi(this);var m=Manager(api,store);await m.ActivateAsync("CLC-AAAA-BBBB-CCCC-DDDD");Check(api.Paths.SequenceEqual(new[]{"license/activate"}));});
  await Test("Valid offline launch makes zero requests",async()=>{var api=new FakeApi(this);var m=Manager(api,store);await m.InitializeAsync();Check(api.Paths.Count==0);m.Context.Demand(FeatureCapability.Scan);});
  await Test("Refresh makes exactly challenge + refresh",async()=>{var api=new FakeApi(this);var m=Manager(api,store);await m.InitializeAsync();await m.RefreshAsync();Check(api.Paths.SequenceEqual(new[]{"device/challenge","license/refresh"}));});
  await Test("Regular ticks do not poll the server",async()=>{var api=new FakeApi(this);var m=Manager(api,store);await m.InitializeAsync();for(int i=0;i<10;i++)await m.TickAsync();Check(api.Paths.Count==0);});
  await Test("Service capability is enforced after expiry",async()=>{var c=new FakeClock();var s=new MemoryStore();var api=new FakeApi(this){Permanent=false};var m=Manager(api,s,c);await m.ActivateAsync("CLC-AAAA-BBBB-CCCC-DDDD");c.Elapsed=TimeSpan.FromDays(10);Throws(()=>m.Context.Demand(FeatureCapability.Cleanup));Check(m.Context.State==LicenseState.Expired);});
  await Test("Transient network failure preserves valid lease",async()=>{var api=new FakeApi(this);var m=Manager(api,store);await m.InitializeAsync();api.Offline=true;await m.RefreshAsync();Check(m.Context.State==LicenseState.Active);});
  await Test("Revocation persists across restart",async()=>{var s=new MemoryStore();var api=new FakeApi(this);var m=Manager(api,s);await m.ActivateAsync("CLC-AAAA-BBBB-CCCC-DDDD");api.Error="LICENSE_DISABLED";await m.RefreshAsync();Check(m.Context.State==LicenseState.Suspended);var next=Manager(new FakeApi(this),s);await next.InitializeAsync();Throws(()=>next.Context.Demand(FeatureCapability.Scan));});
  await Test("DPAPI storage does not contain plaintext",()=>{var p=Path.Combine(root,"protected");var st=new ProtectedStore(p);st.Write("value.dat",new {secret="CLC-AAAA-BBBB-CCCC-DDDD"});Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(p,"value.dat"))).Contains("CLC-"));Check(st.Read<JsonElement>("value.dat").GetProperty("secret").GetString()=="CLC-AAAA-BBBB-CCCC-DDDD");});
  await Test("Unknown root data never selected",()=>Check(!new SafetyPolicy().Classify(Snapshot(@"C:\ABC\data.tmp"),DateTime.UtcNow).DefaultSelected));
  await Test("Windows core path protected",()=>Check(new SafetyPolicy().Classify(Snapshot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"abc.tmp")),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("Windows Recovery root protected",()=>Check(new SafetyPolicy().Classify(Snapshot(Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!,"Recovery","WindowsRE","winre.wim")),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("Windows Recovery folder protected",()=>Check(new SafetyPolicy().Classify(Snapshot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Recovery","reagent.xml")),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("Space path Windows folder locked",()=>Check(new SafetyPolicy().ClassifyPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows),true).Safety==SafetyLevel.Protected));
  await Test("Space path Recovery folder locked",()=>Check(new SafetyPolicy().ClassifyPath(Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!,"Recovery"),true).Safety==SafetyLevel.Protected));
  await Test("Space path ProgramData folder locked",()=>Check(new SafetyPolicy().ClassifyPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),true).Safety==SafetyLevel.Protected));
  await Test("Root bootmgr locked",()=>Check(new SafetyPolicy().ClassifyPath(Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!,"bootmgr"),false).Safety==SafetyLevel.Protected));
  await Test("System Volume Information protected",()=>Check(new SafetyPolicy().Classify(Snapshot(Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!,"System Volume Information","restore.dat")),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("ProgramData protected",()=>Check(new SafetyPolicy().Classify(Snapshot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Microsoft","Recovery","ReAgent.xml")),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("Unknown AppData state protected",()=>Check(new SafetyPolicy().Classify(Snapshot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ExampleApp","settings.dat")),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("Executable protected inside temp",()=>{var f=Old("temp/program.exe");Check(policy.Classify(Snap(f),DateTime.UtcNow).Safety==SafetyLevel.Protected);File.Move(f,Path.Combine(root,"program.exe"));});
  await Test("Portable directory protects its temp files",()=>{var exe=Old("temp/portable/app.exe");var data=Old("temp/portable/data.tmp");Check(policy.Classify(Snap(data),DateTime.UtcNow).Safety==SafetyLevel.Protected);});
  await Test("Developer project protected",()=>{Old("temp/project/package.json");var data=Old("temp/project/work.tmp");Check(policy.Classify(Snap(data),DateTime.UtcNow).Safety==SafetyLevel.Protected);});
  await Test("AI models protected",()=>Check(new SafetyPolicy().Classify(Snapshot(@"C:\Models\model.gguf"),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("Virtual disks protected",()=>Check(new SafetyPolicy().Classify(Snapshot(@"C:\VMs\data.vhdx"),DateTime.UtcNow).Safety==SafetyLevel.Protected));
  await Test("TMP outside approved root not safe",()=>Check(!policy.Classify(Snapshot(Path.Combine(root,"outside.tmp")),DateTime.UtcNow).DefaultSelected));
  await Test("Browser cookies never selected",()=>Check(!new SafetyPolicy().Classify(Snapshot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Google\Chrome\User Data\Default\Network\Cookies")),DateTime.UtcNow).DefaultSelected));
  await Test("New temp file not selected",()=>{var p=Path.Combine(root,"temp","new.tmp");File.WriteAllText(p,"new");Check(!policy.Classify(Snap(p),DateTime.UtcNow).DefaultSelected);});
  await Test("Large temp file optional",()=>Check(policy.Classify(Snapshot(Path.Combine(root,"temp","large.tmp")) with{Size=2L*1024*1024*1024},DateTime.UtcNow).Safety==SafetyLevel.Optional));
  await Test("Scanner is read only and rolls up directory sizes",async()=>{var p=Old("temp/nested/a.tmp");var before=File.ReadAllBytes(p);var db=new ScanDatabase(Path.Combine(root,"scan.db"));var scanner=new DirectoryScanner(gate,policy,db,log);var summary=await scanner.ScanAsync(Path.Combine(root,"temp"),null,CancellationToken.None);Check(File.Exists(p)&&before.SequenceEqual(File.ReadAllBytes(p)));Check(summary.Files>=1);Check(db.Items(SafetyLevel.Safe).Any(i=>i.File.Path==p));Check(db.Children(Path.Combine(root,"temp")).Any(i=>i.Name=="nested"&&i.Bytes==before.Length));});
  await Test("Dry run does not remove files",async()=>{var p=Old("temp/dry.tmp");var report=await Cleaner().ExecuteAsync([Item(p)],true,null,CancellationToken.None);Check(File.Exists(p)&&report.Deleted==0&&report.Items.Single().Result=="WouldDelete");});
  await Test("Actual cleaning removes only validated fixture",async()=>{var p=Old("temp/delete.tmp");var report=await Cleaner().ExecuteAsync([Item(p)],false,null,CancellationToken.None);Check(!File.Exists(p)&&report.Deleted==1);});
  await Test("Changed file is skipped",async()=>{var p=Old("temp/changed.tmp");var item=Item(p);File.AppendAllText(p,"modified");var r=await Cleaner().ExecuteAsync([item],false,null,CancellationToken.None);Check(File.Exists(p)&&r.Skipped==1);});
  await Test("Replacement file with same timestamps is skipped",async()=>{var p=Old("temp/replaced.tmp");var item=Item(p);File.Move(p,p+".old");File.WriteAllText(p,"fixture");File.SetLastWriteTimeUtc(p,item.File.LastWriteUtc);File.SetCreationTimeUtc(p,item.File.CreationUtc);var r=await Cleaner().ExecuteAsync([item],false,null,CancellationToken.None);Check(File.Exists(p)&&r.Skipped==1);});
  await Test("Locked files are skipped",async()=>{var p=Old("temp/locked.tmp");var item=Item(p);using var held=new FileStream(p,FileMode.Open,FileAccess.ReadWrite,FileShare.None);var r=await Cleaner().ExecuteAsync([item],false,null,CancellationToken.None);Check(File.Exists(p)&&r.Skipped==1);});
  await Test("Explicitly selected user data is revalidated before deletion",async()=>{var p=Old("temp/user.tmp");var item=Item(p) with{Classification=new(SafetyLevel.UserData,"User","User selected")};var r=await Cleaner().ExecuteAsync([item],false,null,CancellationToken.None);Check(!File.Exists(p)&&r.Deleted==1);});
  await Test("Cancellation does not start deletion",async()=>{var p=Old("temp/cancel.tmp");using var c=new CancellationTokenSource();c.Cancel();try{await Cleaner().ExecuteAsync([Item(p)],false,null,c.Token);}catch(OperationCanceledException){}Check(File.Exists(p));});
  await Test("No permission means scanner cannot start",async()=>{var denied=new Gate{Allowed=false};var db=new ScanDatabase(Path.Combine(root,"denied.db"));bool threw=false;try{await new DirectoryScanner(denied,policy,db,log).ScanAsync(root,null,CancellationToken.None);}catch(UnauthorizedAccessException){threw=true;}Check(threw);});
  await Test("Repair commands are fixed Microsoft tools",()=>{Check(RepairCommands.Get(RepairAction.ImageRestore).Arguments=="/Online /Cleanup-Image /RestoreHealth");Check(RepairCommands.Get(RepairAction.DiskScan).Arguments==RepairCommands.SystemVolume+" /scan");Check(RepairCommands.Get(RepairAction.DiskScan).ChangesSystem);Check(!new RepairResult(RepairAction.ImageCheck,5,clock.Now,clock.Now,"",false,"failed").Success);});
  if(Environment.GetEnvironmentVariable("CLEANC_LIVE_TESTS")=="1")
   await Test("Production bootstrap verifies with embedded public key",async()=>{using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};var text=await http.GetStringAsync("https://wwz554.ccwu.cc/bootstrap/v1/config");var envelope=JsonSerializer.Deserialize<SignedEnvelope>(text,SignatureVerifier.Json)!;var payload=new SignatureVerifier().Verify<JsonElement>(envelope);Check(payload.GetProperty("apiVersion").GetInt32()==3&&payload.GetProperty("leaseVersion").GetInt32()==4);});
  else Console.WriteLine("SKIP Production bootstrap live probe (set CLEANC_LIVE_TESTS=1; offline regression does not assert server availability)");
  await Test("Recovery quarantine and restore round trip",()=>{var p=Old("temp/recover.tmp");var expected=File.ReadAllBytes(p);var recovery=new RecoveryService(gate,log,Path.Combine(root,"recovery"));var item=recovery.Quarantine(Snap(p),policy);Check(!File.Exists(p)&&File.Exists(item.StoredPath));recovery.Restore(item);Check(expected.SequenceEqual(File.ReadAllBytes(p)));bool rejected=false;try{recovery.Restore(item);}catch(IOException){rejected=true;}Check(rejected);});
  await Test("Hardlinked file is protected",()=>{var p=Old("temp/hardlink.tmp");var alias=Path.Combine(root,"hardlink-alias.tmp");Check(NativeTests.CreateHardLink(alias,p,IntPtr.Zero));using var pin=new PinnedFile(p);Check(pin.Snapshot.Links==2&&policy.Classify(pin.Snapshot,DateTime.UtcNow).Safety==SafetyLevel.Protected);});
  await Test("Pinned ancestor cannot be renamed during deletion",()=>{var p=Old("temp/pinned/data.tmp");using var pin=new PinnedFile(p,true);bool blocked=false;try{Directory.Move(Path.GetDirectoryName(p)!,Path.Combine(root,"moved"));}catch(IOException){blocked=true;}Check(blocked);});
  await Test("Junction is neither followed nor deleted",async()=>{var target=Path.Combine(root,"junction-target");Directory.CreateDirectory(target);var p=Path.Combine(target,"valuable.tmp");File.WriteAllText(p,"keep");var junction=Path.Combine(root,"temp","junction");var start=new System.Diagnostics.ProcessStartInfo{FileName="cmd.exe",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("/c");start.ArgumentList.Add("mklink");start.ArgumentList.Add("/J");start.ArgumentList.Add(junction);start.ArgumentList.Add(target);using var process=System.Diagnostics.Process.Start(start)!;await process.WaitForExitAsync();Check(process.ExitCode==0);bool rejected=false;try{using var pin=new PinnedFile(Path.Combine(junction,"valuable.tmp"),true);}catch(IOException){rejected=true;}Check(rejected);var db=new ScanDatabase(Path.Combine(root,"junction.db"));await new DirectoryScanner(gate,policy,db,log).ScanAsync(Path.Combine(root,"temp"),null,CancellationToken.None);Check(!db.Items(null,0,10000).Any(x=>x.File.Path.Contains("junction"))&&File.ReadAllText(p)=="keep");});
  await Test("Empty API configuration fails closed",async()=>{using var api=new LicenseApi(new(){BaseUrl=""});bool rejected=false;try{await api.Post(LicenseEndpoints.Activate,new{},CancellationToken.None);}catch(LicenseException e){rejected=e.Code=="API_NOT_CONFIGURED";}Check(rejected);});
  await Test("Browser cache index is optional structural file",()=>{var baseDir=Path.Combine(root,"browser","Cache");var rule=new CleanupRule("Chrome-Default-Cache",baseDir,"Chrome 浏览器缓存",TimeSpan.Zero);var p=new SafetyPolicy([rule]);Check(p.Classify(Snapshot(Path.Combine(baseDir,"Cache_Data","index")),DateTime.UtcNow).Safety==SafetyLevel.Optional);});
  await Test("Browser GPU data_3 is optional structural file",()=>{var baseDir=Path.Combine(root,"browser","GPUCache");var rule=new CleanupRule("Chrome-Default-GPUCache",baseDir,"Chrome 浏览器缓存",TimeSpan.Zero);var p=new SafetyPolicy([rule]);Check(p.Classify(Snapshot(Path.Combine(baseDir,"data_3")),DateTime.UtcNow).Safety==SafetyLevel.Optional);});
  await Test("Old generic AppData cache is safe",()=>{var p=new SafetyPolicy();var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CleanCTestUnknownApp","Cache","blob.bin");Check(p.Classify(Snapshot(path),DateTime.UtcNow).Safety==SafetyLevel.Safe);});
  await Test("Recycle bin remains protected from file-by-file scanner deletion",()=>{var p=new SafetyPolicy();var drive=Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;Check(p.ClassifyPath(Path.Combine(drive,"$Recycle.Bin"),true).Safety==SafetyLevel.Protected);});
  await Test("Image inside cache is user data for manual review",()=>{var baseDir=Path.Combine(root,"media-cache");var rule=new CleanupRule("Chrome-Test-Cache",baseDir,"浏览器缓存",TimeSpan.Zero);var p=new SafetyPolicy([rule]);Check(p.Classify(Snapshot(Path.Combine(baseDir,"photo.jpg")),DateTime.UtcNow).Safety==SafetyLevel.UserData);});
  await Test("Windows thumbnail cache is optional because it regenerates",()=>{var baseDir=Path.Combine(root,"explorer-cache");var rule=new CleanupRule("Windows-ThumbnailCache",baseDir,"Windows 缩略图缓存",TimeSpan.Zero,[".db"],"thumbcache_");var p=new SafetyPolicy([rule]);Check(p.Classify(Snapshot(Path.Combine(baseDir,"thumbcache_256.db")),DateTime.UtcNow).Safety==SafetyLevel.Optional);});
  await Test("Recent browser cache is optional",()=>{var baseDir=Path.Combine(root,"browser-recent","Cache");var rule=new CleanupRule("Chrome-Test-Cache",baseDir,"Chrome 浏览器缓存",TimeSpan.FromDays(7));Directory.CreateDirectory(baseDir);var p=new SafetyPolicy([rule]);Check(p.Classify(SnapshotAge(Path.Combine(baseDir,"entry.bin"),1),DateTime.UtcNow).Safety==SafetyLevel.Optional);});
  await Test("Browser cache older than seven days is safe",()=>{var baseDir=Path.Combine(root,"browser-old","Cache");var rule=new CleanupRule("Chrome-Test-Cache",baseDir,"Chrome 浏览器缓存",TimeSpan.FromDays(7));Directory.CreateDirectory(baseDir);var p=new SafetyPolicy([rule]);Check(p.Classify(SnapshotAge(Path.Combine(baseDir,"entry.bin"),10),DateTime.UtcNow).Safety==SafetyLevel.Safe);});
  await Test("Recent generic app cache is optional",()=>{var p=new SafetyPolicy();var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CleanCTestRecentApp","Cache","blob.bin");Check(p.Classify(SnapshotAge(path,1),DateTime.UtcNow).Safety==SafetyLevel.Optional);});
  await Test("Executable in generic cache is protected",()=>{var p=new SafetyPolicy();var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CleanCTestApp","Cache","helper.dll");Check(p.Classify(Snapshot(path),DateTime.UtcNow).Safety==SafetyLevel.Protected);});
  await Test("Database in generic cache is protected",()=>{var p=new SafetyPolicy();var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CleanCTestStateApp","Cache","state.db");Check(p.Classify(Snapshot(path),DateTime.UtcNow).Safety==SafetyLevel.Protected);});
  await Test("Cache index metadata is optional",()=>{var p=new SafetyPolicy();var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CleanCTestIndexApp","Cache","index");Check(p.Classify(Snapshot(path),DateTime.UtcNow).Safety==SafetyLevel.Optional);});
  await Test("Active Claude local-agent session cache is optional",()=>{var p=new SafetyPolicy();var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages","Claude_test","LocalCache","Roaming","Claude","local-agent-mode-sessions","skills-plugin","spec.bin");Check(p.Classify(Snapshot(path),DateTime.UtcNow).Safety==SafetyLevel.Optional);});
  await Test("Missing selected file is resolved instead of skipped",async()=>{var p=Old("temp/gone-before-clean.tmp");var item=Item(p);File.Delete(p);var r=await Cleaner().ExecuteAsync([item],false,null,CancellationToken.None);Check(r.Skipped==0&&r.Deleted==0&&r.Items.Single().Result=="Gone");});
  await Test("Exit database cleanup preserves summaries and clears transient rows",()=>{
   var dbFile=Path.Combine(root,"exit-cleanup.db");
   var db=new ScanDatabase(dbFile);
   using(var c=db.Open())
   {
    using var cmd=c.CreateCommand();
    cmd.CommandText="""
    INSERT INTO entries(path,parent,name,size,modified,created,attributes,safety,category,reason,rule,fileid,volume,links,isdir,selected)
    VALUES('C:\\tmp\\a.bin','C:\\tmp','a.bin',10,0,0,0,0,'test','test',NULL,'0',0,0,0,1);
    INSERT INTO summaries(started,summary) VALUES('2026-09-19T00:00:00Z','{"ok":true}');
    INSERT INTO reports(started,report) VALUES('2026-09-19T00:00:00Z','{"report":true}');
    """;
    cmd.ExecuteNonQuery();
   }
   var r=db.CleanupForExit();Check(r.Success&&r.RemovedEntries==1&&r.RemovedReports==1);
   using(var c=db.Open())
   {
    using var cmd=c.CreateCommand();
    cmd.CommandText="SELECT (SELECT COUNT(*) FROM entries),(SELECT COUNT(*) FROM summaries),(SELECT COUNT(*) FROM reports)";
    using var row=cmd.ExecuteReader();Check(row.Read()&&row.GetInt64(0)==0&&row.GetInt64(1)==1&&row.GetInt64(2)==0);
   }
   Check(!Directory.EnumerateFiles(root,"exit-cleanup.db.exit-*").Any());
  });
  await RunMaintenanceTests();
  await Run171Tests();
  Console.WriteLine($"RESULT: {passed} passed, {Failed} failed. Fixtures: {root}");
 }
 Lease Lease(bool permanent=false)=>new(){Version=4,ApiVersion=3,LicenseId="license-test",DeviceId="DEVICE-TEST",Edition="Pro",LicenseType=permanent?"permanent":"duration",IsPermanent=permanent,CountdownRequired=!permanent,Features=["clean","scan","optimize"],IssuedAt=clock.SystemUtc,ServerTime=clock.SystemUtc,ExpiresAt=clock.SystemUtc.AddHours(72),LicenseExpiresAt=permanent?null:clock.SystemUtc.AddDays(7),LeaseHours=72,RenewalProtocol="challenge-refresh",Nonce="test-nonce"};
 SignedEnvelope Envelope(Lease lease){var raw=JsonSerializer.SerializeToUtf8Bytes(lease,SignatureVerifier.Json);return new(SignatureVerifier.Encode(raw),SignatureVerifier.Encode(key.SignData(raw,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));}
 LicenseManager Manager(FakeApi handler,MemoryStore st,FakeClock? c=null)=>new(new LicenseApi(new(),handler),verifier,new FakeDevice(),st,c??clock,log);
 CleanupExecutor Cleaner()=>new(gate,policy,log);
 string Old(string relative){var p=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,"fixture");File.SetLastWriteTimeUtc(p,DateTime.UtcNow.AddDays(-10));File.SetCreationTimeUtc(p,DateTime.UtcNow.AddDays(-10));return p;}
 static FileSnapshot Snapshot(string p)=>new(p,7,DateTime.UtcNow.AddDays(-10),DateTime.UtcNow.AddDays(-10),FileAttributes.Normal);
 static FileSnapshot SnapshotAge(string p,int days)=>new(p,7,DateTime.UtcNow.AddDays(-days),DateTime.UtcNow.AddDays(-days),FileAttributes.Normal);
 static FileSnapshot Snap(string p){using var h=new PinnedFile(p);return h.Snapshot;}
 ScanItem Item(string p){var s=Snap(p);return new(1,s,policy.Classify(s,DateTime.UtcNow));}
 async Task Test(string name,Action action)=>await Test(name,()=>{action();return Task.CompletedTask;});
 async Task Test(string name,Func<Task> action){try{await action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){Failed++;Console.WriteLine("FAIL "+name+" :: "+e);}}
 static void Check(bool value){if(!value)throw new Exception("Assertion failed");}
 static void Throws(Action action){try{action();}catch(LicenseException){return;}throw new Exception("Expected rejection");}
 sealed class FakeApi(Tests tests):HttpMessageHandler
 {
  public List<string> Paths=[];public bool Permanent=true,Offline;public string? Error;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
   var path=r.RequestUri!.AbsolutePath.Replace("/api/v1/","");Paths.Add(path);if(Offline)throw new HttpRequestException();
   if(Error is not null)return Json(new{success=false,code=Error},HttpStatusCode.Forbidden);
   var body=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
   Check(body.RootElement.GetProperty("deviceId").GetString()=="DEVICE-TEST");
   if(path is "device/challenge" or "offline/challenge")return Json(new{success=true,nonce="a-valid-nonce-for-test-only"});
   if(path=="offline/refresh"){var signed=tests.Envelope(tests.Lease(Permanent));return Json(new{success=true,licenseKey="CLC-AAAA-BBBB-CCCC-DDDD",signedPayload=signed.SignedPayload,signature=signed.Signature});}
   if(path=="license/refresh"){Check(body.RootElement.GetProperty("signature").GetString()=="device-signature");Check(body.RootElement.GetProperty("nonce").GetString()=="a-valid-nonce-for-test-only");}
   var env=tests.Envelope(tests.Lease(Permanent));return Json(new{success=true,signedPayload=env.SignedPayload,signature=env.Signature,lease=new{version=999}});
  }
  static HttpResponseMessage Json(object obj,HttpStatusCode code=HttpStatusCode.OK)=>new(code){Content=new StringContent(JsonSerializer.Serialize(obj),Encoding.UTF8,"application/json")};
 }
}
sealed class FakeClock:ITimeSource {public DateTimeOffset Now=new(2026,9,17,12,0,0,TimeSpan.Zero);public TimeSpan Elapsed=TimeSpan.FromDays(1);public string Boot="boot-one";public DateTimeOffset SystemUtc=>Now;public TimeSpan Uptime=>Elapsed;public string BootId=>Boot;}
sealed class MemoryStore:IProtectedStore{readonly Dictionary<string,string> data=[];public T? Read<T>(string name)=>data.TryGetValue(name,out var s)?JsonSerializer.Deserialize<T>(s,SignatureVerifier.Json):default;public void Write<T>(string name,T value)=>data[name]=JsonSerializer.Serialize(value,SignatureVerifier.Json);}
sealed class FakeDevice:IDeviceIdentity{public string DeviceId=>"DEVICE-TEST";public string PublicKeyPem=>"test-public-only";public string Sign(string nonce)=>"device-signature";}
sealed class RawApi(string json):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});}
sealed class Gate:ICapabilityGate{public bool Allowed=true;public void Demand(FeatureCapability capability){if(!Allowed)throw new UnauthorizedAccessException();}}
static class NativeTests{[System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]public static extern bool CreateHardLink(string name,string existing,IntPtr security);}
