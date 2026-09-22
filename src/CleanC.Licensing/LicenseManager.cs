using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CleanC.Core;
using CleanC.Logging;
namespace CleanC.Licensing;
public sealed class LicenseManager
{
 readonly LicenseApi api;readonly SignatureVerifier verifier;readonly IDeviceIdentity device;readonly IProtectedStore store;readonly TrustedTimeService time;readonly AuditLog log;
 readonly SemaphoreSlim mutex=new(1,1);
 OfflineActivationRecord? offline;
 public OfflineActivationSession? OfflineSession{get;private set;}
 SavedLicense? saved;TimeSpan nextRetry;TimeSpan lastCheckpoint;int failures;bool expiryAttempted;
 readonly ITimeSource clock;
 public LicenseContext Context{get;}
 public string DeviceId=>device.DeviceId;
 public bool IsBusy=>mutex.CurrentCount==0;
 public string MaskedKey=>saved is null||saved.LicenseKey.Length<4?"—":"CLC-****-****-****-"+saved.LicenseKey[^4..];
 public string LastError{get;private set;}="";
 public DateTimeOffset? FirstAcceptedUtc=>saved?.FirstAcceptedUtc;
 public LicenseManager(LicenseApi api,SignatureVerifier verifier,IDeviceIdentity device,IProtectedStore store,ITimeSource clock,AuditLog log)
 {this.api=api;this.verifier=verifier;this.device=device;this.store=store;this.clock=clock;this.log=log;time=new(clock);Context=new(time);}
 public async Task InitializeAsync(CancellationToken token=default)
 {
  try {
   saved=store.Read<SavedLicense>("license.dat");
   if(saved is null){
    offline=store.Read<OfflineActivationRecord>("offline-license.dat");
    if(offline is null)return;
    var lease=offline.Lease;
    if(lease.DeviceId!=device.DeviceId||lease.RenewalProtocol!="offline-v2"||lease.ExpiresAt!=(lease.LicenseExpiresAt??DateTimeOffset.MaxValue)||lease.IsPermanent!=(lease.LicenseType=="permanent"))throw new CryptographicException("离线记录无效。");
    Context.Lease=lease;time.Restore(lease,store.Read<TrustedTimeState>("trusted-time.dat"));return;
   }
   Context.Lease=verifier.VerifyLease(saved.Envelope,device.DeviceId);Context.ForcedState=saved.Lock;
   time.Restore(Context.Lease,store.Read<TrustedTimeState>("trusted-time.dat"));
   log.Write("Licensing","LocalValidation",Context.State.ToString());
   if(Context.State==LicenseState.ClockRollbackSuspected||Due())await RefreshAsync(token);
  }catch(Exception e)when(e is CryptographicException or IOException or System.Text.Json.JsonException or LicenseException){
   Context.ForcedState=e is LicenseException le&&le.Code=="DEVICE_MISMATCH"?LicenseState.DeviceMismatch:LicenseState.InvalidSignature;
   LastError=Context.StatusText;log.Write("Licensing","LocalValidation","Rejected",detail:e.GetType().Name);
  }
 }
 public async Task ActivateAsync(string input,CancellationToken token=default)
 {
  string key=input.Trim().ToUpperInvariant();
  if(!Regex.IsMatch(key,@"^CLC(?:-[A-Z0-9]{1,4}){1,15}$"))throw new LicenseException("FORMAT","请输入 CLC-XXXX-XXXX-XXXX-XXXX 格式的授权码。");
  await mutex.WaitAsync(token);
  var oldState=Context.ForcedState;
  try{
   if(Context.State==LicenseState.Active)throw new LicenseException("ALREADY_ACTIVE","当前授权仍有效，请勿覆盖。");
   // Expired binding must be released through proof of possession before switching keys.
   if(saved is not null&&Context.Lease is {} old&&old.LicenseExpiresAt is {} end&&time.Now>=end)await RefreshCore(token);
   Context.ForcedState=LicenseState.Activating;
   var response=await api.Post(LicenseEndpoints.Activate,new {licenseKey=key,deviceId=device.DeviceId,devicePublicKey=device.PublicKeyPem,deviceName="Windows PC",windowsVersion=Environment.OSVersion.VersionString,appVersion="1.6.8"},token);
   Accept(LicenseApi.Envelope(response),key);
  }catch{if(Context.ForcedState==LicenseState.Activating)Context.ForcedState=oldState;throw;}
  finally{mutex.Release();}
 }
 public async Task RefreshAsync(CancellationToken token=default)
 {
  if(saved is null&&offline is null)throw new LicenseException("NO_LICENSE","请先输入授权码激活。");
  await mutex.WaitAsync(token);try{
   if(saved is null){
    var challenge=await api.Post("offline/challenge",new{deviceId=device.DeviceId},token);
    var nonce=challenge.GetProperty("nonce").GetString()??"";
    var response=await api.Post("offline/refresh",new{deviceId=device.DeviceId,nonce,signature=device.Sign(nonce),appVersion="1.6.8"},token);
    var licenseKey=response.TryGetProperty("licenseKey",out var keyElement)?keyElement.GetString():null;
    if(string.IsNullOrWhiteSpace(licenseKey))throw new LicenseException("INVALID_REFRESH","服务器未返回绑定授权信息。");
    Accept(LicenseApi.Envelope(response),licenseKey);
   }else await RefreshCore(token);
  }finally{mutex.Release();}
 }
 async Task RefreshCore(CancellationToken token)
 {
  if(saved is null)return;
  try {
   var challenge=await api.Post(LicenseEndpoints.Challenge,new {licenseKey=saved.LicenseKey,deviceId=device.DeviceId},token);
   var nonce=challenge.GetProperty("nonce").GetString()??"";
   var response=await api.Post(LicenseEndpoints.Refresh,new{licenseKey=saved.LicenseKey,deviceId=device.DeviceId,nonce,signature=device.Sign(nonce),windowsVersion=Environment.OSVersion.VersionString,appVersion="1.6.8"},token);
   Accept(LicenseApi.Envelope(response),saved.LicenseKey);
  }catch(LicenseException e) {
   LastError=e.Message; failures++;ScheduleRetry();
   LicenseState? state=e.Code switch{
    "LICENSE_EXPIRED_RELEASED" or "LICENSE_EXPIRED"=>LicenseState.Expired,
    "LICENSE_DISABLED"=>LicenseState.Suspended,
    "DEVICE_NOT_BOUND" or "LICENSE_NOT_FOUND"=>LicenseState.Revoked,
    "DEVICE_KEY_MISMATCH" or "DEVICE_MISMATCH" or "INVALID_DEVICE_SIGNATURE"=>LicenseState.DeviceMismatch,
    "INVALID_SIGNATURE" or "INVALID_LEASE"=>LicenseState.InvalidSignature,_=>null};
   if(state.HasValue){Context.ForcedState=state;saved=saved with{Lock=state};store.Write("license.dat",saved);}
   log.Write("Licensing","Refresh","Rejected",detail:e.Code);
  }catch(Exception e)when(e is HttpRequestException or TaskCanceledException or IOException){
   LastError="暂时无法连接授权服务；有效租约期间仍可离线使用。";
   failures++;ScheduleRetry();
   log.Write("Licensing","Refresh","Unavailable",detail:e.GetType().Name);
  }
 }
 void ScheduleRetry()=>nextRetry=clock.Uptime+TimeSpan.FromMinutes(failures switch{1=>1,2=>5,3=>15,_=>60});
 void Accept(SignedEnvelope envelope,string key)
 {
  var lease=verifier.VerifyLease(envelope,device.DeviceId);
  if(Context.Lease is {} previous&&lease.ServerTime<previous.ServerTime)throw new LicenseException("INVALID_LEASE","服务器返回了过旧的租约。");
  var first=saved?.LicenseKey==key?saved.FirstAcceptedUtc:lease.ServerTime;
  var next=new SavedLicense(key,envelope,first);
  var newTime=new TrustedTimeService(clock);newTime.Accept(lease);
  // State is committed before exposing any capability. A torn write requires an online refresh.
  store.Write("trusted-time.dat",newTime.Snapshot(lease));store.Write("license.dat",next);
  offline=null;store.Write<OfflineActivationRecord?>("offline-license.dat",null);OfflineSession?.Dispose();OfflineSession=null;
  saved=next;time.Accept(lease);Context.Lease=lease;Context.ForcedState=null;
  failures=0;expiryAttempted=false;nextRetry=TimeSpan.Zero;lastCheckpoint=clock.Uptime;LastError="";
  log.Write("Licensing","LeaseAccepted","Active",detail:lease.LicenseType);
 }
 bool Due()
 {
  var l=Context.Lease;if(l is null||!time.Initialized)return false;
  if(Context.ForcedState.HasValue)return false;
  var margin=TimeSpan.FromMinutes(Math.Min(15,(l.ExpiresAt-l.ServerTime).TotalMinutes/10));
  return time.RollbackSuspected||time.Now>=l.ExpiresAt-margin;
 }
 public async Task TickAsync()
 {
  if(IsBusy)return;
  if(saved is null){if(offline is not null&&time.Initialized&&clock.Uptime-lastCheckpoint>=TimeSpan.FromMinutes(2)){Checkpoint();lastCheckpoint=clock.Uptime;}return;}
  if(time.Initialized&&clock.Uptime-lastCheckpoint>=TimeSpan.FromMinutes(2)){Checkpoint();lastCheckpoint=clock.Uptime;}
  bool expired=Context.State==LicenseState.Expired&&!expiryAttempted&&Context.ForcedState is null;
  if(expired){expiryAttempted=true;await RefreshAsync();}
  else if(Due()&&clock.Uptime>=nextRetry)await RefreshAsync();
 }
 public void Checkpoint(){if(Context.Lease is not null&&time.Initialized&&!time.RollbackSuspected)store.Write("trusted-time.dat",time.Snapshot(Context.Lease));}
 public OfflineActivationSession BeginOfflineActivation()
 {
  if(IsBusy||Context.State==LicenseState.Active)throw new LicenseException("BUSY","当前状态不能创建离线会话。");
  OfflineSession?.Dispose();return OfflineSession=new(device.DeviceId,device.PublicKeyPem,clock.SystemUtc);
 }
 public async Task CompleteOfflineActivationAsync(string code)
 {
  await mutex.WaitAsync();try{
   if(Context.State==LicenseState.Active)throw new LicenseException("ALREADY_ACTIVE","当前授权仍有效。");
   var session=OfflineSession;
   if(session is null||!session.Verify(code,out var offlineType,out var offlineExpiry))throw new LicenseException("OFFLINE_CODE_INVALID","激活码不正确或二维码已超时。最多尝试 5 次，之后请重新生成二维码。");
   var lease=new Lease{Version=4,ApiVersion=3,LicenseId="offline:"+session.SessionId,DeviceId=device.DeviceId,Edition="standard",LicenseType=offlineType,IsPermanent=offlineType=="permanent",CountdownRequired=offlineType!="permanent",Features=["clean","scan","optimize"],IssuedAt=session.CreatedAt,ServerTime=session.CreatedAt,ExpiresAt=offlineExpiry??DateTimeOffset.MaxValue,LicenseExpiresAt=offlineExpiry,LeaseHours=0,RenewalProtocol="offline-v2",Nonce=session.SessionId};
   var newTime=new TrustedTimeService(clock);newTime.AcceptOffline(lease,session.Elapsed);
   var next=new OfflineActivationRecord(lease);
   store.Write("trusted-time.dat",newTime.Snapshot(lease));store.Write("offline-license.dat",next);store.Write<SavedLicense?>("license.dat",null);
   saved=null;offline=next;Context.Lease=lease;Context.ForcedState=null;time.AcceptOffline(lease,session.Elapsed);lastCheckpoint=clock.Uptime;LastError="";OfflineSession=null;
   log.Write("Licensing","OfflineActivation","Accepted",detail:offlineType);
  }finally{mutex.Release();}
 }
 public Task<string> DiagnosticsAsync(CancellationToken token=default)=>api.DiagnosticsAsync(verifier,token);
}

