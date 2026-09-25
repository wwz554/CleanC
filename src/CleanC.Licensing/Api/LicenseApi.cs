using System.Net.Http.Json;
using System.Text.Json;
namespace CleanC.Licensing;
public sealed class LicenseApiOptions
{
 public string BaseUrl{get;set;}="https://wwz554.ccwu.cc/api/v1/";
 public string BootstrapUrl{get;set;}="https://wwz554.ccwu.cc/bootstrap/v1/config";
}
public static class LicenseEndpoints
{
 public const string Activate="license/activate",Challenge="device/challenge",Refresh="license/refresh",Health="health",Meta="meta";
}
public sealed class LicenseApi : IDisposable
{
 readonly HttpClient http;
 readonly LicenseApiOptions options;
 public string BaseUrl=>options.BaseUrl;
 public LicenseApi(LicenseApiOptions options,HttpMessageHandler? handler=null)
 {
  this.options=options;
  http=handler is null?new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(8)}):new HttpClient(handler);
  http.Timeout=TimeSpan.FromSeconds(20);http.MaxResponseContentBufferSize=128*1024;http.DefaultRequestHeaders.UserAgent.ParseAdd("CleanC/1.6.9");
 }
 static Uri Https(string value){if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo))throw new LicenseException("API_NOT_CONFIGURED","授权服务尚未配置有效 HTTPS 地址。");return uri;}
 public async Task<JsonElement> Post(string endpoint,object body,CancellationToken token)
 {
  var root=Https(options.BaseUrl);
  using var response=await http.PostAsJsonAsync(new Uri(root,endpoint),body,SignatureVerifier.Json,token);
  var text=await response.Content.ReadAsStringAsync(token);
  JsonElement data;
  try{data=JsonSerializer.Deserialize<JsonElement>(text);}catch(JsonException){throw new LicenseException("SERVER_RESPONSE","授权服务器返回格式异常。");}
  if(data.ValueKind!=JsonValueKind.Object)throw new LicenseException("SERVER_RESPONSE","授权服务器返回格式异常。");
  if(!response.IsSuccessStatusCode||!data.TryGetProperty("success",out var ok)||ok.ValueKind!=JsonValueKind.True)
  {
   var code=data.TryGetProperty("code",out var c)&&c.ValueKind==JsonValueKind.String?c.GetString()??"SERVER_ERROR":"SERVER_ERROR";
   // Server messages are not logged or allowed to echo license keys.
   throw new LicenseException(code,ErrorText(code));
  }
  return data;
 }
 public static SignedEnvelope Envelope(JsonElement response)=>response.Deserialize<SignedEnvelope>(SignatureVerifier.Json)??throw new LicenseException("INVALID_SIGNATURE","缺少签名。");
 public async Task<string> DiagnosticsAsync(SignatureVerifier verifier,CancellationToken token)
 {
  using var h=await http.GetAsync(new Uri(Https(options.BaseUrl),LicenseEndpoints.Health),token);h.EnsureSuccessStatusCode();
  using var m=await http.GetAsync(new Uri(Https(options.BaseUrl),LicenseEndpoints.Meta),token);m.EnsureSuccessStatusCode();
  await RefreshBootstrapAsync(verifier,token);
  using var offline=await http.GetAsync(new Uri(Https(options.BaseUrl),"offline/readiness"),token);
  return offline.IsSuccessStatusCode?"联网授权服务可达；Bootstrap 签名验证通过；离线密钥格式检查通过，仍需扫码验收公钥配对。":"联网授权服务可达且 Bootstrap 签名验证通过；离线服务尚未就绪或未升级，请联系管理员检查离线密钥。";
 }
 public async Task RefreshBootstrapAsync(SignatureVerifier verifier,CancellationToken token)
 {
  using var r=await http.GetAsync(Https(options.BootstrapUrl),token);r.EnsureSuccessStatusCode();
  var e=JsonSerializer.Deserialize<SignedEnvelope>(await r.Content.ReadAsStringAsync(token),SignatureVerifier.Json)??throw new LicenseException("INVALID_SIGNATURE","缺少签名。");
  var data=verifier.Verify<JsonElement>(e);
  if(data.GetProperty("apiVersion").GetInt32()!=3||data.GetProperty("leaseVersion").GetInt32()!=4||data.GetProperty("renewalProtocol").GetString()!="challenge-refresh")throw new LicenseException("PROTOCOL","服务器协议不匹配。");
  var root=Https(data.GetProperty("canonicalBaseUrl").GetString()!);
  if(root.AbsolutePath!="/"||!string.IsNullOrEmpty(root.Query)||!string.IsNullOrEmpty(root.Fragment))throw new LicenseException("PROTOCOL","服务器地址格式异常。");
  options.BaseUrl=new Uri(root,"api/v1/").ToString();
 }
 public static string ErrorText(string code)=>code switch{
  "LICENSE_NOT_FOUND"=>"授权码不存在，请检查输入。",
  "LICENSE_EXPIRED_RELEASED"=>"授权已到期，旧绑定已释放，可以输入新授权码。",
  "LICENSE_EXPIRED"=>"授权已到期，请输入有效授权码。",
  "LICENSE_DISABLED"=>"此授权已被停用，请联系软件提供者。",
  "DEVICE_NOT_BOUND"=>"设备已解绑，请重新激活。",
  "DEVICE_KEY_MISMATCH" or "INVALID_DEVICE_SIGNATURE"=>"设备密钥与服务器记录不一致。",
  "DEVICE_ALREADY_LICENSED" or "DEVICE_ALREADY_BOUND" or "DEVICE_HAS_ACTIVE_LICENSE"=>"本机已绑定有效授权，请先联系软件提供者处理。",
  "LICENSE_ALREADY_BOUND" or "DEVICE_LIMIT_REACHED"=>"授权码已绑定其他设备。",
  "RATE_LIMITED"=>"请求较频繁，请稍后再试。",
  "OFFLINE_NOT_CONFIGURED" or "OFFLINE_KEY_INVALID"=>"服务器尚未正确配置离线授权密钥，请联系软件提供者。",
  "OFFLINE_UNAVAILABLE"=>"离线授权服务暂时不可用，请稍后重试。",
  "CHALLENGE_INVALID" or "CHALLENGE_ALREADY_USED"=>"设备挑战已失效，请重新刷新授权。",
  _=>$"授权请求未完成（{code}）。"
 };
 public void Dispose()=>http.Dispose();
}

