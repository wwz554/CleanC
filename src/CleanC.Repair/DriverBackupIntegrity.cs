using System.Security.Cryptography;
using System.Text.Json;
using CleanC.Core;
namespace CleanC.Repair;

public static class DriverOperationStatus
{
 public static bool IsSuccess(int overall,int individual,int hresult,int overallHresult=0)=>overall==2&&individual==2&&hresult>=0&&overallHresult>=0;
}
public static class DriverBackupIntegrity
{
 public static bool IsMetadata(string path)=>new[]{"CleanC-driver-hashes.json","CleanC-driver-backup.json","CleanC-backup-protected.flag","CleanC-backup-reboot-pending.flag","备份说明.txt","请勿删除.txt"}.Contains(Path.GetFileName(path),StringComparer.OrdinalIgnoreCase);
 public static bool Verify(string folder,string inf,out string error)
 {
  error="";
  try
  {
   folder=Path.GetFullPath(folder);inf=Path.GetFullPath(inf);
   if(!SafetyPolicy.Within(inf,folder)||!Path.GetExtension(inf).Equals(".inf",StringComparison.OrdinalIgnoreCase)||
    SafetyPolicy.HasReparseAncestor(inf)||(File.GetAttributes(inf)&FileAttributes.ReparsePoint)!=0)
    throw new IOException("INF 不属于当前备份或路径包含链接");
   var path=Path.Combine(folder,"CleanC-driver-hashes.json");
   var manifest=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path))??new();
   if(manifest.Count==0)throw new IOException("SHA-256 清单为空");
   var verified=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var pair in manifest)
   {
    var file=Path.GetFullPath(Path.Combine(folder,pair.Key));
    if(Path.IsPathRooted(pair.Key)||!SafetyPolicy.Within(file,folder)||!File.Exists(file)||IsMetadata(file)||
      SafetyPolicy.HasReparseAncestor(file)||(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0||!verified.Add(file))
     throw new IOException("备份清单包含不安全、重复或缺失路径");
    using var stream=File.OpenRead(file);var hash=Convert.ToHexString(SHA256.HashData(stream));
    if(!hash.Equals(pair.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("备份文件哈希不一致");
   }
   if(!verified.Contains(inf))throw new IOException("目标 INF 未被哈希清单覆盖");
   foreach(var file in Directory.EnumerateFileSystemEntries(folder,"*",SearchOption.AllDirectories))
   {
    var attributes=File.GetAttributes(file);
    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("备份包含链接");
    if((attributes&FileAttributes.Directory)==0&&!IsMetadata(file)&&!verified.Contains(Path.GetFullPath(file)))
     throw new IOException("备份中存在未校验载荷");
   }
   return true;
  }
  catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException)
  {error=e.Message;return false;}
 }
}
