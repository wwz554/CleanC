using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
namespace CleanC.Licensing;
public sealed class OfflineActivationSession : IDisposable
{
 const string Alphabet="0123456789ABCDEFGHJKMNPQRSTVWXYZ";
 readonly byte[] secret=RandomNumberGenerator.GetBytes(32);
 readonly Stopwatch age=Stopwatch.StartNew();int attempts;bool disposed;
 public string Request{get;} public string Url=>"https://wwz554.ccwu.cc/offline/activate#"+Request;
 public DateTimeOffset CreatedAt{get;} public string SessionId{get;}=Guid.NewGuid().ToString("N");
 public TimeSpan Elapsed=>age.Elapsed;
 public bool IsValid=>!disposed&&attempts<5&&age.Elapsed<TimeSpan.FromMinutes(10);
 public OfflineActivationSession(string deviceId,string devicePublicKey,DateTimeOffset now)
 {
  CreatedAt=DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
  var context=JsonSerializer.Serialize(new object[]{2,"CleanC",SessionId,deviceId,devicePublicKey,CreatedAt.ToUnixTimeMilliseconds()},new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
  var clear=new byte[64];secret.CopyTo(clear,0);SHA256.HashData(Encoding.UTF8.GetBytes(context)).CopyTo(clear,32);
  using var resource=typeof(OfflineActivationSession).Assembly.GetManifestResourceStream("CleanC.Licensing.Crypto.offline-public.pem")??throw new CryptographicException("离线公钥缺失。");
  using var reader=new StreamReader(resource);using var rsa=RSA.Create();rsa.ImportFromPem(reader.ReadToEnd());
  var box=Base64(rsa.Encrypt(clear,RSAEncryptionPadding.OaepSHA256));CryptographicOperations.ZeroMemory(clear);
  Request=Base64(JsonSerializer.SerializeToUtf8Bytes(new{v=2,app="CleanC",sessionId=SessionId,deviceId,devicePublicKey,createdAt=CreatedAt.ToUnixTimeMilliseconds(),box}));
 }
 static string Base64(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
 public static string Normalize(string code)=>new(code.ToUpperInvariant().Where(c=>!char.IsWhiteSpace(c)&&c!='-').Select(c=>c=='O'?'0':c is 'I' or 'L'?'1':c).ToArray());
 public bool Verify(string code,out string licenseType,out DateTimeOffset? expiresAt)
 {
  licenseType="";expiresAt=null;if(!IsValid)return false;attempts++;var entered=Normalize(code);if(entered.Length!=16||entered.Any(c=>!Alphabet.Contains(c)))return false;
  var packed=new byte[10];int bits=0,index=0;ulong value=0;
  foreach(char c in entered)
  {
   value=(value<<5)|(uint)Alphabet.IndexOf(c);bits+=5;
   while(bits>=8){bits-=8;packed[index++]=(byte)((value>>bits)&0xFF);value=bits==0?0:value&((1UL<<bits)-1);}
  }
  uint meta=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packed);
  var mac=HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes("CleanC/offline/v2\n"+Request+"\n"+meta.ToString(System.Globalization.CultureInfo.InvariantCulture)));
  if(!CryptographicOperations.FixedTimeEquals(mac.AsSpan(0,6),packed.AsSpan(4,6)))return false;
  uint kind=meta>>30,minutes=meta&0x3fffffff;
  if(kind==0&&minutes==0)licenseType="permanent";
  else if(kind is 1 or 2){licenseType=kind==1?"duration":"fixed";expiresAt=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero).AddMinutes(minutes);if(expiresAt<=CreatedAt+Elapsed)return false;}
  else return false;
  Dispose();return true;
 }
 public void Dispose(){disposed=true;CryptographicOperations.ZeroMemory(secret);age.Stop();}
}
public sealed record OfflineActivationRecord(Lease Lease,CleanC.Core.LicenseState? Lock=null);
