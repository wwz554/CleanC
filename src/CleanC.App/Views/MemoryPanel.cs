using CleanC.Core;
using CleanC.Repair;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanC.App;
public sealed partial class MainWindow
{
 ContentControl? memoryPanel;
 UIElement BuildMemoryPanel()
 {
  memoryPanel=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch};
  memoryPanel.Content=Ui.GlassCard(Ui.Stack(12,Ui.T("内存诊断与回收",20,true),
   Ui.T("使用 Microsoft Windows 工作集 API。先分析，再选择后台程序；不结束程序，不关闭服务，不强制清空系统待机缓存。高占用也可能是正常需求或内存泄漏，回收并不能修复泄漏。",12,false,Ui.Muted),
   Ui.Row(10,Ui.Button("分析内存",()=>_=Guard(AnalyzeMemory),true),Ui.Button("微软 RAMMap 说明",()=>OpenWeb("https://learn.microsoft.com/en-us/sysinternals/downloads/rammap")))),new Thickness(22));
  return memoryPanel;
 }
 async Task AnalyzeMemory()
 {
  if(RepairBackgroundWorkRunning||DriverBackgroundWorkRunning||scanRunning||cleanupRunning||cleanupPreparing||cleanupFinalizing){await Notice("任务正在运行","请等待当前任务完成后再分析或回收内存。");return;}
  Interlocked.Increment(ref repairUiWorkflowCount);
  try{
   var service=new MemoryService(services.License.Context,services.Log);
   var data=await Task.Run(()=> (Snapshot:service.Read(),Targets:service.Analyze()));
   var selected=new HashSet<MemoryTarget>();
   var controls=Ui.Stack(10);
   controls.Children.Add(Ui.T("内存诊断",20,true));
   controls.Children.Add(Ui.T(MemoryText(data.Snapshot),13,false,Ui.Muted));
   controls.Children.Add(Ui.T("缓存可由 Windows 自动回收，不等于垃圾；已用 / 提交内存才用于判断实际压力。勾选所需的后台程序，未勾选的一律不处理。",12,false,Ui.Muted));
   var result=Ui.T("",12,false,Ui.Muted);
   var trim=Ui.Button("回收所选后台程序工作集",()=>{},true);trim.IsEnabled=false;
   foreach(var target in data.Targets){
    var check=new CheckBox{Content=$"{target.Name} · PID {target.Pid} · {Display.Bytes(target.WorkingSet)}"+(target.Eligible?"":" · 跳过"),IsEnabled=target.Eligible};
    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(check,target.Reason);
    check.Click+=(_,_)=>{if(check.IsChecked==true)selected.Add(target);else selected.Remove(target);trim.IsEnabled=selected.Count>0;};
    controls.Children.Add(check);
   }
   trim.Click+=async(_,_)=>await Guard(async()=>{
    if(selected.Count==0)return;
    if(RepairBackgroundWorkRunning||DriverBackgroundWorkRunning||scanRunning||cleanupRunning||cleanupPreparing||cleanupFinalizing){await Notice("任务正在运行","请等待其他任务完成，避免影响正在扫描或维护的程序。");return;}
    if(!await Confirm("回收后台工作集","这会让所选后台程序的部分内存页交回 Windows；再次使用时可能重新载入并暂时变慢。不会降低它的实际内存需求，也不会关闭程序。","回收所选"))return;
    if(RepairBackgroundWorkRunning||DriverBackgroundWorkRunning||scanRunning||cleanupRunning||cleanupPreparing||cleanupFinalizing)return;
    Interlocked.Increment(ref repairUiWorkflowCount);controls.IsHitTestVisible=false;result.Text="正在复核身份并回收…";
    try{
     var report=await service.TrimAsync(selected);
     result.Text=$"完成 {report.Succeeded} 个，跳过 {report.Skipped} 个；所选工作集实测减少 {Display.Bytes(report.WorkingSetReduction)}。\n可用内存：{Display.Bytes((long)report.Before.Available)} → {Display.Bytes((long)report.After.Available)}（期间其他程序也会改变占用，不承诺永久释放）。";
     selected.Clear();foreach(var check in controls.Children.OfType<CheckBox>())check.IsChecked=false;trim.IsEnabled=false;SetStatus("内存回收已完成；请重新分析查看当前占用。");
    }finally{controls.IsHitTestVisible=true;Interlocked.Decrement(ref repairUiWorkflowCount);TryFinishPendingClose();}
   });
   controls.Children.Add(trim);controls.Children.Add(result);controls.Children.Add(Ui.Button("重新分析",()=>_=Guard(AnalyzeMemory)));
   memoryPanel!.Content=Ui.GlassCard(controls,new Thickness(22));
  }finally{Interlocked.Decrement(ref repairUiWorkflowCount);TryFinishPendingClose();}
 }
 static string MemoryText(MemorySnapshot m)=>$"已用 {Display.Bytes((long)(m.Physical-m.Available))} / {Display.Bytes((long)m.Physical)} · 可用 {Display.Bytes((long)m.Available)}\n提交 {Display.Bytes((long)m.Commit)} / {Display.Bytes((long)m.CommitLimit)} · 系统缓存 {Display.Bytes((long)m.Cache)}\n分页池 {Display.Bytes((long)m.PagedPool)} · 非分页池 {Display.Bytes((long)m.NonPagedPool)}";
}
