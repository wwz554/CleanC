using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace CleanC.Licensing;

public sealed record OfflineCredential(int Version,string App,string Purpose,Lease Lease,string RequestHash,string CodeHash,string ChallengeNonce="");

public enum UpgradeChannel { Manual, InApp }
public static class UpgradeOrigin
{
 public static UpgradeChannel Read(string appDirectory,string version)
 {
  // This marker controls UX, not authorization. Only a server signature grants access.
  try{
   var path=Path.Combine(appDirectory,"upgrade-origin.json");
   if(new FileInfo(path).Length>1024)return UpgradeChannel.Manual;
   using var json=JsonDocument.Parse(File.ReadAllText(path));
   return json.RootElement.GetProperty("version").GetString()==version&&json.RootElement.GetProperty("channel").GetString()=="in-app"
    ?UpgradeChannel.InApp:UpgradeChannel.Manual;
  }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException){return UpgradeChannel.Manual;}
 }
}

public static class OfflineCredentialFile
{
 public const int MaximumBytes=16384;
 public static SignedEnvelope Parse(string text)
 {
  if(Encoding.UTF8.GetByteCount(text)>MaximumBytes)throw new LicenseException("CREDENTIAL_INVALID","凭证文件过大，请选择手机授权页面下载的文件。");
  try{
   using var doc=JsonDocument.Parse(text);
   if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException();
   var fields=doc.RootElement.EnumerateObject().ToArray();
   if(fields.Length!=2||fields.Count(p=>p.Name=="signedPayload"&&p.Value.ValueKind==JsonValueKind.String)!=1||fields.Count(p=>p.Name=="signature"&&p.Value.ValueKind==JsonValueKind.String)!=1)throw new JsonException();
   return new(doc.RootElement.GetProperty("signedPayload").GetString()!,doc.RootElement.GetProperty("signature").GetString()!);
  }catch(JsonException){throw new LicenseException("CREDENTIAL_INVALID","凭证格式不正确，请导入手机网页下载的 .cleanc-license 文件。");}
 }
 public static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
