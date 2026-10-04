using CleanC.Core;
using CleanC.Licensing;
using CleanC.Repair;
sealed partial class Tests
{
 async Task Run173Tests()
 {
  await Test("Aged Windows Update ETL logs are narrow diagnostics, not component deletion",()=>{
   var p=new SafetyPolicy();var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"Logs\WindowsUpdate");
   var file=Path.Combine(dir,"WindowsUpdate.20260601.112451.395.1.etl");
   Check(p.Classify(SnapshotAge(file,60),DateTime.UtcNow).DefaultSelected);
   Check(p.Classify(SnapshotAge(file,2),DateTime.UtcNow).Safety==SafetyLevel.Optional);
   foreach(var bad in new[]{Path.Combine(dir,"personal.etl"),Path.Combine(dir,"settings.json"),Path.Combine(dir,"nested","WindowsUpdate.20260601.112451.395.1.etl")})
    Check(p.Classify(SnapshotAge(bad,60),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("Read-only component analysis does not block safe fixture cleanup",async()=>{
   var host=new WaitingComponentHost();var service=new ComponentStoreService(gate,log,host);var pending=service.AnalyzeAsync();
   try{using var write=MaintenanceLock.Enter();Check(service.IsRunning&&!pending.IsCompleted);}
   finally{host.Done.SetResult((0,ComponentFixture));await pending;}
  });
  foreach(bool permanent in new[]{false,true})
  await Test("Code-only 16 chars persist expiry through reboot without network "+permanent,async()=>{
   var st=new MemoryStore();var api=new FakeApi(this){Offline=true};var c=new FakeClock();
   var m=Manager(api,st,c);var session=m.BeginOfflineActivation();var lease=OfflineLease(permanent);var pin=FixturePin(session,lease);
   await m.CompleteOfflineActivationAsync(pin);Check(api.Paths.Count==0&&m.Context.State==LicenseState.Active);
   Check(m.Context.Lease!.LicenseExpiresAt==lease.LicenseExpiresAt);
   var later=Manager(api,st,c);await later.InitializeAsync();await later.TickAsync();
   Check(later.Context.State==LicenseState.Active&&api.Paths.Count==0);
   if(!permanent){c.Elapsed+=TimeSpan.FromDays(50);await later.TickAsync();Check(later.Context.State==LicenseState.Expired);}
  });
  await Test("Code-only session cannot be used by another scan and wrong PIN is refused",async()=>{
   var api=new FakeApi(this){Offline=true};var m=Manager(api,new MemoryStore());var old=m.BeginOfflineActivation();var pin=FixturePin(old,OfflineLease());
   m.BeginOfflineActivation();await Rejected(()=>m.CompleteOfflineActivationAsync(pin));Check(m.Context.State!=LicenseState.Active);
  });
  await Test("Unsigned code-only marker cannot turn arbitrary v3 into signed authorization",async()=>{
   var st=new MemoryStore();st.Write("offline-license.dat",new OfflineActivationRecord(OfflineLease(),CodeOnly:true));
   var m=Manager(new FakeApi(this){Offline=true},st);await m.InitializeAsync();Check(m.Context.State==LicenseState.InvalidSignature);
  });
  await Test("Code-only persisted device mismatch remains locked without rebinding",async()=>{
   var st=new MemoryStore();var m=Manager(new FakeApi(this){Offline=true},st);var session=m.BeginOfflineActivation();
   await m.CompleteOfflineActivationAsync(FixturePin(session,OfflineLease()));
   var record=st.Read<OfflineActivationRecord>("offline-license.dat")!;st.Write("offline-license.dat",record with{Lease=record.Lease with{DeviceId="OTHER"}});
   var later=Manager(new FakeApi(this){Offline=true},st);await later.InitializeAsync();Check(later.Context.State!=LicenseState.Active&&later.DeviceId=="DEVICE-TEST");
  });
  await Test("Bounded query wait completes, times out and cancels",()=>{
   BoundedOperation.Wait(()=>true,TimeSpan.FromMilliseconds(1),CancellationToken.None);
   bool timeout=false;try{BoundedOperation.Wait(()=>false,TimeSpan.FromMilliseconds(1),CancellationToken.None);}catch(TimeoutException){timeout=true;}Check(timeout);
   using var c=new CancellationTokenSource();c.Cancel();bool canceled=false;try{BoundedOperation.Wait(()=>false,TimeSpan.FromDays(1),c.Token);}catch(OperationCanceledException){canceled=true;}Check(canceled);
  });
  await Test("Memory policy rejects foreground, system, visible, critical, other user and session",()=>{
   const long size=128*1024*1024;
   Check(MemoryService.Allowed("TestBackground",false,true,true,false,false,false,size));
   Check(!MemoryService.Allowed("TestBackground",true,true,true,false,false,false,size));
   Check(!MemoryService.Allowed("TestBackground",false,false,true,false,false,false,size));
   Check(!MemoryService.Allowed("TestBackground",false,true,false,false,false,false,size));
   Check(!MemoryService.Allowed("TestBackground",false,true,true,true,false,false,size));
   Check(!MemoryService.Allowed("TestBackground",false,true,true,false,true,false,size));
   Check(!MemoryService.Allowed("TestBackground",false,true,true,false,false,true,size));
   Check(!MemoryService.Allowed("svchost",false,true,true,false,false,false,size));
   Check(!MemoryService.Allowed("CleanC",false,true,true,false,false,false,size));
   Check(!MemoryService.Allowed("TestBackground",false,true,true,false,false,false,1024));
  });
  await Test("Native memory diagnostics only, no process trimming",()=>{
   var memory=new MemoryService(gate,log);var snapshot=memory.Read();
   Check(snapshot.Physical>0&&snapshot.Available<=snapshot.Physical&&snapshot.CommitLimit>=snapshot.Commit);
   var targets=memory.Analyze();Check(targets.Count<=40&&targets.All(x=>x.Started>0&&x.Path.Length>0&&x.PrivateBytes>=0));
  });
  if(Environment.GetEnvironmentVariable("CLEANC_WUA_TESTS")=="1")
  await Test("Real WUA asynchronous cached search (read only)",()=>{
   dynamic session=Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
   dynamic query=session.CreateUpdateSearcher();query.Online=false;
   object result=WindowsUpdateSearch.Run((object)query,"IsInstalled=0 and Type='Driver' and IsHidden=0",CancellationToken.None);
   Check(Convert.ToInt32(((dynamic)result).ResultCode)==2);
   foreach(var obj in new[]{result,(object)query,(object)session})System.Runtime.InteropServices.Marshal.FinalReleaseComObject(obj);
  });
  await Test("Upgrade rollback and pending installer components are never direct garbage",()=>{
   var p=new SafetyPolicy();var drive=Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
   foreach(var name in new[]{"Windows.old","$WINDOWS.~BT","$WINDOWS.~WS","$WinREAgent","ESD"})
    Check(p.Classify(SnapshotAge(Path.Combine(drive,name,"keep.bin"),365),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
 }
}
