using System.Diagnostics;
using System.Runtime.InteropServices;
using CleanC.Core;
using CleanC.Licensing;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Graphics;
namespace CleanC.App;
public sealed partial class MainWindow : Window
{
 readonly AppServices services;
 readonly ShellViewModel vm=new();
 readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
 Grid shell=new();Grid pageStage=new();ContentControl pageHost=new(),pageHostA=new(),pageHostB=new();ScrollViewer? pageScroll,pageScrollA,pageScrollB;Border? navIndicator;TextBlock footer=new();TextBlock licenseCaption=new();TextBlock licenseDot=new();TextBlock licenseCountdown=new();
 StackPanel nav=new();bool initialized;bool closingPending;bool tickBusy;bool hiddenShutdownStarted,allowFinalClose,pendingShowAfterMaintenance;volatile bool databaseMaintenanceRunning;long shutdownGeneration;string currentPage="overview";int navigationTransitionVersion;readonly SemaphoreSlim pageTransitionGate=new(1,1);readonly object databaseMaintenanceSync=new();
 readonly SemaphoreSlim dialogGate=new(1,1);
 Task<CleanC.Scanner.ExitDatabaseCleanupResult>? databaseMaintenanceTask;
 CancellationTokenSource? scanCancellation,cleanupCancellation,fileMoveCancellation;
 volatile bool scanRunning,cleanupRunning,cleanupFinalizing,fileMoveRunning;int transientDatabaseReaders;
 bool TransientDatabaseReadRunning=>Volatile.Read(ref transientDatabaseReaders)>0;
 bool AnyTaskRunning=>scanRunning||cleanupRunning||cleanupFinalizing||fileMoveRunning||RepairBackgroundWorkRunning||DriverBackgroundWorkRunning;
 bool PreservedBackgroundWorkRunning=>cleanupRunning||cleanupFinalizing||fileMoveRunning||RepairBackgroundWorkRunning||DriverBackgroundWorkRunning;
 LicenseState previousState=LicenseState.Uninitialized;
 readonly List<Button> navigation=[];
 nint windowIconSmall,windowIconBig;bool windowIconApplied;
 UIElement? overviewIdleView,overviewScanningView;
 void InvalidateOverviewCache(){overviewIdleView=null;overviewScanningView=null;}
 public MainWindow(AppServices services)
 {
  this.services=services;Title="CleanC";ExtendsContentIntoTitleBar=true;
  try
  {
   var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
   var dpi=Math.Max(1.0,GetDpiForWindow(hwnd)/96.0);
   var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary);
   if(area is not null)AppWindow.Resize(new SizeInt32(Math.Max(900,Math.Min((int)(1280*dpi),area.WorkArea.Width-32)),Math.Max(620,Math.Min((int)(800*dpi),area.WorkArea.Height-32))));
   else AppWindow.Resize(new SizeInt32(1280,800));
   if(AppWindow.Presenter is OverlappedPresenter presenter){presenter.PreferredMinimumWidth=900;presenter.PreferredMinimumHeight=620;}
  }
  catch(Exception e){services.Log.Write("App","WindowSizing","Fallback",detail:e.GetType().Name);try{AppWindow.Resize(new SizeInt32(1280,800));}catch{}}
  try{if(Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())SystemBackdrop=new MicaBackdrop();}catch(Exception e){services.Log.Write("App","Backdrop","Disabled",detail:e.GetType().Name);}
  BuildShell();
  AppWindow.Closing+=(_,e)=>{
   if(allowFinalClose)return;
   e.Cancel=true;
   BeginHiddenShutdown();
  };
  Activated+=(_,_)=>ApplyWindowIcons();
  Closed+=(_,_)=>{timer.Stop();ReleaseWindowIcons();};
  timer.Tick+=(_,_)=>OnTimerTick();
  _=Initialize();
 }
 const uint WmSetIcon=0x0080,ImageIcon=1,LrLoadFromFile=0x0010;
 const int IconSmall=0,IconBig=1;
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 static extern nint LoadImageW(nint hInst,string name,uint type,int cx,int cy,uint fuLoad);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]
 static extern nint SendMessageW(nint hWnd,uint msg,nint wParam,nint lParam);
 [DllImport("user32.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool DestroyIcon(nint hIcon);
 void ApplyWindowIcons()
 {
  if(windowIconApplied)return;
  try
  {
   var iconPath=System.IO.Path.Combine(AppContext.BaseDirectory,"Assets","CleanC-shell-1.6.8.ico");
   if(!File.Exists(iconPath)){services.Log.Write("App","WindowIcon","Missing",target:iconPath);return;}
   AppWindow.SetIcon(iconPath);
   AppWindow.SetTaskbarIcon(iconPath);
   AppWindow.SetTitleBarIcon(iconPath);
   var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
   if(hwnd!=0)
   {
    windowIconSmall=LoadImageW(0,iconPath,ImageIcon,16,16,LrLoadFromFile);
    windowIconBig=LoadImageW(0,iconPath,ImageIcon,32,32,LrLoadFromFile);
    if(windowIconSmall!=0)SendMessageW(hwnd,WmSetIcon,IconSmall,windowIconSmall);
    if(windowIconBig!=0)SendMessageW(hwnd,WmSetIcon,IconBig,windowIconBig);
   }
   windowIconApplied=true;
   services.Log.Write("App","WindowIcon","Applied",target:iconPath);
  }
  catch(Exception e){services.Log.Write("App","WindowIcon","Fallback",detail:e.ToString());}
 }
 void ReleaseWindowIcons()
 {
  try{if(windowIconSmall!=0){DestroyIcon(windowIconSmall);windowIconSmall=0;}if(windowIconBig!=0){DestroyIcon(windowIconBig);windowIconBig=0;}}catch{}
 }
 async Task Initialize()
 {
  try{await services.License.InitializeAsync();}
  catch(Exception e){services.Log.Write("App","Initialize","Failed",detail:e.ToString());SetStatus(e.Message);}
  finally{initialized=true;}
  previousState=services.License.Context.State;
  try{RenderPage();}
  catch(Exception e){services.Log.Write("App","InitialRender","Failed",detail:e.ToString());SetStatus("界面初始化未完成："+e.Message);}
  timer.Start();
  _=CheckForUpdatesAsync(true);
 }
 void BuildShell()
 {
  Ui.Dark=services.Preferences.Theme=="dark"||(services.Preferences.Theme=="system"&&Application.Current.RequestedTheme==ApplicationTheme.Dark);
  shell=new Grid{RequestedTheme=Ui.Dark?ElementTheme.Dark:ElementTheme.Light,Background=new LinearGradientBrush{StartPoint=new(0,0),EndPoint=new(1,1),GradientStops={new(){Color=Ui.Color(Ui.Dark?"111C2B":"EFF5FF"),Offset=0},new(){Color=Ui.Color(Ui.Dark?"1B2B3B":"F8FBFF"),Offset=.55},new(){Color=Ui.Color(Ui.Dark?"13293A":"E7F1F7"),Offset=1}}}};
  shell.RowDefinitions.Add(new(){Height=new GridLength(48)});shell.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
  var titlebar=new Grid{Margin=new Thickness(24,0,160,0),Height=48};titlebar.Children.Add(Ui.Row(10,Ui.Logo(22),Ui.T("CleanC",13,true)));shell.Children.Add(titlebar);SetTitleBar(titlebar);
  var layout=Ui.Columns(208,-1);layout.ColumnSpacing=0;Grid.SetRow(layout,1);shell.Children.Add(layout);
  var side=new Grid{Padding=new Thickness(16,24,12,20)};side.RowDefinitions.Add(new(){Height=GridLength.Auto});side.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});side.RowDefinitions.Add(new(){Height=GridLength.Auto});
  var brand=Ui.Row(12,Ui.Logo(40),Ui.Stack(2,Ui.T("CleanC",23,true),Ui.T("为每一份数据留心",10,false,Ui.Muted)));brand.Margin=new Thickness(8,0,0,32);Ui.Add(side,brand,0);
  nav=new StackPanel{Spacing=8};navigation.Clear();
  var navHost=new Grid();
  navIndicator=new Border{Height=48,VerticalAlignment=VerticalAlignment.Top,CornerRadius=new CornerRadius(17),Background=Ui.NavigationGlassBrush(),BorderBrush=new SolidColorBrush(Colors.Transparent),BorderThickness=new Thickness(0),IsHitTestVisible=false,Opacity=.94};
  navHost.Children.Add(navIndicator);
  foreach(var (name,glyph,id) in new[]{("概览","\uE80F","overview"),("智能清理","\uE74D","clean"),("空间分析","\uE9D9","space"),("驱动修复","\uE950","driver"),("系统修复","\uE90F","repair"),("设置","\uE713","settings")}){
   var button=Ui.Button("",()=>Navigate(id));button.Content=Ui.Row(16,Ui.Icon(glyph,18),Ui.T(name,14));button.HorizontalAlignment=HorizontalAlignment.Stretch;button.Height=48;button.Tag=id;button.Background=new SolidColorBrush(Colors.Transparent);button.CornerRadius=new CornerRadius(16);button.Opacity=Equals(id,currentPage)?1:.91;
   button.PointerEntered+=(_,_)=>{button.Opacity=.98;if(!Equals(button.Tag,currentPage))button.Background=Ui.NavigationHoverBrush();};
   button.PointerPressed+=(_,_)=>{button.Opacity=1;if(!Equals(button.Tag,currentPage))button.Background=Ui.NavigationPressedBrush();};
   button.PointerReleased+=(_,_)=>{button.Opacity=Equals(button.Tag,currentPage)?1:.98;button.Background=Equals(button.Tag,currentPage)?new SolidColorBrush(Colors.Transparent):Ui.NavigationHoverBrush();};
   button.PointerExited+=(_,_)=>{button.Opacity=Equals(button.Tag,currentPage)?1:.91;button.Background=new SolidColorBrush(Colors.Transparent);};
   navigation.Add(button);nav.Children.Add(button);
  }
  navHost.Children.Add(nav);Grid.SetRow(navHost,1);side.Children.Add(navHost);
  licenseDot=Ui.T("●",12,false,Ui.Muted);licenseCaption=Ui.T("正在验证",12,false,Ui.Muted);licenseCountdown=Ui.T("—",14,true);
  var licenseCard=Ui.Card(Ui.Stack(8,Ui.Row(8,Ui.Icon("\uE73E",16),Ui.T("CleanC",14,true)),Ui.Row(7,licenseDot,licenseCaption),licenseCountdown),new Thickness(16));licenseCard.Tapped+=(_,_)=>Navigate("settings");Grid.SetRow(licenseCard,2);side.Children.Add(licenseCard);Ui.Add(layout,side,0);
  var right=new Grid{Margin=new Thickness(24,20,32,16)};right.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});right.RowDefinitions.Add(new(){Height=GridLength.Auto});
  pageHostA=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Top};
  pageHostB=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Top};
  pageScrollA=new ScrollViewer{Content=pageHostA,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Colors.Transparent)};
  pageScrollB=new ScrollViewer{Content=pageHostB,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Colors.Transparent),Visibility=Visibility.Collapsed};
  pageHost=pageHostA;pageScroll=pageScrollA;
  pageStage=new Grid{Background=new SolidColorBrush(Colors.Transparent)};
  pageStage.Children.Add(pageScrollA);pageStage.Children.Add(pageScrollB);right.Children.Add(pageStage);
  footer=Ui.T(vm.Status,11,false,Ui.Muted);footer.Margin=new Thickness(4,16,0,0);Grid.SetRow(footer,1);right.Children.Add(footer);Ui.Add(layout,right,1);
  Content=shell;RenderPage();UpdateNavigationSelection(false);
 }
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr handle);
 async void Navigate(string id)
 {
  if(databaseMaintenanceRunning){ShowDatabaseMaintenancePage("正在完成退出前数据库维护，维护结束后即可继续使用。");return;}
  if(currentPage==id){UpdateNavigationSelection(true);return;}
  var version=++navigationTransitionVersion;
  currentPage=id;UpdateNavigationSelection(true);
  try{await TransitionContentAsync(RenderPage,false,false,version);}
  catch(Exception e){services.Log.Write("App","NavigationTransition","Failed",id,e.ToString());if(version==navigationTransitionVersion)RenderPage();}
 }

 void UpdateNavigationSelection(bool animate=true)
 {
  var index=Math.Max(0,navigation.FindIndex(b=>Equals(b.Tag,currentPage)));
  for(var i=0;i<navigation.Count;i++){navigation[i].Background=new SolidColorBrush(Colors.Transparent);navigation[i].Opacity=i==index?1:.91;}
  if(navIndicator is null)return;

  var visual=ElementCompositionPreview.GetElementVisual(navIndicator);
  var target=(float)(index*56d);
  visual.StopAnimation("Offset.Y");
  visual.StopAnimation("Opacity");
  visual.Offset=new Vector3(visual.Offset.X,target,visual.Offset.Z);

  if(!animate||!MotionEnabled()){visual.Opacity=.94f;return;}

  // No sliding indicator: the clicked button already provides immediate pointer
  // feedback. The selection glass simply settles in at the new location.
  visual.Opacity=.58f;
  var compositor=visual.Compositor;
  var opacity=compositor.CreateScalarKeyFrameAnimation();
  opacity.Duration=TimeSpan.FromMilliseconds(220);
  var ease=compositor.CreateCubicBezierEasingFunction(new Vector2(.25f,.10f),new Vector2(.25f,1f));
  opacity.InsertKeyFrame(1,.94f,ease);
  visual.StartAnimation("Opacity",opacity);
 }

 bool MotionEnabled()
 {
  try{return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;}
  catch{return true;}
 }

 async Task TransitionContentAsync(Action render,bool preserveScroll=false,bool subtle=false,int expectedVersion=-1)
 {
  await pageTransitionGate.WaitAsync();
  try
  {
   if(expectedVersion>=0&&expectedVersion!=navigationTransitionVersion)return;
   if(!MotionEnabled()){render();return;}

   const int fadeDurationMs=500;
   const int layoutSettleMs=24;

   var outgoingHost=pageHost;
   var outgoingScroll=pageScroll;
   var useB=ReferenceEquals(outgoingHost,pageHostA);
   var incomingHost=useB?pageHostB:pageHostA;
   var incomingScroll=useB?pageScrollB:pageScrollA;
   if(outgoingScroll is null||incomingScroll is null){render();return;}

   var previousOffset=preserveScroll?outgoingScroll.VerticalOffset:0d;
   var outgoingVisual=ElementCompositionPreview.GetElementVisual(outgoingScroll);
   var incomingVisual=ElementCompositionPreview.GetElementVisual(incomingScroll);

   outgoingVisual.StopAnimation("Opacity");
   incomingVisual.StopAnimation("Opacity");

   // Pure crossfade: both pages remain at exactly the same position and scale.
   outgoingVisual.Opacity=1f;
   incomingVisual.Opacity=0f;
   outgoingVisual.Offset=new Vector3(outgoingVisual.Offset.X,0,outgoingVisual.Offset.Z);
   incomingVisual.Offset=new Vector3(incomingVisual.Offset.X,0,incomingVisual.Offset.Z);
   outgoingVisual.Scale=Vector3.One;
   incomingVisual.Scale=Vector3.One;

   incomingHost.Content=null;
   incomingScroll.Visibility=Visibility.Visible;
   incomingScroll.IsHitTestVisible=false;
   outgoingScroll.IsHitTestVisible=false;

   pageHost=incomingHost;
   pageScroll=incomingScroll;
   try
   {
    // Build the next page behind the fully visible current page. The user never
    // sees a blank/white intermediate frame while a heavier view is constructed.
    render();
   }
   catch
   {
    pageHost=outgoingHost;pageScroll=outgoingScroll;
    incomingHost.Content=null;
    incomingScroll.Visibility=Visibility.Collapsed;
    incomingScroll.IsHitTestVisible=false;
    outgoingScroll.IsHitTestVisible=true;
    incomingVisual.Opacity=1f;
    throw;
   }

   incomingScroll.ChangeView(null,preserveScroll?previousOffset:0d,null,true);

   // Allow one layout pass before revealing the new page. This is deliberately
   // short and constant; the visible transition itself is always exactly 500 ms.
   await Task.Delay(layoutSettleMs);

   if(expectedVersion>=0&&expectedVersion!=navigationTransitionVersion)
   {
    incomingHost.Content=null;
    incomingScroll.Visibility=Visibility.Collapsed;
    incomingScroll.IsHitTestVisible=false;
    pageHost=outgoingHost;pageScroll=outgoingScroll;
    outgoingScroll.IsHitTestVisible=true;
    return;
   }

   var compositor=incomingVisual.Compositor;
   var ease=compositor.CreateCubicBezierEasingFunction(new Vector2(.25f,.10f),new Vector2(.25f,1f));

   // Same timing/easing in opposite directions keeps perceived brightness stable:
   // outgoing opacity + incoming opacity stays visually close to a constant mix.
   var fadeIn=compositor.CreateScalarKeyFrameAnimation();
   fadeIn.Duration=TimeSpan.FromMilliseconds(fadeDurationMs);
   fadeIn.InsertKeyFrame(0,0f);
   fadeIn.InsertKeyFrame(1,1f,ease);

   var fadeOut=compositor.CreateScalarKeyFrameAnimation();
   fadeOut.Duration=TimeSpan.FromMilliseconds(fadeDurationMs);
   fadeOut.InsertKeyFrame(0,1f);
   fadeOut.InsertKeyFrame(1,0f,ease);

   incomingVisual.StartAnimation("Opacity",fadeIn);
   outgoingVisual.StartAnimation("Opacity",fadeOut);

   await Task.Delay(fadeDurationMs+20);

   outgoingVisual.StopAnimation("Opacity");
   incomingVisual.StopAnimation("Opacity");
   outgoingVisual.Opacity=1f;
   incomingVisual.Opacity=1f;
   outgoingVisual.Offset=new Vector3(outgoingVisual.Offset.X,0,outgoingVisual.Offset.Z);
   incomingVisual.Offset=new Vector3(incomingVisual.Offset.X,0,incomingVisual.Offset.Z);
   outgoingVisual.Scale=Vector3.One;
   incomingVisual.Scale=Vector3.One;

   outgoingHost.Content=null;
   outgoingScroll.Visibility=Visibility.Collapsed;
   outgoingScroll.IsHitTestVisible=false;
   incomingScroll.Visibility=Visibility.Visible;
   incomingScroll.IsHitTestVisible=true;
  }
  finally{pageTransitionGate.Release();}
 }

 void RenderPage()
 {
  if(databaseMaintenanceRunning){ShowDatabaseMaintenancePage("正在完成退出前数据库维护，维护结束后即可继续使用。");return;}
  if(!initialized){pageHost.Content=Ui.Stack(20,Ui.T("正在本地验证授权…",20,true));return;}
  if(services.License.Context.State!=LicenseState.Active&&!RepairBackgroundWorkRunning&&!DriverBackgroundWorkRunning){ShowActivation();return;}
  switch(currentPage){case "clean":ShowCleanup();break;case "space":ShowSpace(vm.ScanRoot);break;case "driver":ShowDrivers();break;case "repair":ShowRepair();break;case "settings":ShowSettings();break;default:ShowOverview();break;}
 }
 void OnTimerTick()
 {
  if(!initialized)return;
  UpdateLicenseDisplay();
  if(!tickBusy)_=RunLicenseMaintenanceAsync();
 }
 void UpdateLicenseDisplay()
 {
  try
  {
   var state=services.License.Context.State;
   licenseDot.Foreground=state==LicenseState.Active?Ui.Success:Ui.Muted;
   licenseCaption.Text=state==LicenseState.Active?"已激活":state is LicenseState.Expired or LicenseState.ExpiredOffline?"已到期":"未激活";
   licenseCountdown.Text=services.License.Context.Countdown;if(settingsCountdownValue is not null)settingsCountdownValue.Text=services.License.Context.Countdown;
  }
  catch(Exception e){services.Log.Write("App","LicenseDisplay","Failed",detail:e.GetType().Name);}
 }
 async Task RunLicenseMaintenanceAsync()
 {
  if(tickBusy)return;
  tickBusy=true;
  try
  {
   var before=services.License.Context.State;
   await services.License.TickAsync();
   var after=services.License.Context.State;
   if(after!=LicenseState.Active){scanCancellation?.Cancel();cleanupCancellation?.Cancel();}
   if(after!=previousState&&!AnyTaskRunning)await TransitionContentAsync(RenderPage,false,false);
   previousState=after;
   UpdateLicenseDisplay();
  }
  catch(Exception e)
  {
   services.Log.Write("App","LicenseTick","Failed",detail:e.ToString());
   SetStatus("授权状态更新未完成："+e.Message);
  }
  finally{tickBusy=false;}
 }
 void TryFinishPendingClose()
 {
  // The hidden shutdown loop already observes task state. This hook remains for
  // task-finally blocks and only starts a cycle if one is not already active.
  if(closingPending&&!hiddenShutdownStarted)BeginHiddenShutdown();
 }

 public void RequestExternalActivation()
 {
  DispatcherQueue.TryEnqueue(()=>
  {
   bool maintenanceRunning;
   // Serialize redirected activation with the exact instant Stage 3 claims DB
   // ownership. Either activation wins and no rebuild starts, or maintenance
   // wins and activation is shown the DB-safe page; never both at once.
   lock(databaseMaintenanceSync)
   {
    shutdownGeneration++;
    closingPending=false;
    hiddenShutdownStarted=false;
    allowFinalClose=false;
    maintenanceRunning=databaseMaintenanceRunning;
    if(maintenanceRunning)pendingShowAfterMaintenance=true;
   }
   if(maintenanceRunning)
   {
    try{AppWindow.Show(true);}catch{try{Activate();}catch{}}
    timer.Start();
    ShowDatabaseMaintenancePage("CleanC 已收到重新打开请求，正在完成数据库收尾；完成后会恢复原任务页面。");
    return;
   }
   ShowAfterExternalActivation();
  });
 }

 void ShowDatabaseMaintenancePage(string message)
 {
  try
  {
   pageHost.Content=Ui.Stack(24,
    Heading("EXIT MAINTENANCE","正在完成安全收尾","扫描索引数据库正在重建为仅保留历史摘要的精简数据库。此时不会启动新的扫描、清理、驱动或系统修复任务。"),
    Ui.GlassCard(Ui.Stack(14,Ui.Icon("\uE895",28),Ui.T("数据库维护进行中",20,true),Ui.T(message,13,false,Ui.Muted)),new Thickness(24)));
   SetStatus(message);
  }
  catch(Exception e){services.Log.Write("App","DatabaseMaintenanceUi","Failed",detail:e.ToString());}
 }

 void ShowAfterExternalActivation()
 {
  pendingShowAfterMaintenance=false;
  allowFinalClose=false;
  closingPending=false;
  hiddenShutdownStarted=false;

  try{AppWindow.Show(true);}catch{try{Activate();}catch{}}
  timer.Start();

  if(databaseMaintenanceRunning)
  {
   pendingShowAfterMaintenance=true;
   ShowDatabaseMaintenancePage("数据库仍在安全收尾，完成后会恢复原来的页面和任务状态。");
   return;
  }

  // Return to the task that is actually still running. Space Analysis itself is
  // only a dashboard and never counts as background work.
  if(cleanupRunning)currentPage="clean";
  else if(DriverBackgroundWorkRunning){currentPage="driver";driverViewCache=null;}
  else if(RepairBackgroundWorkRunning){currentPage="repair";repairViewCache=null;}
  else if(fileMoveRunning)currentPage="space";
  else if(scanRunning)currentPage="clean";

  UpdateNavigationSelection(false);
  try{RenderPage();}catch(Exception e){services.Log.Write("App","RestoreHiddenUi","Failed",detail:e.ToString());}

  if(cleanupRunning)SetStatus("清理仍在后台执行。");
  else if(DriverBackgroundWorkRunning)SetStatus("驱动任务仍在后台执行，已恢复真实进度。");
  else if(RepairBackgroundWorkRunning)SetStatus("系统检查 / 修复仍在后台执行，已恢复真实进度。");
  else if(fileMoveRunning)SetStatus("文件处理仍在后台执行。");
  else if(scanRunning)SetStatus(scanCancellation?.IsCancellationRequested==true?"C 盘扫描正在协作停止…":"C 盘扫描仍在运行。");
  else SetStatus("CleanC 已恢复。");
 }

 void BeginHiddenShutdown()
 {
  if(hiddenShutdownStarted)return;
  hiddenShutdownStarted=true;
  closingPending=true;
  pendingShowAfterMaintenance=false;
  var generation=++shutdownGeneration;

  // Stage 1: UI disappears immediately.
  try{AppWindow.Hide();}catch{}
  timer.Stop();

  // Keep the current scan-generation Smart Clean cache alive while the process is
  // only hidden. Driver/repair/cleanup tasks may continue for a long time and a
  // redirected launch must reuse the SAME in-memory cache instead of rebuilding it.
  // Stage 3 already waits for transient DB readers to finish before replacing
  // CleanC.db; successful final DB maintenance invalidates the cache afterwards.

  // Only a genuinely RUNNING C-drive scan is cancelled. A completed scan and the
  // Space Analysis dashboard do nothing here. Cleanup/file move/driver/repair are
  // deliberately NOT cancelled.
  if(scanRunning&&scanCancellation is not null&&!scanCancellation.IsCancellationRequested)
  {
   try
   {
    scanStatus="正在停止扫描…";
    scanDetail="已收到退出请求，正在等待扫描线程在安全枚举边界停止。";
    scanCancellation.Cancel();
    services.Log.Write("App","HiddenShutdown","CancelRunningScan",detail:"C-drive scan was Running");
   }
   catch(Exception e){services.Log.Write("App","HiddenShutdown","CancelScanFailed",detail:e.ToString());}
  }

  try{services.License.Checkpoint();}catch(Exception ex){services.Log.Write("App","Checkpoint","Failed",detail:ex.GetType().Name);}
  _=FinishHiddenShutdownAsync(generation);
 }

 Task<CleanC.Scanner.ExitDatabaseCleanupResult>? GetOrStartDatabaseMaintenanceTask(long generation)
 {
  lock(databaseMaintenanceSync)
  {
   // Activation and Stage 3 use the same lock. If this hidden-exit generation
   // was invalidated before DB ownership was acquired, do not start maintenance.
   if(generation!=Volatile.Read(ref shutdownGeneration))return null;

   // While a prior maintenance task is still owned by the exit state machine,
   // every close/reopen cycle observes the SAME task. Never rebuild CleanC.db
   // concurrently.
   if(databaseMaintenanceRunning&&databaseMaintenanceTask is not null)return databaseMaintenanceTask;

   databaseMaintenanceRunning=true;
   databaseMaintenanceTask=Task.Run(() =>
   {
    try{services.Preferences.Save();}
    catch(Exception e){services.Log.Write("App","PreferencesSaveOnExit","Failed",Preferences.SettingsPath,e.ToString());}

    try
    {
     var result=services.Database.CleanupForExit();
     services.Log.Write("App","DatabaseExitCleanup",result.Success?"Completed":"Failed",services.Database.DatabasePath,
      $"entries={result.RemovedEntries}; reports={result.RemovedReports}; before={result.BeforeBytes}; after={result.AfterBytes}; error={result.Error}");
     return result;
    }
    catch(Exception e)
    {
     services.Log.Write("App","DatabaseExitCleanup","Failed",services.Database.DatabasePath,e.ToString());
     return new CleanC.Scanner.ExitDatabaseCleanupResult(false,0,0,0,0,e.Message);
    }
   });
   return databaseMaintenanceTask;
  }
 }

 async Task ApplyCompletedDatabaseMaintenanceAsync(Task<CleanC.Scanner.ExitDatabaseCleanupResult> maintenance,CleanC.Scanner.ExitDatabaseCleanupResult result)
 {
  // CleanC.db no longer contains scan entries after a successful exit rebuild.
  // Invalidate all in-memory views as well so a launch during the 3-second grace
  // period can never act on stale scan/cleanup data that no longer exists in DB.
  if(result.Success)
  {
   var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
   if(!DispatcherQueue.TryEnqueue(()=>
   {
    try
    {
     vm.LastScan=null;
     _=InvalidateCleanupCache();
     ClearSpaceCache();
     InvalidateOverviewCache();
     scanProgressView=null;
    }
    finally{done.TrySetResult(true);}
   }))done.TrySetResult(true);
   await done.Task.ConfigureAwait(false);
  }

  lock(databaseMaintenanceSync)
  {
   if(ReferenceEquals(databaseMaintenanceTask,maintenance))
   {
    databaseMaintenanceRunning=false;
    databaseMaintenanceTask=null;
   }
  }
 }

 void RestoreFromShutdownWatchdog(string status,bool scanTimeout=false,bool maintenancePending=false)
 {
  // This is deliberately a UI restore, not a hard kill. The outstanding task is
  // allowed to reach its own safe completion boundary.
  shutdownGeneration++;
  closingPending=false;
  hiddenShutdownStarted=false;
  allowFinalClose=false;
  pendingShowAfterMaintenance=maintenancePending;
  try{AppWindow.Show(true);}catch{try{Activate();}catch{}}
  timer.Start();
  if(scanTimeout)
  {
   currentPage="clean";
   scanStatus="正在停止扫描…";
   scanDetail="扫描取消超过 10 秒仍未返回。CleanC 已恢复界面，不会通过强杀线程或进程来结束扫描。";
   scanProgressView=null;
   UpdateNavigationSelection(false);
   try{RenderPage();}catch{}
  }
  else if(maintenancePending)ShowDatabaseMaintenancePage(status);
  else
  {
   try{ShowAfterExternalActivation();}catch{}
  }
  SetStatus(status);
 }

 async Task FinishHiddenShutdownAsync(long generation)
 {
  try
  {
   // Stage 1: scanning is read-only against C: and writes only CleanC.db. Ask it
   // to stop cooperatively and wait up to 10 seconds for a safe enumeration
   // boundary. If it does not stop, restore UI instead of killing its thread or
   // terminating the whole process underneath it.
   var scanStopped=await WaitWhileAsync(()=>scanRunning,TimeSpan.FromSeconds(10),generation).ConfigureAwait(false);
   if(generation!=Volatile.Read(ref shutdownGeneration))return;
   if(!scanStopped)
   {
    services.Log.Write("App","HiddenShutdownWatchdog","ScanCancelTimeout",detail:"Scan did not stop in 10 seconds; UI restored and process kept alive. No hard kill was used.");
    DispatcherQueue.TryEnqueue(()=>RestoreFromShutdownWatchdog("扫描仍在安全停止中；为避免强杀扫描线程，CleanC 已恢复界面。",scanTimeout:true));
    return;
   }

   // Stage 2: user-requested write/system tasks continue to completion. The UI
   // level driver/repair workflow flags cover the tiny gaps between service calls,
   // so an install/verify pipeline cannot be mistaken for an idle process.
   var workFinished=await WaitWhileAsync(()=>PreservedBackgroundWorkRunning,TimeSpan.FromHours(6),generation).ConfigureAwait(false);
   if(generation!=Volatile.Read(ref shutdownGeneration))return;

   if(!workFinished)
   {
    // A six-hour task is abnormal. Never hard-kill cleanup, driver install,
    // DISM/SFC/CHKDSK, or a file delete. Make the process visible again instead.
    services.Log.Write("App","HiddenShutdownWatchdog","CriticalTaskTimeout",detail:"Hidden work exceeded 6 hours; restoring UI instead of killing it.");
    DispatcherQueue.TryEnqueue(()=>RestoreFromShutdownWatchdog("后台关键任务运行时间异常偏长，CleanC 已恢复界面并继续等待任务自身结束。"));
    return;
   }

   if(generation!=Volatile.Read(ref shutdownGeneration))return;

   // Drain short-lived DB-backed UI readers (Smart Clean cache / Space tree)
   // before replacing CleanC.db. They are not user tasks, so a timeout restores
   // the UI rather than risking a file-replacement race.
   var readersFinished=await WaitWhileAsync(()=>TransientDatabaseReadRunning,TimeSpan.FromSeconds(10),generation).ConfigureAwait(false);
   if(generation!=Volatile.Read(ref shutdownGeneration))return;
   if(!readersFinished)
   {
    services.Log.Write("App","HiddenShutdownWatchdog","DatabaseReaderTimeout",detail:"Transient UI DB readers did not drain in 10 seconds; UI restored before DB replacement.");
    DispatcherQueue.TryEnqueue(()=>RestoreFromShutdownWatchdog("仍有界面缓存正在读取扫描数据库，CleanC 已恢复界面并暂不替换数据库。"));
    return;
   }

   // Stage 3: settings + deterministic summaries-only database rebuild. The
   // maintenance task is single-owner and shared across reopen/close cycles.
   var maintenance=GetOrStartDatabaseMaintenanceTask(generation);
   if(maintenance is null)return;
   var completed=await Task.WhenAny(maintenance,Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
   if(completed!=maintenance)
   {
    services.Log.Write("App","HiddenShutdownWatchdog","DatabaseTimeout",detail:"Database maintenance exceeded 30 seconds; restoring a DB-safe UI while the SAME task continues.");
    if(generation==Volatile.Read(ref shutdownGeneration))
     DispatcherQueue.TryEnqueue(()=>RestoreFromShutdownWatchdog("数据库维护超过 30 秒，已恢复安全界面；原维护任务继续收尾，不会并发启动第二次数据库重建。",maintenancePending:true));

    var delayedResult=await maintenance.ConfigureAwait(false);
    await ApplyCompletedDatabaseMaintenanceAsync(maintenance,delayedResult).ConfigureAwait(false);
    if(pendingShowAfterMaintenance&&!closingPending)DispatcherQueue.TryEnqueue(()=>ShowAfterExternalActivation());
    return;
   }

   var result=await maintenance.ConfigureAwait(false);
   await ApplyCompletedDatabaseMaintenanceAsync(maintenance,result).ConfigureAwait(false);

   if(!result.Success)
   {
    services.Log.Write("App","HiddenShutdown","DatabaseCleanupNotVerified",detail:result.Error);
    if(generation==Volatile.Read(ref shutdownGeneration))
     DispatcherQueue.TryEnqueue(()=>RestoreFromShutdownWatchdog("退出前数据库清理没有验证成功，CleanC 已恢复界面，没有把这次退出当作成功收尾。"));
    return;
   }

   if(generation!=Volatile.Read(ref shutdownGeneration))
   {
    if(pendingShowAfterMaintenance&&!closingPending)DispatcherQueue.TryEnqueue(()=>ShowAfterExternalActivation());
    return;
   }

   if(pendingShowAfterMaintenance&&!closingPending)
   {
    DispatcherQueue.TryEnqueue(()=>ShowAfterExternalActivation());
    return;
   }

   // Three-second grace period begins only AFTER successful DB cleanup and
   // in-memory scan-state invalidation. A redirected launch invalidates this
   // generation and reopens the same MainWindow.
   var graceWatch=System.Diagnostics.Stopwatch.StartNew();
   while(graceWatch.Elapsed<TimeSpan.FromSeconds(3))
   {
    if(generation!=Volatile.Read(ref shutdownGeneration))return;
    await Task.Delay(100).ConfigureAwait(false);
   }

   if(generation!=Volatile.Read(ref shutdownGeneration))return;

   // Final close. Prefer the normal Window/Application shutdown path. The final
   // process fallback is only armed after scan + all preserved tasks + DB
   // maintenance have finished and is generation-guarded so a reactivation wins.
   DispatcherQueue.TryEnqueue(()=>
   {
    if(generation!=shutdownGeneration)return;
    allowFinalClose=true;
    try{Close();}
    catch
    {
     try{Application.Current.Exit();}
     catch{Environment.Exit(0);}
    }
    _=Task.Run(async()=>
    {
     await Task.Delay(5000).ConfigureAwait(false);
     if(generation!=Volatile.Read(ref shutdownGeneration))return;
     try{DispatcherQueue.TryEnqueue(()=>{try{Application.Current.Exit();}catch{}});}catch{}
     await Task.Delay(1000).ConfigureAwait(false);
     if(generation==Volatile.Read(ref shutdownGeneration))Environment.Exit(0);
    });
   });
  }
  catch(Exception e)
  {
   services.Log.Write("App","HiddenShutdown","Failed",detail:e.ToString());
   DispatcherQueue.TryEnqueue(()=>RestoreFromShutdownWatchdog("退出状态机发生异常，CleanC 已恢复界面，未强制终止后台任务。"));
  }
 }

 async Task<bool> WaitWhileAsync(Func<bool> predicate,TimeSpan timeout,long generation)
 {
  var watch=System.Diagnostics.Stopwatch.StartNew();
  while(predicate())
  {
   if(generation!=Volatile.Read(ref shutdownGeneration))return false;
   if(watch.Elapsed>=timeout)return false;
   await Task.Delay(120).ConfigureAwait(false);
  }
  return true;
 }

 void SetStatus(string value){vm.Status=value;footer.Text=value;}
 async Task Guard(Func<Task> action){try{await action();}catch(OperationCanceledException){SetStatus("操作已取消。");}catch(Exception e){services.Log.Write("App","Operation","Failed",detail:e.ToString());App.WriteCrashLog("App.Operation",e);try{await Notice("操作未完成",e.Message);}catch(Exception dialogError){services.Log.Write("App","Notice","Failed",detail:dialogError.ToString());}}}
 async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
 {
  await dialogGate.WaitAsync();
  try{return await dialog.ShowAsync();}
  finally{dialogGate.Release();}
 }
 async Task Notice(string title,string message)
 {
  var d=new ContentDialog{XamlRoot=shell.XamlRoot,Title=title,Content=new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap},CloseButtonText="知道了",RequestedTheme=shell.RequestedTheme};
  await ShowDialogAsync(d);
 }
 async Task<bool> Confirm(string title,string message,string button)
 {
  var d=new ContentDialog{XamlRoot=shell.XamlRoot,Title=title,Content=new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap},PrimaryButtonText=button,CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,RequestedTheme=shell.RequestedTheme};
  return await ShowDialogAsync(d)==ContentDialogResult.Primary;
 }
 static void OpenFolder(string path){if(!Directory.Exists(path))Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo{FileName=path,UseShellExecute=true});}
 StackPanel Heading(string eyebrow,string title,string subtitle)=>Ui.Stack(8,Ui.T(eyebrow,11,true,Ui.Accent),Ui.T(title,30,true),Ui.T(subtitle,14,false,Ui.Muted));
 void ShowActivation()
 {
  var state=services.License.Context;
  var key=new TextBox{PlaceholderText="CLC-XXXX-XXXX-XXXX-XXXX",Height=48,MaxLength=200,CornerRadius=new CornerRadius(14),FontFamily=new FontFamily("Cascadia Mono"),FontSize=15};
  var activate=Ui.Button("在线激活",()=>{},true);activate.HorizontalAlignment=HorizontalAlignment.Stretch;
  activate.Click+=async(_,_)=>await Guard(async()=>{activate.IsEnabled=false;try{await services.License.ActivateAsync(key.Text);key.Text="";await TransitionContentAsync(RenderPage,false,false);}finally{activate.IsEnabled=true;}});
  var right=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch};
  var session=services.License.BeginOfflineActivation();
  void ShowCode()
  {
   var input=new TextBox{PlaceholderText="XXXX-XXXX-XXXX-XXXX",MaxLength=40,Height=48,FontSize=20,FontFamily=new FontFamily("Cascadia Mono"),CornerRadius=new CornerRadius(14)};
   var submit=Ui.Button("立即激活",()=>{},true);submit.IsEnabled=false;submit.HorizontalAlignment=HorizontalAlignment.Stretch;
   input.TextChanged+=(_,_)=>{submit.IsEnabled=OfflineActivationSession.Normalize(input.Text).Length==16&&session.IsValid;};
   submit.Click+=async(_,_)=>await Guard(async()=>{submit.IsEnabled=false;try{await services.License.CompleteOfflineActivationAsync(input.Text);await TransitionContentAsync(RenderPage,false,false);}finally{submit.IsEnabled=session.IsValid&&OfflineActivationSession.Normalize(input.Text).Length==16;}});
   right.Content=Ui.Stack(16,Ui.T("等待输入激活码",22,true),Ui.T("输入手机页面显示的 16 位激活码，支持整段粘贴。",13,false,Ui.Muted),input,submit,Ui.Button("返回二维码",()=>_=Guard(ShowQr)),Ui.Button("重新生成二维码",()=>_=Guard(async()=>{session=services.License.BeginOfflineActivation();await ShowQr();})));
  }
  async Task ShowQr()
  {
   using var generator=new QRCoder.QRCodeGenerator();using var data=generator.CreateQrCode(session.Url,QRCoder.QRCodeGenerator.ECCLevel.M);using var png=new QRCoder.PngByteQRCode(data);
   using var stream=new Windows.Storage.Streams.InMemoryRandomAccessStream();using(var writer=new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0))){writer.WriteBytes(png.GetGraphic(5));await writer.StoreAsync();await writer.FlushAsync();}
   stream.Seek(0);var bitmap=new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();await bitmap.SetSourceAsync(stream);
   var qr=new Image{Source=bitmap,Width=320,Height=320,Stretch=Stretch.Uniform};
   right.Content=Ui.Stack(12,Ui.T("内网离线激活",22,true),Ui.T("手机扫码，在公开网页输入授权码。",13,false,Ui.Muted),new Border{Background=Ui.B("FFFFFF"),Padding=new Thickness(8),CornerRadius=new CornerRadius(16),Child=qr},Ui.Button("我已扫码",ShowCode,true),Ui.Button("重新生成二维码",()=>_=Guard(async()=>{session=services.License.BeginOfflineActivation();await ShowQr();})),Ui.T("二维码 10 分钟内有效 · 按授权码原有效期激活",12,false,Ui.Muted));
  }
  var columns=Ui.Columns(-1,-1);Ui.Add(columns,Ui.Stack(18,Ui.Logo(56),Ui.T("授权码",22,true),Ui.T(state.StatusText,13,false,Ui.Muted),key,activate,Ui.T("联网电脑可直接输入授权码完成授权。",13,false,Ui.Muted),Ui.Button("复制设备码",CopyDevice)),0);Ui.Add(columns,right,1);
  var card=Ui.GlassCard(Ui.Stack(24,Heading("LICENSE","激活 CleanC","直接输入授权码，或使用手机扫码完成离线激活。"),columns,Ui.T("激活码仅对当前二维码有效；重新生成后旧码立即失效。离线授权到期后，请重新扫码或联网激活。",12,false,Ui.Muted)),new Thickness(28));
  card.MaxWidth=1000;card.HorizontalAlignment=HorizontalAlignment.Stretch;card.Margin=new Thickness(0,12,0,24);pageHost.Content=card;_=Guard(ShowQr);
 }
 void CopyDevice(){var data=new Windows.ApplicationModel.DataTransfer.DataPackage();data.SetText(services.License.DeviceId);Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);SetStatus("设备码已复制。");}
 void ShowOverview()
 {
  var cached=scanRunning?overviewScanningView:overviewIdleView;if(cached is not null){pageHost.Content=cached;return;}
  var drive=new DriveInfo(vm.ScanRoot);long total=drive.TotalSize,free=drive.AvailableFreeSpace;
  var scanButton=Ui.Button(scanRunning?"正在扫描…":"开始扫描   →",()=>_=Guard(StartScan),true);scanButton.IsEnabled=!scanRunning&&!cleanupRunning;
  var hero=Ui.Columns(-1,280);var left=Ui.Stack(20,Ui.Pill("本地处理 · 隐私优先"),Ui.Stack(12,Ui.T("给 C 盘，留一点余地。",30,true),Ui.T("从确认安全的缓存开始，\n让重要的文件安心留在原处。",15,false,Ui.Muted)),scanButton,Ui.Row(20,Ui.T("已使用  "+Display.Bytes(total-free),12,false,Ui.Muted),Ui.T("总容量  "+Display.Bytes(total),12,false,Ui.Muted)));left.VerticalAlignment=VerticalAlignment.Center;Ui.Add(hero,left,0);Ui.Add(hero,new DiskGauge((double)(total-free)/total,Display.Bytes(free)),1);
  var cards=Ui.Columns(-1,-1,-1);
  void Feature(int col,string glyph,string title,string body,string page){var b=Ui.Card(Ui.Stack(20,Ui.Icon(glyph,26),Ui.Stack(8,Ui.T(title,18,true),Ui.T(body,12,false,Ui.Muted)),Ui.Button("查看   ↗",()=>Navigate(page))),new Thickness(20));Ui.Add(cards,b,col);}
  Feature(0,"\uE74D","智能清理","明确的安全规则，\n每次清理由你确认。","clean");Feature(1,"\uE9D9","空间分析","从磁盘到文件夹，\n看清空间用在哪里。","space");Feature(2,"\uE90F","系统修复","使用 Windows 官方工具，\n检查映像与系统文件。","repair");
  var view=Ui.Stack(24,Heading("CLEAN SPACE. CLEAR MIND.","你好，欢迎使用 CleanC",scanRunning?"扫描已在后台运行，你可以继续使用其它功能。":"让 Windows 保持干净、稳定。"),Ui.Card(hero,new Thickness(32)),cards,Ui.Row(8,Ui.Icon("\uE73E",14),Ui.T("扫描只读 · 无广告 · 无后台驻留 · 不上传文件",11,false,Ui.Muted)));
  if(scanRunning)overviewScanningView=view;else overviewIdleView=view;pageHost.Content=view;
 }
}

