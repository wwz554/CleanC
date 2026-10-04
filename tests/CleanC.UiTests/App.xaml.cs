using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CleanC.App;
using CleanC.Core;
using CleanC.Licensing;
using CleanC.Logging;
using CleanC.Scanner;
using CleanC.Repair;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace CleanC.UiTests;
public partial class TestApp:Application
{
 readonly string root=Path.Combine(Path.GetTempPath(),"CleanC-ui-tests-"+Guid.NewGuid().ToString("N"));
 readonly List<string> evidence=[];
 MainWindow window=null!;
 public TestApp(){InitializeComponent();UnhandledException+=(_,e)=>Finish(1,e.Exception.ToString());}
 protected override async void OnLaunched(LaunchActivatedEventArgs args)
 {
  try{
   Directory.CreateDirectory(root);
   // Ephemeral signing fixture and memory-only store. No production key, no
   // customer authorization, no CNG identity creation, no real system maintenance.
   using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
   var clock=new Clock();var store=new Store();var log=new AuditLog(Path.Combine(root,"logs"));
   var now=clock.SystemUtc;var lease=new Lease{Version=4,ApiVersion=3,LicenseId="UI-FIXTURE",DeviceId="DEVICE-UI-FIXTURE",Edition="pro",
    LicenseType="permanent",IsPermanent=true,CountdownRequired=false,Features=["scan","clean","optimize"],IssuedAt=now,ServerTime=now,
    ExpiresAt=now.AddHours(72),LeaseHours=72,RenewalProtocol="challenge-refresh",Nonce="ui-fixture-nonce"};
   var bytes=JsonSerializer.SerializeToUtf8Bytes(lease,SignatureVerifier.Json);
   var envelope=new SignedEnvelope(SignatureVerifier.Encode(bytes),SignatureVerifier.Encode(key.SignData(bytes,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
   var license=new LicenseManager(new LicenseApi(new(),new Api(envelope)),new SignatureVerifier(key.ExportSubjectPublicKeyInfoPem()),new Identity(),store,clock,log);
   await license.ActivateAsync("CLC-UI-FIXT-URE");
   var driverHost=new NavigationDriverHost();
   var services=new AppServices(license,new ScanDatabase(Path.Combine(root,"scan.db")),log,new SafetyPolicy([]),driverHost);
   window=new MainWindow(services);window.Activate();await Until(()=>Get<bool>("initialized"));
   var vm=Get<object>("vm");var scanRoot=Path.Combine(root,"fixture");Directory.CreateDirectory(scanRoot);
   for(int i=0;i<4000;i++)File.WriteAllText(Path.Combine(scanRoot,"fixture-"+i+".bin"),"not user data");
   vm.GetType().GetField("<ScanRoot>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,scanRoot);
   Set("driverUiWorkflowCount",1); // Skip DISM in this UI fixture.
   var scan=(Task)Call("StartScan")!;
   await Task.Delay(20);
   foreach(var page in new[]{"repair","clean","driver","overview","clean","repair"}){Call("Navigate",page);await Task.Delay(20);}
   await scan.WaitAsync(TimeSpan.FromSeconds(30));
   var completedFixtureScan=vm.GetType().GetProperty("LastScan")!.GetValue(vm);
   Check(Get<string>("currentPage")=="repair","scan completion does not steal current page");
   Set("driverUiWorkflowCount",0);
   for(int i=0;i<20;i++){Call("Navigate","clean");Call("Navigate","repair");await Task.Delay(5);}
   Check(!Directory.EnumerateFiles(Path.Combine(root,"logs")).SelectMany(File.ReadLines).Any(l=>l.Contains("NavigationTransition")&&l.Contains("Failed")),"cached view ownership survives rapid navigation");
   typeof(CleanC.Repair.DriverService).GetProperty("IsScanning")!.SetValue(services.Drivers,true);
   Set("driverUiPercent",24d);Set("driverUiStage","fixture driver query 24");Call("Navigate","driver");
   Call("Navigate","overview");Set("driverUiPercent",88d);Set("driverUiStage","fixture driver query 88");Call("Navigate","driver");
   await Task.Delay(200);((FrameworkElement)window.Content).UpdateLayout();
   Check(Texts((DependencyObject)window.Content).Any(t=>t.Contains("fixture driver query 88")),"returning to driver page renders latest background state");
   typeof(CleanC.Repair.DriverService).GetProperty("IsScanning")!.SetValue(services.Drivers,false);
   Set("driverScanResult",new CleanC.Repair.DriverScanResult(now,now,[],0,0,0,false,"fixture online timeout, local inventory preserved"));
   Set("driverProgressView",null);Set("driverViewCache",null);Call("RenderPage");
   await Task.Delay(200);((FrameworkElement)window.Content).UpdateLayout();
   Check(!Texts((DependencyObject)window.Content).Any(t=>t=="正在全面扫描驱动"),"completed driver scan removes progress state");
   var progressType=typeof(MainWindow).Assembly.GetType("CleanC.App.DispatcherProgress`1")!.MakeGenericType(typeof(int));
   int callbacks=0,last=-1;var progress=(IProgress<int>)Activator.CreateInstance(progressType,window.DispatcherQueue,(Action<int>)(v=>{callbacks++;last=v;}),null)!;
   for(int i=0;i<10000;i++)progress.Report(i);await Task.Delay(50);
   Check(callbacks==1&&last==9999,"10000 progress events coalesce to newest value");
   ((IDisposable)progress).Dispose();
   Set("driverUiWorkflowCount",1);var canceledScan=(Task)Call("StartScan")!;
   Get<CancellationTokenSource>("scanCancellation").Cancel();await canceledScan.WaitAsync(TimeSpan.FromSeconds(10));Set("driverUiWorkflowCount",0);
   await Task.Delay(100);((FrameworkElement)window.Content).UpdateLayout();
   Check(!Get<bool>("scanRunning")&&vm.GetType().GetProperty("LastScan")!.GetValue(vm) is null&&Directory.GetFiles(scanRoot).Length==4000,
    "cancel before worker starts releases scan/cache and deletes no files");
   Call("Navigate","driver");var driverTask=(Task)Call("StartDriverScan")!;
   await Until(()=>services.Drivers.ScanState?.LocalResult is not null);await Task.Delay(200);
   Check(!driverTask.IsCompleted&&Texts((DependencyObject)window.Content).Any(t=>t.Contains("本机检测已完成 · 联网查询在后台继续")),
    "actual driver pipeline displays local inventory before online query ends");
   for(int i=0;i<12;i++){Call("Navigate",i%2==0?"memory":"clean");await Task.Delay(5);Call("Navigate","driver");await Task.Delay(10);}
   await Task.Delay(200);((FrameworkElement)window.Content).UpdateLayout();
   Check(driverHost.Reads==1&&driverHost.Searches==1&&services.Drivers.ScanState!.Running&&!Texts((DependencyObject)window.Content).Any(t=>t=="24%"),
    "actual background driver query survives 12 round trips without restart or stale 24 percent");
   Call("Navigate","memory");driverHost.Release.Set();await driverTask.WaitAsync(TimeSpan.FromSeconds(20));
   Check(Get<string>("currentPage")=="memory"&&Get<DriverScanResult>("driverScanResult").UpdateCount==1,
    "actual driver completion while away publishes result without stealing memory page");
   await ((Task)Call("AnalyzeMemory")!).WaitAsync(TimeSpan.FromSeconds(10));
   Check(Get<List<Button>>("navigation").Any(b=>Equals(b.Tag,"memory"))&&Texts((DependencyObject)window.Content).Any(t=>t.Contains("提交上限"))&&
    Descendants((DependencyObject)window.Content).OfType<CheckBox>().All(c=>c.IsChecked!=true),"memory has its own sidebar entry, commit details and no default reclaim selection");
   var observation=(Task)Call("ObserveMemory")!;Get<CancellationTokenSource>("memoryObservationCancellation").Cancel();await observation.WaitAsync(TimeSpan.FromSeconds(10));
   Check(!Get<bool>("memoryBusy")&&Texts((DependencyObject)window.Content).Any(t=>t.Contains("观察已停止")),"memory observation can stop safely without altering processes");
   driverHost.Release.Reset();Call("Navigate","driver");var cancelDriver=(Task)Call("StartDriverScan")!;
   await Until(()=>driverHost.Searches==2);Call("Navigate","overview");Get<CancellationTokenSource>("driverScanCancellation").Cancel();
   await cancelDriver.WaitAsync(TimeSpan.FromSeconds(10));
   Check(Get<string>("currentPage")=="overview"&&Get<DriverScanResult>("driverScanResult") is {OfficialCheckSucceeded:false,Devices.Count:1},
    "cancel online query while away retains local results and unknown update status");
   var transitionGate=Get<SemaphoreSlim>("pageTransitionGate");await transitionGate.WaitAsync();bool staleRender=false;
   var staleTransition=(Task)Call("TransitionContentAsync",(Action)(()=>staleRender=true),false,false,-1)!;
   Call("Navigate","memory");transitionGate.Release();await staleTransition;await Task.Delay(100);
   Check(!staleRender&&Get<string>("currentPage")=="memory","queued background render cannot overwrite a newer navigation");
   vm.GetType().GetProperty("LastScan")!.SetValue(vm,completedFixtureScan);Call("Navigate","space");await Task.Delay(100);
   var metadata=Get<Dictionary<string,List<DirectoryStat>>>("spaceCache");
   for(int i=0;i<70;i++){var path=Path.Combine(scanRoot,"folder-"+i);metadata[path]=[];Call("ShowSpace",path);}
   Check(Get<Dictionary<string,UIElement>>("spaceViewCache").Count<=6&&metadata.Count<=64,"browsing 70 space pages retains at most 6 XAML views and 64 directory datasets");
   var activationType=typeof(MainWindow).Assembly.GetType("CleanC.App.ActivationPage")!;
   var activation=(FrameworkElement)Activator.CreateInstance(activationType,
    (Func<OfflineActivationSession>)(()=>new("DEVICE-UI-FIXTURE","fixture-public-key",now)),
    (Func<string,Task>)(_=>Task.CompletedTask),(Func<string,string?,Task>)((_,_)=>Task.CompletedTask),
    (Action)(()=>{}),(Func<Task<string?>>)(()=>Task.FromResult<string?>(null)),null,null)!;
   window.Content=activation;activationType.GetMethod("ShowCode")!.Invoke(activation,null);await Task.Delay(50);
   Check(Descendants(activation).OfType<TextBox>().Count()==1&&!Texts(activation).Any(t=>t.Contains("导入签名")),"offline input page has one PIN box and no file import");
   Finish(0,"all UI fixtures passed");
  }catch(Exception e){Finish(1,e.ToString());}
 }
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
 T Get<T>(string name)=>(T)typeof(MainWindow).GetField(name,Flags)!.GetValue(window)!;
 void Set(string name,object? value)=>typeof(MainWindow).GetField(name,Flags)!.SetValue(window,value);
 object? Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,Flags)!.Invoke(window,args);
 static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var d in Descendants(VisualTreeHelper.GetChild(root,i)))yield return d;}
 static IEnumerable<string> Texts(DependencyObject root)=>Descendants(root).OfType<TextBlock>().Select(t=>t.Text);
 static async Task Until(Func<bool> condition){var until=DateTime.UtcNow.AddSeconds(10);while(!condition()){if(DateTime.UtcNow>until)throw new TimeoutException();await Task.Delay(20);}}
 void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);evidence.Add("PASS "+name);}
 void Finish(int code,string result){Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"result.txt"),string.Join(Environment.NewLine,evidence.Append(result)));Environment.Exit(code);}
 sealed class Clock:ITimeSource{readonly DateTimeOffset start=DateTimeOffset.UtcNow;public DateTimeOffset SystemUtc=>DateTimeOffset.UtcNow;public TimeSpan Uptime=>DateTimeOffset.UtcNow-start;public string BootId=>"UI-FIXTURE";}
 sealed class Identity:IDeviceIdentity{public string DeviceId=>"DEVICE-UI-FIXTURE";public string PublicKeyPem=>"fixture";public string Sign(string nonce)=>throw new InvalidOperationException("No online refresh in UI fixture");}
 sealed class Store:IProtectedStore{readonly Dictionary<string,string> values=[];public T? Read<T>(string name)=>values.TryGetValue(name,out var v)?JsonSerializer.Deserialize<T>(v,SignatureVerifier.Json):default;public void Write<T>(string name,T value)=>values[name]=JsonSerializer.Serialize(value,SignatureVerifier.Json);}
 sealed class Api(SignedEnvelope envelope):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{success=true,signedPayload=envelope.SignedPayload,signature=envelope.Signature}),System.Text.Encoding.UTF8,"application/json")});}
 sealed class NavigationDriverHost:IDriverScanHost
 {
  public readonly ManualResetEventSlim Release=new();public int Reads,Searches;
  public IReadOnlyList<DriverInventory> ReadLocalDrivers(CancellationToken token){token.ThrowIfCancellationRequested();Interlocked.Increment(ref Reads);return [new("UI-DEVICE-FIXTURE","Fixture device","NET","Microsoft","Microsoft","1.0",null,"FIXTURE",["FIXTURE"],[],"oem1.inf",true,0,"正常")];}
  public IReadOnlyList<DriverUpdateCandidate> SearchUpdates(CancellationToken token,IProgress<DriverProgress>? progress)
  {
   Interlocked.Increment(ref Searches);Release.Wait(token);
   if(Environment.GetEnvironmentVariable("CLEANC_UI_WUA_TESTS")=="1"){
    dynamic session=Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;dynamic query=session.CreateUpdateSearcher();object? result=null;
    try{query.Online=false;result=WindowsUpdateSearch.Run((object)query,"IsInstalled=0 and Type='Driver' and IsHidden=0",token);
     if(Convert.ToInt32(((dynamic)result).ResultCode)!=2)throw new Exception("Cached WUA query incomplete");}
    finally{foreach(var obj in new[]{result,(object)query,(object)session})if(obj is not null)System.Runtime.InteropServices.Marshal.ReleaseComObject(obj);}
   }
   return [new("fixture-update","Fixture Driver 2.0","FIXTURE","Microsoft","fixture","Microsoft","NET","2.0",DateTimeOffset.UtcNow,false)];
  }
 }
}
