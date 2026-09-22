using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using CleanC.Core;
using CleanC.Scanner;
using System.Runtime.InteropServices;

namespace CleanC.App;

public partial class App : Application
{
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
 static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
 const string AppUserModelId="CleanC.Desktop";
 private Window? window;
 private AppInstance? appInstance;
 private string? runMarker;

 public App()
 {
  InitializeComponent();
  UnhandledException+=(_,e)=>{
   WriteCrashLog("WinUI.UnhandledException",e.Exception);
   // Unknown WinUI exceptions are not claimed as recovered. Continuing after an
   // unhandled exception can leave cleanup/driver/repair state inconsistent.
   e.Handled=false;
  };
  TaskScheduler.UnobservedTaskException+=(_,e)=>{WriteCrashLog("TaskScheduler.UnobservedTaskException",e.Exception);e.SetObserved();};
  AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{if(e.ExceptionObject is Exception ex)WriteCrashLog("AppDomain.UnhandledException",ex);};
 }

 protected override async void OnLaunched(LaunchActivatedEventArgs args)
 {
  try
  {
   try{SetCurrentProcessExplicitAppUserModelID(AppUserModelId);}catch{}

   // Windows App SDK single-instance activation. A second launch is redirected
   // to the already-running (possibly hidden) CleanC process instead of creating
   // another UI/process that immediately exits without waking the first one.
   var activation=AppInstance.GetCurrent().GetActivatedEventArgs();
   appInstance=AppInstance.FindOrRegisterForKey("CleanC-"+AppPaths.UserSid);
   if(!appInstance.IsCurrent)
   {
    await appInstance.RedirectActivationToAsync(activation);
    Exit();
    return;
   }
   appInstance.Activated+=OnAppInstanceActivated;

   runMarker=Path.Combine(AppPaths.UserData,"startup.running");
   Directory.CreateDirectory(AppPaths.UserData);
   var dataDir=Path.Combine(AppPaths.UserData,"Data");
   var dbFormatMarker=Path.Combine(dataDir,"db-format-1.0.2.marker");
   var policyMarker=Path.Combine(dataDir,"safety-policy-"+SafetyPolicy.Version+".marker");
   if(File.Exists(runMarker)||!File.Exists(dbFormatMarker)||!File.Exists(policyMarker))
   {
    // CleanC.db contains transient scan data plus persistent scan summaries.
    // UI preferences (theme, scan mode and future panel settings) live separately
    // in settings.json and are never removed when the scan database is rebuilt.
    // 1.0.2 also performs a one-time rollover from the 1.0.1 database so a stale
    // WAL/SHM or partially written full-disk scan cannot poison every later launch.
    try{ScanDatabase.RecoverAfterUncleanExit();}catch(Exception e){WriteCrashLog("Database.RecoverAfterUncleanExit",e);}
    try{Directory.CreateDirectory(dataDir);File.WriteAllText(dbFormatMarker,DateTimeOffset.Now.ToString("O"));File.WriteAllText(policyMarker,DateTimeOffset.Now.ToString("O"));}catch(Exception e){WriteCrashLog("Database.FormatMarker",e);}
   }
   File.WriteAllText(runMarker,$"pid={Environment.ProcessId}; started={DateTimeOffset.Now:O}");

   window=new MainWindow(AppServices.Create());
   window.Closed+=OnWindowClosed;
   window.Activate();
  }
  catch(Exception e)
  {
   WriteCrashLog("Startup",e);
   ShowFallback(e);
  }
 }

 void ShowFallback(Exception error)
 {
  try
  {
   window=new Window{Title="CleanC - 启动恢复",Content=new StackPanel{Padding=new Thickness(40),Spacing=16,Children={new TextBlock{Text="CleanC 启动未完成",FontSize=28,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold},new TextBlock{Text=error.Message,TextWrapping=TextWrapping.Wrap},new TextBlock{Text="已记录崩溃日志。重新安装不会删除授权数据。",TextWrapping=TextWrapping.Wrap}}}};
   window.Closed+=OnWindowClosed;
   window.Activate();
  }
  catch(Exception fallbackError)
  {
   WriteCrashLog("StartupFallback",fallbackError);
   ClearRunMarker();
   Exit();
  }
 }

 void OnWindowClosed(object sender,WindowEventArgs args)
 {
  ClearRunMarker();
 }

 void OnAppInstanceActivated(object? sender,AppActivationArguments args)
 {
  if(window is not MainWindow main)return;
  main.RequestExternalActivation();
 }
 void ClearRunMarker(){try{if(runMarker is not null&&File.Exists(runMarker))File.Delete(runMarker);}catch{}}

 internal static void WriteCrashLog(string source,Exception exception)
 {
  var text=$"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
  try{Directory.CreateDirectory(AppPaths.Logs);File.AppendAllText(Path.Combine(AppPaths.Logs,"crash.log"),text);return;}catch{}
  try{var fallback=Path.Combine(AppPaths.UserData,"Logs");Directory.CreateDirectory(fallback);File.AppendAllText(Path.Combine(fallback,"crash-fallback.log"),text);}catch{}
 }
}
