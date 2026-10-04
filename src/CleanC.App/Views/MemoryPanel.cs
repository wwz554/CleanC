using CleanC.Core;
using CleanC.Repair;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanC.App;
public sealed partial class MainWindow
{
 ContentControl? memoryPanel;UIElement? memoryViewCache;TextBlock? memorySnapshotText,memoryExplanationText,memoryObservationText;
 CancellationTokenSource? memoryObservationCancellation;bool memoryBusy;
 void ShowMemory()
 {
  services.License.Context.Demand(FeatureCapability.SystemRepair);
  if(memoryViewCache is null){
   memorySnapshotText=Ui.T("正在读取 Windows 内存计数…",14,true);
   memoryExplanationText=Ui.T("",12,false,Ui.Muted);memoryObservationText=Ui.T("尚未观察：一次高占用不等于泄漏。",12,false,Ui.Muted);
   memoryPanel=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch};
   memoryPanel.Content=Ui.GlassCard(Ui.T("点击“分析进程”查看私有提交和物理工作集。回收默认不选择任何进程。",13,false,Ui.Muted),new Thickness(22));
   memoryViewCache=Ui.Stack(20,Heading("MEMORY INSIGHTS","内存分析与回收","微软 Windows 公开接口 · 区分提交额度、物理内存与缓存，先定位再处理。"),
    Ui.GlassCard(Ui.Stack(12,memorySnapshotText,memoryExplanationText),new Thickness(22)),
    Ui.Row(8,Ui.Button("分析进程",()=>_=Guard(AnalyzeMemory),true),
     Ui.Button("观察 60 秒增长",()=>_=Guard(ObserveMemory)),Ui.Button("停止观察",()=>memoryObservationCancellation?.Cancel())),
    Ui.GlassCard(memoryObservationText,new Thickness(22)),memoryPanel,
    Ui.GlassCard(Ui.Stack(10,Ui.T("微软诊断工具与安全边界",18,true),
     Ui.T("提交上限随 RAM、分页文件和崩溃转储需求变化，这本身不是 Bug。必须观察增长趋势，不根据一个数字断言泄漏。不要关闭分页文件、禁用服务或随意修改 NDU / SysMain 注册表。",12,false,Ui.Muted),
     Ui.T("回收只调用 EmptyWorkingSet，交回所选后台程序的部分物理页。不会取消程序私有提交、修复泄漏、结束程序或清空系统待机列表；再次使用可能重新加载并暂时变慢。",12,false,Ui.Muted),
     Ui.Row(8,Ui.Button("RAMMap 官方说明",()=>OpenWeb("https://learn.microsoft.com/en-us/sysinternals/downloads/rammap")),
      Ui.Button("VMMap 官方说明",()=>OpenWeb("https://learn.microsoft.com/en-us/sysinternals/downloads/vmmap")),
      Ui.Button("Windows 性能监视器",()=>OpenMicrosoftPerformanceMonitor()))),new Thickness(22)));
  }
  pageHost.Content=memoryViewCache;UpdateMemorySnapshot();
 }
 void UpdateMemorySnapshot()
 {
  if(memorySnapshotText is null)return;
  try{var m=new MemoryService(services.License.Context,services.Log).Read();memorySnapshotText.Text=MemoryText(m);memoryExplanationText!.Text=MemoryService.Explain(m);}
  catch(Exception e){memorySnapshotText.Text="Windows 内存计数读取失败："+e.Message;}
 }
 void OpenMicrosoftPerformanceMonitor()
 {
  try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=Path.Combine(Environment.SystemDirectory,"perfmon.exe"),UseShellExecute=true});}
  catch(Exception e){SetStatus("无法打开 Windows 性能监视器："+e.Message);}
 }
 async Task AnalyzeMemory()
 {
  if(memoryBusy){SetStatus("内存任务正在进行；可以切换页面，任务不会重复启动。");return;}
  memoryBusy=true;Interlocked.Increment(ref repairUiWorkflowCount);
  try{
   var service=new MemoryService(services.License.Context,services.Log);
   var data=await Task.Run(()=> (Snapshot:service.Read(),Targets:service.Analyze()));
   var selected=new HashSet<MemoryTarget>();var controls=Ui.Stack(10);
   controls.Children.Add(Ui.T("进程内存 · 按私有提交排序",20,true));
   controls.Children.Add(Ui.T("私有提交 = 程序仍持有的分配；工作集 = 驻留物理内存。两列不能相加，也不等于分页文件实际使用量。系统池 / 共享提交不全部归入进程；权限不足或已退出的进程可能无法列出。",12,false,Ui.Muted));
   var result=Ui.T("",12,false,Ui.Muted);var trim=Ui.Button("回收所选后台工作集",()=>{},true);trim.IsEnabled=false;
   var list=Ui.Stack(8);
   foreach(var target in data.Targets){
    var check=new CheckBox{Content=$"{target.Name} · PID {target.Pid}\n私有提交 {Display.Bytes(target.PrivateBytes)} · 工作集 {Display.Bytes(target.WorkingSet)}"+(target.Eligible?"":" · 仅诊断，不回收"),IsEnabled=target.Eligible};
    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(check,target.Reason);
    check.Click+=(_,_)=>{if(check.IsChecked==true)selected.Add(target);else selected.Remove(target);trim.IsEnabled=selected.Count>0;};list.Children.Add(check);
   }
   controls.Children.Add(new ScrollViewer{Content=list,MaxHeight=380,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
   trim.Click+=async(_,_)=>await Guard(async()=>{
    if(selected.Count==0)return;
    if(AnyTaskRunning||memoryBusy){await Notice("任务正在运行","请等待扫描或维护任务完成后再回收工作集。");return;}
    if(!await Confirm("回收后台工作集","不会关闭程序，但部分页需重新加载，程序可能暂时变慢。不会消除程序私有提交或修复泄漏。","回收所选"))return;
    if(AnyTaskRunning||memoryBusy)return;
    memoryBusy=true;Interlocked.Increment(ref repairUiWorkflowCount);controls.IsHitTestVisible=false;result.Text="正在重新复核进程身份并回收…";
    try{
     var report=await service.TrimAsync(selected);
     result.Text=$"完成 {report.Succeeded} 个，跳过 {report.Skipped} 个；所选工作集减少 {Display.Bytes(report.WorkingSetReduction)}。\n可用内存 {Display.Bytes((long)report.Before.Available)} → {Display.Bytes((long)report.After.Available)}；系统提交 {Display.Bytes((long)report.Before.Commit)} → {Display.Bytes((long)report.After.Commit)}。其他程序也会改变这些数值，不承诺永久释放。";
     selected.Clear();foreach(var check in list.Children.OfType<CheckBox>())check.IsChecked=false;trim.IsEnabled=false;SetStatus("工作集回收完成，未结束程序。请重新分析最新占用。");UpdateMemorySnapshot();
    }finally{controls.IsHitTestVisible=true;memoryBusy=false;Interlocked.Decrement(ref repairUiWorkflowCount);TryFinishPendingClose();}
   });
   controls.Children.Add(trim);controls.Children.Add(result);memoryPanel!.Content=Ui.GlassCard(controls,new Thickness(22));
  }finally{memoryBusy=false;Interlocked.Decrement(ref repairUiWorkflowCount);TryFinishPendingClose();}
 }
 async Task ObserveMemory()
 {
  if(memoryBusy){SetStatus("内存分析或观察正在进行。");return;}
  memoryBusy=true;memoryObservationCancellation=new();var token=memoryObservationCancellation.Token;Interlocked.Increment(ref repairUiWorkflowCount);
  try{
   var service=new MemoryService(services.License.Context,services.Log);var first=await Task.Run(service.Sample,token);MemorySample last=first;
   for(int i=1;i<=6;i++){
    memoryObservationText!.Text=$"正在只读观察 · {(i-1)*10} / 60 秒。可以切换页面，也可停止观察；不会释放或结束程序。";
    await Task.Delay(TimeSpan.FromSeconds(10),token);last=await Task.Run(service.Sample,token);
   }
   var growth=MemoryService.Compare(first,last);
   string Delta(long n)=>(n>=0?"+":"−")+Display.Bytes(Math.Abs(n));
   memoryObservationText!.Text=$"60 秒系统提交变化 {Delta((long)last.Snapshot.Commit-(long)first.Snapshot.Commit)}；分页池 {Delta((long)last.Snapshot.PagedPool-(long)first.Snapshot.PagedPool)}；非分页池 {Delta((long)last.Snapshot.NonPagedPool-(long)first.Snapshot.NonPagedPool)}。\n"+
    string.Join("\n",growth.Where(x=>x.PrivateBytesDelta>0).Select(x=>$"{x.Name} · PID {x.Pid} · 私有提交 {Delta(x.PrivateBytesDelta)} · 工作集 {Delta(x.WorkingSetDelta)}"))+
    "\n这是增长线索，不是泄漏确诊。只比较前后均存在且启动时间相同的高占用进程，正常工作也会增长。持续异常请用微软 VMMap / 性能监视器继续定位。";
  }catch(OperationCanceledException){memoryObservationText!.Text="观察已停止，未回收或结束任何程序。";}
  finally{memoryObservationCancellation?.Dispose();memoryObservationCancellation=null;memoryBusy=false;Interlocked.Decrement(ref repairUiWorkflowCount);TryFinishPendingClose();}
 }
 static string MemoryText(MemorySnapshot m)=>$"物理已用 {Display.Bytes((long)(m.Physical-m.Available))} / {Display.Bytes((long)m.Physical)} · 可用 {Display.Bytes((long)m.Available)}\n已提交 {Display.Bytes((long)m.Commit)} / 提交上限 {Display.Bytes((long)m.CommitLimit)} · 系统缓存计数 {Display.Bytes((long)m.Cache)}\n分页池 {Display.Bytes((long)m.PagedPool)} · 非分页池 {Display.Bytes((long)m.NonPagedPool)}";
}
