using System.Security.AccessControl;
using System.Security.Principal;
namespace CleanC.Core;
public static class AppPaths
{
 public static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CleanC");
 public static string MachineData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CleanC");
 public static string UserSid => WindowsIdentity.GetCurrent().User!.Value;
 // Per-user ACLs avoid sharing DPAPI blobs or device identities between Windows accounts.
 public static string License => EnsurePrivate(Path.Combine(MachineData,"License",UserSid));
 public static string Logs => EnsurePrivate(Path.Combine(MachineData,"Logs",UserSid));
 public static string Recovery => EnsurePrivate(Path.Combine(MachineData,"Recovery",UserSid));
 public static string DriverPackages => Path.Combine(AppContext.BaseDirectory,"DriverPackages");
 public static string DriverBackups => Path.Combine(AppContext.BaseDirectory,"DriverBackups");
 public static string EnsurePrivate(string path)
 {
  if(!Directory.Exists(path)) {
   var security=new DirectorySecurity();
   security.SetAccessRuleProtection(true,false);
   foreach(var sid in new[]{UserSid,"S-1-5-18","S-1-5-32-544"})
    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   new DirectoryInfo(path).Create(security);
  }
  for(var d=new DirectoryInfo(path);d is not null;d=d.Parent)
   if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("CleanC 数据目录不能是链接。");
  return path;
 }
}

