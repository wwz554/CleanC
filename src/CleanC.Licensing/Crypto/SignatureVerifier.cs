using System.Security.Cryptography;
using System.Text.Json;
namespace CleanC.Licensing;
public sealed class SignatureVerifier
{
 private readonly string publicKey;
 public static JsonSerializerOptions Json {get;}=new(JsonSerializerDefaults.Web);
 public SignatureVerifier(string? pem=null){if(pem is not null){publicKey=pem;return;}using var s=typeof(SignatureVerifier).Assembly.GetManifestResourceStream("CleanC.Licensing.Crypto.cleanc-public.pem")!;using var r=new StreamReader(s);publicKey=r.ReadToEnd();}
 public static byte[] Decode(string value){if(value.Length>90000)throw new FormatException("响应过大");var s=value.Replace('-','+').Replace('_','/');return Convert.FromBase64String(s.PadRight((s.Length+3)/4*4,'='));}
 public static string Encode(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
 public T Verify<T>(SignedEnvelope envelope)
 {
  try{
   if(envelope is null||string.IsNullOrEmpty(envelope.SignedPayload)||string.IsNullOrEmpty(envelope.Signature))throw new CryptographicException();
   using var ec=ECDsa.Create();ec.ImportFromPem(publicKey);
   var payload=Decode(envelope.SignedPayload);var sig=Decode(envelope.Signature);
   if(sig.Length!=64||!ec.VerifyData(payload,sig,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))throw new CryptographicException();
   // Parse only the exact verified bytes. The unsigned outer lease is never trusted.
   using(var doc=JsonDocument.Parse(payload)){ValidateUnique(doc.RootElement);}
   return JsonSerializer.Deserialize<T>(payload,Json)??throw new JsonException();
  }catch(Exception e)when(e is CryptographicException or JsonException or FormatException or ArgumentException){throw new LicenseException("INVALID_SIGNATURE","服务器签名无效，已拒绝授权数据。");}
 }
 static void ValidateUnique(JsonElement e){if(e.ValueKind==JsonValueKind.Object){var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in e.EnumerateObject()){if(!set.Add(p.Name))throw new JsonException();ValidateUnique(p.Value);}}else if(e.ValueKind==JsonValueKind.Array)foreach(var v in e.EnumerateArray())ValidateUnique(v);}
 public Lease VerifyLease(SignedEnvelope envelope,string deviceId)
 {
  var l=Verify<Lease>(envelope);
  if(l.DeviceId!=deviceId)throw new LicenseException("DEVICE_MISMATCH","授权与当前设备不匹配。");
  if(l.Version!=4||l.ApiVersion!=3||l.RenewalProtocol!="challenge-refresh"||string.IsNullOrWhiteSpace(l.LicenseId)||string.IsNullOrWhiteSpace(l.Nonce)||l.Features is null||l.IssuedAt!=l.ServerTime||l.ServerTime.Year<2020||l.ExpiresAt<=l.ServerTime||l.ExpiresAt-l.ServerTime>TimeSpan.FromDays(31)||l.LeaseHours<1||l.ExpiresAt>l.ServerTime.AddHours(l.LeaseHours).AddSeconds(1)||l.IsPermanent!=(l.LicenseType=="permanent")||l.CountdownRequired==l.IsPermanent||l.IsPermanent!=(l.LicenseExpiresAt is null)||(!l.IsPermanent&&(l.LicenseType!="duration"&&l.LicenseType!="fixed"))||(l.LicenseExpiresAt.HasValue&&(l.LicenseExpiresAt<=l.ServerTime||l.ExpiresAt>l.LicenseExpiresAt)))
   throw new LicenseException("INVALID_LEASE","服务器租约字段不完整或不符合 v4 协议。");
  return l;
 }
 public OfflineCredential VerifyOffline(SignedEnvelope envelope,string deviceId)
 {
  var p=Verify<OfflineCredential>(envelope);
  var l=p.Lease;
  if(l is null)throw new LicenseException("INVALID_LEASE","离线凭证缺少授权信息。");
  if(l.DeviceId!=deviceId)throw new LicenseException("DEVICE_MISMATCH","签名凭证属于另一台电脑，请重新扫码领取。");
  if(p.Version!=1||p.App!="CleanC"||p.Purpose!="offline-entitlement-v1"||
   l.Version!=4||l.ApiVersion!=3||l.RenewalProtocol!="offline-v3"||l.LeaseHours!=0||
   string.IsNullOrWhiteSpace(l.LicenseId)||string.IsNullOrWhiteSpace(l.Nonce)||l.Features is null||
   l.IssuedAt!=l.ServerTime||l.ServerTime.Year<2020||l.ExpiresAt<=l.ServerTime||
   l.ExpiresAt!=(l.LicenseExpiresAt??DateTimeOffset.MaxValue)||l.IsPermanent!=(l.LicenseType=="permanent")||
   l.CountdownRequired==l.IsPermanent||l.IsPermanent!=(l.LicenseExpiresAt is null)||
   (!l.IsPermanent&&l.LicenseType is not ("duration" or "fixed"))||
   p.RequestHash is null||p.CodeHash is null||
   p.ChallengeNonce is null||
   !((p.RequestHash.Length==0&&p.CodeHash.Length==0&&p.ChallengeNonce.Length is >=16 and <=512)||
    (IsHash(p.RequestHash)&&IsHash(p.CodeHash)&&p.ChallengeNonce.Length==0)))
   throw new LicenseException("INVALID_LEASE","签名凭证字段不完整或协议不匹配。");
  return p;
 }
 static bool IsHash(string value)=>value.Length==64&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
