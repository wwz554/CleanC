using CleanC.Core;
using CleanC.Repair;
sealed partial class Tests
{
 async Task Run174Tests()
 {
  await Test("Commit limit is a promise ceiling, not physical RAM consumed",()=>{
   var m=new MemorySnapshot(16UL<<30,6UL<<30,15UL<<30,20UL<<30,5UL<<30,700UL<<20,800UL<<20);
   var text=MemoryService.Explain(m);Check(text.Contains("不是已占用")&&text.Contains("不会取消")&&!text.Contains("接近上限"));
   Check(MemoryService.Explain(m with{Commit=19UL<<30}).Contains("接近上限"));
  });
  await Test("Memory growth compares private commit and rejects reused PID",()=>{
   var m=new MemorySnapshot(16,6,15,20,5,1,1);var now=DateTimeOffset.UtcNow;
   var a=new MemoryTarget(123,"fixture","fixture.exe",1,false,100,"",500);
   var old=new MemorySample(now,m,[a,new(456,"old","old.exe",2,false,100,"",1000)]);
   var last=new MemorySample(now.AddSeconds(60),m,[a with{WorkingSet=80,PrivateBytes=700},new(456,"new","new.exe",3,false,999,"",99999)]);
   var growth=MemoryService.Compare(old,last);Check(growth.Count==1&&growth[0].PrivateBytesDelta==200&&growth[0].WorkingSetDelta==-20);
  });
  await Test("Driver local results publish before online completion independently of UI callbacks",async()=>{
   var host=new WaitingDriverHost();var drivers=new DriverService(gate,log,host);var task=drivers.ScanAsync();
   try{
    Check(host.Started.Wait(5000));var state=drivers.ScanState!;Check(state.Running&&state.LocalResult!.Devices.Count==1&&!task.IsCompleted);
    using var canceled=new CancellationTokenSource();canceled.Cancel();bool refused=false;try{await drivers.ScanAsync(null,canceled.Token);}catch(Exception e)when(e is InvalidOperationException or OperationCanceledException){refused=true;}Check(refused&&host.Reads==1);
   }finally{host.Release.Set();await task;}
   Check(!drivers.ScanState!.Running&&drivers.ScanState.CompletedResult!.OfficialCheckSucceeded&&!drivers.IsScanning&&!drivers.IsRunning);
  });
  await Test("Cancel online query preserves local inventory and unknown update status",async()=>{
   var host=new WaitingDriverHost();var drivers=new DriverService(gate,log,host);using var canceled=new CancellationTokenSource();
   var task=drivers.ScanAsync(null,canceled.Token);Check(host.Started.Wait(5000));canceled.Cancel();var result=await task;
   Check(result.Devices.Count==1&&!result.OfficialCheckSucceeded&&result.OfficialCheckWarning!.Contains("停止")&&!drivers.IsRunning);
  });
  await Test("Online timeout preserves published local snapshot and releases task state",async()=>{
   var host=new WaitingDriverHost{Timeout=true};host.Release.Set();var drivers=new DriverService(gate,log,host);var result=await drivers.ScanAsync();
   Check(result.Devices.Count==1&&!result.OfficialCheckSucceeded&&drivers.ScanState!.CompletedResult==result&&!drivers.IsRunning);
  });
  await Test("Memory reclaim cannot overlap Windows maintenance even with empty selection",async()=>{
   using var write=MaintenanceLock.Enter();bool blocked=false;
   try{await new MemoryService(gate,log).TrimAsync([]);}catch(InvalidOperationException){blocked=true;}Check(blocked);
  });
 }
 sealed class WaitingDriverHost:IDriverScanHost
 {
  public readonly ManualResetEventSlim Started=new(),Release=new();public int Reads;public bool Timeout;
  public IReadOnlyList<DriverInventory> ReadLocalDrivers(CancellationToken token){token.ThrowIfCancellationRequested();Reads++;return [new("FIXTURE","Fixture","NET","Microsoft","Microsoft","1.0",null,"FIXTURE",["FIXTURE"],[],"oem1.inf",true,0,"正常")];}
  public IReadOnlyList<DriverUpdateCandidate> SearchUpdates(CancellationToken token,IProgress<DriverProgress>? progress){Started.Set();Release.Wait(token);if(Timeout)throw new TimeoutException("fixture online timeout");return [];}
 }
}
