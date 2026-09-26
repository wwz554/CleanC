using CleanC.Core;
using CleanC.Licensing;
using CleanC.Repair;

sealed partial class Tests
{
 async Task Run171Tests()
 {
  MemoryStore OfflineStore(FakeClock c)
  {
   var state=new MemoryStore();var l=Lease() with{RenewalProtocol="offline-v2",ExpiresAt=Lease().LicenseExpiresAt!.Value,LeaseHours=0};
   var trusted=new TrustedTimeService(c);trusted.AcceptOffline(l,TimeSpan.Zero);
   state.Write("offline-license.dat",new OfflineActivationRecord(l));state.Write("trusted-time.dat",trusted.Snapshot(l));return state;
  }
  await Test("Offline license survives reboot without network or identity replacement",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);c.Boot="second-boot";c.Elapsed=TimeSpan.FromMinutes(1);c.Now+=TimeSpan.FromHours(3);
   var api=new FakeApi(this){Offline=true};var m=Manager(api,st,c);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.Active&&api.Paths.Count==0&&m.Context.Caption=="已激活");
   Check(m.Context.TrustedNow==clock.Now.AddHours(3));m.Context.Demand(FeatureCapability.Cleanup);
  });
  await Test("Same boot NTP correction uses monotonic elapsed and stays active",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);c.Now-=TimeSpan.FromHours(1);c.Elapsed+=TimeSpan.FromMinutes(10);
   var m=Manager(new FakeApi(this){Offline=true},st,c);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.Active&&m.Context.TrustedNow==clock.Now.AddMinutes(10));
  });
  await Test("Offline rollback cannot unlock when network fails and retries are bounded",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);c.Boot="second";c.Now-=TimeSpan.FromHours(1);
   var api=new FakeApi(this){Offline=true};var m=Manager(api,st,c);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.ClockRollbackSuspected&&m.Context.Countdown=="时间待验证");
   var requests=api.Paths.Count;await m.TickAsync();Check(api.Paths.Count==requests);
   Throws(()=>m.Context.Demand(FeatureCapability.Cleanup));
   Check(st.Read<OfflineActivationRecord>("offline-license.dat") is not null);
  });
  await Test("Missing checkpoint can recover only through a signed server lease",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);st.Write<TrustedTimeState?>("trusted-time.dat",null);
   var api=new FakeApi(this);var m=Manager(api,st,c);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.Active&&api.Paths.SequenceEqual(new[]{"offline/challenge","offline/refresh"}));
   Check(st.Read<OfflineActivationRecord>("offline-license.dat") is not null&&st.Read<SavedLicense>("license.dat") is null);
  });
  await Test("Missing checkpoint and offline network keep time error not invalid signature",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);st.Write<TrustedTimeState?>("trusted-time.dat",null);
   var m=Manager(new FakeApi(this){Offline=true},st,c);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.ClockRollbackSuspected&&m.Context.Countdown=="时间待验证");
  });
  await Test("Fast offline local clock cannot block verified server recovery forever",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);var record=st.Read<OfflineActivationRecord>("offline-license.dat")!;
   st.Write("offline-license.dat",record with{Lease=record.Lease with{ServerTime=clock.Now.AddHours(1),IssuedAt=clock.Now.AddHours(1)}});
   st.Write<TrustedTimeState?>("trusted-time.dat",null);
   var m=Manager(new FakeApi(this),st,c);await m.InitializeAsync();Check(m.Context.State==LicenseState.Active);
  });
  await Test("Offline recovery preserves total expiry instead of a 72-hour online lease",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);st.Write<TrustedTimeState?>("trusted-time.dat",null);
   var m=Manager(new FakeApi(this){Permanent=false},st,c);await m.InitializeAsync();
   Check(m.Context.Lease!.ExpiresAt==Lease().LicenseExpiresAt&&m.Context.Lease.RenewalProtocol=="offline-v2");
   c.Boot="after-recovery";c.Now+=TimeSpan.FromDays(4);c.Elapsed=TimeSpan.FromMinutes(1);
   var api=new FakeApi(this){Offline=true};var restarted=Manager(api,st,c);await restarted.InitializeAsync();
   Check(restarted.Context.State==LicenseState.Active&&api.Paths.Count==0);
  });
  await Test("DPAPI offline record and checkpoint survive upgrade-style reconstruction",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);var folder=Path.Combine(root,"encrypted-offline");
   var disk=new ProtectedStore(folder);disk.Write("offline-license.dat",st.Read<OfflineActivationRecord>("offline-license.dat"));
   disk.Write("trusted-time.dat",st.Read<TrustedTimeState>("trusted-time.dat"));
   c.Boot="new-boot";c.Now+=TimeSpan.FromHours(2);c.Elapsed=TimeSpan.FromMinutes(1);
   var api=new FakeApi(this){Offline=true};
   var m=new LicenseManager(new LicenseApi(new(),api),verifier,new FakeDevice(),new ProtectedStore(folder),c,log);
   await m.InitializeAsync();m.Checkpoint();Check(m.Context.State==LicenseState.Active&&api.Paths.Count==0);
   var again=new LicenseManager(new LicenseApi(new(),api),verifier,new FakeDevice(),new ProtectedStore(folder),c,log);
   await again.InitializeAsync();Check(again.Context.State==LicenseState.Active);
  });
  await Test("Offline expiry never keeps a misleading active countdown",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);c.Boot="second";c.Now+=TimeSpan.FromDays(8);
   var m=Manager(new FakeApi(this){Offline=true},st,c);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.Expired&&m.Context.Countdown=="已到期");
  });
  await Test("Offline device mismatch is explained and never rebinds automatically",async()=>{
   var c=new FakeClock();var st=OfflineStore(c);var record=st.Read<OfflineActivationRecord>("offline-license.dat")!;
   st.Write("offline-license.dat",record with{Lease=record.Lease with{DeviceId="other-device"}});
   var api=new FakeApi(this);var m=Manager(api,st,c);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.DeviceMismatch&&m.Context.Caption=="设备待验证"&&api.Paths.Count==0);
  });
  await Test("Safety cleanup protects license, trusted time, device and DPAPI key stores",()=>{
   var p=new SafetyPolicy();var program=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
   foreach(var name in new[]{"license.dat","offline-license.dat","trusted-time.dat","device.dat","trusted-time.dat.tmp"})
   {
    var path=Path.Combine(program,"CleanC","License","test-sid",name);
    Check(p.Classify(SnapshotAge(path,365),DateTime.UtcNow).Safety==SafetyLevel.Protected);
    Check(p.ClassifyPath(path,false).Safety==SafetyLevel.Protected);
   }
   var roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
   foreach(var name in new[]{"Crypto","Protect","Credentials"})
    Check(p.Classify(SnapshotAge(Path.Combine(roaming,"Microsoft",name,"key-material"),365),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("DISM progress streams CR updates and keeps output for reports",async()=>{
   var values=new List<RepairProgress>();var output="start\r[ 10.0% ]\r[ 42.5% ]\r[100.0%]\ncomplete";
   var actual=await ComponentCommandOutput.ReadAsync(new StringReader(output),"清理",new InlineProgress(values.Add));
   Check(actual==output&&values.Any(x=>x.Percent==10)&&values.Any(x=>x.Percent==42)&&values.Any(x=>x.Percent==100));
  });
  await Test("Invalid DISM percentages cannot fake completion",async()=>{
   var values=new List<RepairProgress>();
   await ComponentCommandOutput.ReadAsync(new StringReader("200%\nversion 10.0\n"),"清理",new InlineProgress(values.Add));
   Check(values.Count==0);
  });
  await Test("DISM cleanup reports preparation and final verification stages",async()=>{
   var host=new FakeComponentHost();host.Results.Enqueue((0,ComponentFixture));host.Results.Enqueue((0,"done"));host.Results.Enqueue((0,ComponentFixture));
   var values=new List<RepairProgress>();var result=await new ComponentStoreService(gate,log,host).CleanupAsync(new InlineProgress(values.Add));
   Check(result.Completed&&values.Any(x=>x.Stage.Contains("清理前"))&&values.Any(x=>x.Stage.Contains("验证结果")));
  });
  await Test("Component analysis can run while scanner processes fixture files",async()=>{
   var host=new WaitingComponentHost();var service=new ComponentStoreService(gate,log,host);
   var analysis=service.AnalyzeAsync();Check(service.IsRunning&&!analysis.IsCompleted);
   var folder=Path.Combine(root,"parallel-scan");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"a.tmp"),"fixture");
   var db=new CleanC.Scanner.ScanDatabase(Path.Combine(root,"parallel.db"));
   try
   {
    var scan=await new CleanC.Scanner.DirectoryScanner(gate,policy,db,log).ScanAsync(folder,null,CancellationToken.None);
    Check(scan.Files==1&&!analysis.IsCompleted);
   }
   finally{host.Done.SetResult((0,ComponentFixture));await analysis;}
   Check(!service.IsRunning);
  });
 }
 sealed class InlineProgress(Action<RepairProgress> action):IProgress<RepairProgress>{public void Report(RepairProgress value)=>action(value);}
 sealed class WaitingComponentHost:IComponentStoreHost
 {
  public bool IsAdministrator=>true;public bool? RestartPending=>false;
  public TaskCompletionSource<(int ExitCode,string Output)> Done{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public Task<(int ExitCode,string Output)> RunAsync(string args,IProgress<RepairProgress>? progress)=>Done.Task;
 }
}
