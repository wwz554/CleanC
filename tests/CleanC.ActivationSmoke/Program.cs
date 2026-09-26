using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CleanC.Licensing;

// Deliberate live test only. Never supply a customer's authorization code.
if (!args.Contains("--live")) { Console.WriteLine("SKIP: requires --live and a dedicated temporary test license."); return; }
var licenseKey=Environment.GetEnvironmentVariable("CLEANC_SMOKE_LICENSE")??throw new Exception("Temporary test license missing");
var deviceId=Environment.GetEnvironmentVariable("CLEANC_SMOKE_DEVICE")??throw new Exception("Test device ID missing");
if(!licenseKey.StartsWith("CLC-SMOKE-")||!deviceId.StartsWith("deployment-smoke-device-"))throw new Exception("Only synthetic deployment fixtures are allowed");
using var handler=new SocketsHttpHandler();
// Optional diagnostic IP obtained from a trusted DNS resolver. TLS still validates
// wwz554.ccwu.cc normally; this does not alter system DNS or the production client.
if(Environment.GetEnvironmentVariable("CLEANC_SMOKE_RESOLVED_IP") is string ip)
{
 var address=System.Net.IPAddress.Parse(ip);
 handler.ConnectCallback=async(context,token)=>
 {
  if(context.DnsEndPoint.Host!="wwz554.ccwu.cc"||context.DnsEndPoint.Port!=443)throw new Exception("Unexpected diagnostic destination");
  var socket=new System.Net.Sockets.Socket(address.AddressFamily,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);
  try{await socket.ConnectAsync(address,443,token);return new System.Net.Sockets.NetworkStream(socket,true);}catch{socket.Dispose();throw;}
 };
 Console.WriteLine("NOTE diagnostic DNS override enabled; HTTPS certificate verification remains enabled");
}
using var http=new HttpClient(handler) { BaseAddress=new Uri("https://wwz554.ccwu.cc"),Timeout=TimeSpan.FromSeconds(30) };
using var ec=ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey=ec.ExportSubjectPublicKeyInfoPem();
using var session=new OfflineActivationSession(deviceId,publicKey,DateTimeOffset.UtcNow);
async Task<JsonElement> Post(string path,object body,int expected=200)
{
 using var response=await http.PostAsJsonAsync(path,body);
 using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
 if((int)response.StatusCode!=expected)throw new Exception($"{path}: expected HTTP {expected}, received {(int)response.StatusCode}; code="+(json.RootElement.TryGetProperty("code",out var c)?c.GetString():"unknown"));
 return json.RootElement.Clone();
}
var body=new {request=session.Request,licenseKey};
var issued=await Post("/api/v1/offline/issue",body);
var repeated=await Post("/api/v1/offline/issue",body);
var code=issued.GetProperty("code").GetString()!;
if(code!=repeated.GetProperty("code").GetString())throw new Exception("Offline retry returned a different code");
Console.WriteLine("PASS live offline issue and identical retry response");
using(var otherSession=new OfflineActivationSession(deviceId,publicKey,DateTimeOffset.UtcNow))
 if(otherSession.Verify(code,out _,out _))throw new Exception("Code accepted by another session");
if(!session.Verify(code,out var type,out var expiry))throw new Exception("Actual client rejected live offline code");
if(type!=issued.GetProperty("licenseType").GetString())throw new Exception("License type mismatch");
var expectedExpiry=issued.GetProperty("expiresAt").ValueKind==JsonValueKind.Null?(DateTimeOffset?)null:issued.GetProperty("expiresAt").GetDateTimeOffset();
if(expiry!=expectedExpiry)throw new Exception("Offline expiry mismatch");
if(session.Verify(code,out _,out _))throw new Exception("Consumed session accepted another code");
Console.WriteLine($"PASS actual client offline verification: {type}; expiry matches; wrong-session and replay rejected");
var online=await Post("/api/v1/license/activate",new {licenseKey,deviceId,devicePublicKey=publicKey,appVersion="1.6.9"});
var envelope=online.Deserialize<SignedEnvelope>(SignatureVerifier.Json)??throw new Exception("Missing online envelope");
var lease=new SignatureVerifier().VerifyLease(envelope,deviceId);
if(lease.LicenseType!=type)throw new Exception("Online/offline license types differ");
Console.WriteLine("PASS actual client verifies signed online lease after offline activation");
