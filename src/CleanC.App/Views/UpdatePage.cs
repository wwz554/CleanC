using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanC.App;
public sealed partial class MainWindow
{
 bool updateBusy;
 async Task CheckForUpdatesAsync(bool automatic=false)
 {
  if(updateBusy||AnyTaskRunning)return;updateBusy=true;
  try{
   var update=await UpdateService.CheckAsync();
   if(update is null){if(!automatic)await Notice("软件更新","当前已是最新版本。");return;}
   if(AnyTaskRunning)return;
   if(!await Confirm("发现 CleanC "+update.Version,"当前版本 "+UpdateService.Current+"。\n"+update.Notes+"\n更新会保留授权和本地数据。", "下载并更新"))return;
   using var cancel=new CancellationTokenSource();var bar=new ProgressBar{Minimum=0,Maximum=100,Value=0,Width=380};var text=Ui.T("正在下载更新…",14);
   var dialog=new ContentDialog{XamlRoot=shell.XamlRoot,Title="更新 CleanC",Content=Ui.Stack(16,text,bar),CloseButtonText="取消",RequestedTheme=shell.RequestedTheme};dialog.CloseButtonClick+=(_,_)=>cancel.Cancel();
   string? installer=null;Exception? error=null;
   dialog.Opened+=async(_,_)=>{try{installer=await UpdateService.DownloadAsync(update,new Progress<double>(value=>{bar.Value=value;text.Text=$"正在下载更新… {value:F0}%";}),cancel.Token);text.Text="下载校验完成，准备安装…";}catch(Exception e){error=e;}finally{dialog.Hide();}};
   await ShowDialogAsync(dialog);
   if(cancel.IsCancellationRequested)return;if(error is not null)throw error;if(installer is null)return;
   if(AnyTaskRunning)throw new InvalidOperationException("任务仍在执行，稍后重新点击更新。");
   services.License.Checkpoint();UpdateService.Install(installer);
   allowFinalClose=true;Close();Application.Current.Exit();
  }catch(OperationCanceledException){}catch(Exception e){if(!automatic)await Notice("更新未完成",e.Message);else SetStatus("自动检查更新失败，可在设置中重试。");}
  finally{updateBusy=false;}
 }
}
