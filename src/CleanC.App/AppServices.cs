using CleanC.Core;
using CleanC.Logging;
using CleanC.Licensing;
using CleanC.Scanner;
using CleanC.Cleaner;
using CleanC.Repair;
using System.Text.Json;
namespace CleanC.App;
public sealed class Preferences
{
 public static string SettingsPath=>Path.Combine(AppPaths.UserData,"settings.json");
 public string Theme{get;set;}="system";
 public bool QuietScan{get;set;}=true;

 public static Preferences Load()
 {
  try{return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath))??new();}
  catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException){return new();}
 }

 public void Save()
 {
  Directory.CreateDirectory(AppPaths.UserData);
  var temp=SettingsPath+".tmp";
  var json=JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true});
  File.WriteAllText(temp,json);
  File.Move(temp,SettingsPath,true);
 }
}
public sealed class AppServices
{
 public LicenseManager License{get;}
 public ScanDatabase Database{get;}
 public DirectoryScanner Scanner{get;}
 public CleanupExecutor Cleaner{get;}
 public RecoveryService Recovery{get;}
 public RecycleBinService RecycleBin{get;}
 public RepairService Repair{get;}
 public ComponentStoreService ComponentStore{get;}
 public DriverService Drivers{get;}
 public AuditLog Log{get;}
 public SafetyPolicy Policy{get;}
 public Preferences Preferences{get;}=Preferences.Load();
 public AppServices(LicenseManager license,ScanDatabase database,AuditLog log,SafetyPolicy policy)
 {License=license;Database=database;Log=log;Policy=policy;Scanner=new(license.Context,policy,database,log);Cleaner=new(license.Context,policy,log);Recovery=new(license.Context,log);RecycleBin=new(license.Context,log);Repair=new(license.Context,log);ComponentStore=new(license.Context,log);Drivers=new(license.Context,log);}
 public static AppServices Create()
 {
  var log=new AuditLog();var store=new ProtectedStore();var identity=new DeviceIdentity(store);var api=new LicenseApi(new());
  var database=new ScanDatabase();
  try{log.Write("Database","SQLiteNative","Ready",detail:database.SQLiteVersion());}catch(Exception e){log.Write("Database","SQLiteNative","Unknown",detail:e.Message);}
  return new(new(api,new SignatureVerifier(),identity,store,new WindowsTimeSource(),log),database,log,new SafetyPolicy());
 }
}
