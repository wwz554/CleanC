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
 readonly UpgradeChannel upgradeChannel;
 bool MigrationPending=>offline is {Envelope:null,CodeOnly:false};
 public bool CanValidateExisting=>!MigrationPending||upgradeChannel==UpgradeChannel.InApp;
 public LicenseContext Context{get;}
 public string DeviceId=>device.DeviceId;
 public bool DeviceKeyAvailable=>device.CanSign;
 public bool IsBusy=>mutex.CurrentCount==0;
 public string MaskedKey=>saved is null||saved.LicenseKey.Length<4?"—":"CLC-****-****-****-"+saved.LicenseKey[^4..];
 public string LastError{get;private set;}="";
 public DateTimeOffset? FirstAcceptedUtc=>saved?.FirstAcceptedUtc;
 public LicenseManager(LicenseApi api,SignatureVerifier verifier,IDeviceIdentity device,IProtectedStore store,ITimeSource clock,AuditLog log,UpgradeChannel upgradeChannel=UpgradeChannel.Manual)
 {this.api=api;this.verifier=verifier;this.device=device;this.store=store;this.clock=clock;this.log=log;this.upgradeChannel=upgradeChannel;time=new(clock);Context=new(time);}
 public async Task InitializeAsync(CancellationToken token=default)
 {
  try {
   saved=store.Read<SavedLicense>("license.dat");
   if(saved is null){
    offline=store.Read<OfflineActivationRecord>("offline-license.dat");
    if(offline is null)return;
    if(MigrationPending){
     // Legacy local fields are NOT proof of authorization. Keep them only for
     // recovery; never expose capabilities or an active countdown before signing.
     Context.ForcedState=LicenseState.UpgradeRequired;
     LastError=Context.StatusText;
     if(upgradeChannel==UpgradeChannel.InApp)await TryRecoverOfflineTimeAsync(token);
     return;
    }
    var lease=offline.Envelope is not null?verifier.VerifyOffline(offline.Envelope,device.DeviceId).Lease:ValidateCodeOnlyRecord(offline);
    Context.Lease=lease;Context.ForcedState=offline.Lock;time.Restore(lease,store.Read<TrustedTimeState>("trusted-time.dat"));
    log.Write("Licensing","OfflineLocalValidation",Context.State.ToString(),detail:"offline-v3; signature and checkpoint restored");
    if(Context.State==LicenseState.ClockRollbackSuspected)await TryRecoverOfflineTimeAsync(token);
    return;
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
  if(!DeviceKeyAvailable)throw new LicenseException("DEVICE_KEY_MISSING","原设备密钥不可用，原身份和授权已保留，请联系管理员恢复，不能静默改绑。");
  string key=input.Trim().ToUpperInvariant();
  if(!Regex.IsMatch(key,@"^CLC(?:-[A-Z0-9]{1,4}){1,15}$"))throw new LicenseException("FORMAT","请输入 CLC-XXXX-XXXX-XXXX-XXXX 格式的授权码。");
  await mutex.WaitAsync(token);
  var oldState=Context.ForcedState;
  try{
   if(Context.State==LicenseState.Active)throw new LicenseException("ALREADY_ACTIVE","当前授权仍有效，请勿覆盖。");
   // Expired binding must be released through proof of possession before switching keys.
   if(saved is not null&&Context.Lease is {} old&&old.LicenseExpiresAt is {} end&&time.Now>=end)await RefreshCore(token);
   if(saved is null&&offline is not null&&Context.Lease?.LicenseExpiresAt is {} offlineEnd&&time.Now>=offlineEnd){
    try{await RefreshOfflineCore(token);}catch(LicenseException e)when(e.Code is "LICENSE_EXPIRED_RELEASED" or "DEVICE_NOT_BOUND"){}
   }
   Context.ForcedState=LicenseState.Activating;
   var response=await api.Post(LicenseEndpoints.Activate,new {licenseKey=key,deviceId=device.DeviceId,devicePublicKey=device.PublicKeyPem,deviceName="Windows PC",windowsVersion=Environment.OSVersion.VersionString,appVersion="1.7.3"},token);
   Accept(LicenseApi.Envelope(response),key);
  }catch{if(Context.ForcedState==LicenseState.Activating)Context.ForcedState=oldState;throw;}
  finally{mutex.Release();}
 }
 public async Task RefreshAsync(CancellationToken token=default)
 {
  if(saved is null&&offline is null)throw new LicenseException("NO_LICENSE","请先输入授权码激活。");
  if(MigrationPending&&!CanValidateExisting)throw new LicenseException("UPGRADE_REQUIRED",Context.StatusText);
  await mutex.WaitAsync(token);try{
   if(saved is null)await RefreshOfflineCore(token);else await RefreshCore(token);
  }finally{mutex.Release();}
 }
 async Task RefreshOfflineCore(CancellationToken token)
 {
  try{
    var challenge=await api.Post("offline/challenge",new{deviceId=device.DeviceId},token);
    var nonce=challenge.GetProperty("nonce").GetString()??"";
    var response=await api.Post("offline/refresh",new{deviceId=device.DeviceId,nonce,signature=device.Sign(nonce),appVersion="1.7.3"},token);
    var licenseKey=response.TryGetProperty("licenseKey",out var keyElement)?keyElement.GetString():null;
    if(string.IsNullOrWhiteSpace(licenseKey))throw new LicenseException("INVALID_REFRESH","服务器未返回绑定授权信息。");
    if(!response.TryGetProperty("offlineProof",out var signed))throw new LicenseException("SERVER_UPGRADE_REQUIRED","授权服务尚未升级，请稍后重试；原授权已保留。");
    AcceptOfflineValidation(LicenseApi.Envelope(signed),nonce);
  }catch(LicenseException e){
   LastError=e.Message;
   LicenseState? state=e.Code switch{
    "LICENSE_EXPIRED_RELEASED" or "LICENSE_EXPIRED"=>LicenseState.Expired,
    "LICENSE_DISABLED"=>LicenseState.Suspended,
    "DEVICE_NOT_BOUND" or "LICENSE_NOT_FOUND"=>LicenseState.Revoked,
    "DEVICE_KEY_MISMATCH" or "DEVICE_MISMATCH" or "INVALID_DEVICE_SIGNATURE"=>LicenseState.DeviceMismatch,
    "INVALID_SIGNATURE" or "INVALID_LEASE"=>LicenseState.InvalidSignature,_=>null};
   if(state.HasValue&&offline is not null){Context.ForcedState=state;offline=offline with{Lock=state};store.Write("offline-license.dat",offline);}
   log.Write("Licensing","OfflineRefresh","Rejected",detail:e.Code);throw;
  }
 }
 async Task RefreshCore(CancellationToken token)
 {
  if(saved is null)return;
  try {
   var challenge=await api.Post(LicenseEndpoints.Challenge,new {licenseKey=saved.LicenseKey,deviceId=device.DeviceId},token);
   var nonce=challenge.GetProperty("nonce").GetString()??"";
   var response=await api.Post(LicenseEndpoints.Refresh,new{licenseKey=saved.LicenseKey,deviceId=device.DeviceId,nonce,signature=device.Sign(nonce),windowsVersion=Environment.OSVersion.VersionString,appVersion="1.7.3"},token);
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
 void AcceptOfflineValidation(SignedEnvelope envelope,string nonce)
 {
  var proof=verifier.VerifyOffline(envelope,device.DeviceId);
  if(proof.ChallengeNonce!=nonce||proof.RequestHash.Length!=0)throw new LicenseException("INVALID_LEASE","服务器凭证不属于本次验证，请重试。");
  var lease=proof.Lease;
  // Store the exact signed offline entitlement, not an unsigned derived lease.
  // Server total expiry remains authoritative; a 72-hour lease is never substituted.
  var newTime=new TrustedTimeService(clock);newTime.Accept(lease);
  var next=new OfflineActivationRecord(lease,Envelope:envelope);
  store.Write("trusted-time.dat",newTime.Snapshot(lease));store.Write("offline-license.dat",next);
  offline=next;time.Accept(lease);Context.Lease=lease;Context.ForcedState=null;OfflineSession?.Dispose();OfflineSession=null;
  failures=0;expiryAttempted=false;nextRetry=TimeSpan.Zero;lastCheckpoint=clock.Uptime;LastError="";
  log.Write("Licensing","OfflineValidation","Accepted",detail:lease.LicenseType);
 }
 void Accept(SignedEnvelope envelope,string key)
 {
  var lease=verifier.VerifyLease(envelope,device.DeviceId);
  // offline-v2's ServerTime was supplied by the local clock, not signed by the
  // server. Do not let a fast local clock permanently prevent signed recovery.
  if(Context.Lease is {RenewalProtocol:not ("offline-v2" or "offline-code16")} previous&&lease.ServerTime<previous.ServerTime)throw new LicenseException("INVALID_LEASE","服务器返回了过旧的租约。");
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
 async Task TryRecoverOfflineTimeAsync(CancellationToken token=default)
 {
  // Keep the original DPAPI record and device identity. Only a verified server
  // lease may repair uncertain time; network failure is NOT a signature failure.
  try{await RefreshAsync(token);}
  catch(Exception e)when(e is LicenseException or HttpRequestException or TaskCanceledException or IOException)
  {
   LastError=Context.State==LicenseState.UpgradeRequired
    ?"旧离线授权已保留，升级验证暂未完成；联网后将自动重试，也可用原授权码扫码领取 16 位激活码。"
    :Context.State==LicenseState.ClockRollbackSuspected
    ?"原离线授权已保留，但时间校验未通过；请校准 Windows 日期时间并联网验证，无需删除授权或更换设备码。"
    :Context.StatusText;
   failures++;ScheduleRetry();log.Write("Licensing","OfflineTimeRecovery","Deferred",detail:e is LicenseException le?le.Code:e.GetType().Name);
  }
 }
 public async Task TickAsync()
 {
  if(IsBusy)return;
  if(saved is null){
   if(offline is not null&&(Context.State==LicenseState.ClockRollbackSuspected||(Context.State==LicenseState.UpgradeRequired&&CanValidateExisting))&&clock.Uptime>=nextRetry)await TryRecoverOfflineTimeAsync();
   if(offline is not null&&time.Initialized&&clock.Uptime-lastCheckpoint>=TimeSpan.FromMinutes(2)){Checkpoint();lastCheckpoint=clock.Uptime;}
   return;
  }
  if(time.Initialized&&clock.Uptime-lastCheckpoint>=TimeSpan.FromMinutes(2)){Checkpoint();lastCheckpoint=clock.Uptime;}
  bool expired=Context.State==LicenseState.Expired&&!expiryAttempted&&Context.ForcedState is null;
  if(expired){expiryAttempted=true;await RefreshAsync();}
  else if(Due()&&clock.Uptime>=nextRetry)await RefreshAsync();
 }
 public void Checkpoint(){if(Context.Lease is not null&&time.Initialized&&!time.RollbackSuspected)store.Write("trusted-time.dat",time.Snapshot(Context.Lease));}
 public OfflineActivationSession BeginOfflineActivation()
 {
  if(!DeviceKeyAvailable)throw new LicenseException("DEVICE_KEY_MISSING","原设备密钥不可用，不能自动更换设备身份；请联系管理员恢复原授权。");
  if(IsBusy||Context.State==LicenseState.Active)throw new LicenseException("BUSY","当前状态不能创建离线会话。");
  OfflineSession?.Dispose();return OfflineSession=new(device.DeviceId,device.PublicKeyPem,clock.SystemUtc);
 }
 public async Task CompleteOfflineActivationAsync(string code,string? credentialFile=null)
 {
  await mutex.WaitAsync();try{
   if(Context.State==LicenseState.Active)throw new LicenseException("ALREADY_ACTIVE","当前授权仍有效。");
   if(string.IsNullOrWhiteSpace(credentialFile)){
    var codeSession=OfflineSession;
    if(codeSession is null||!codeSession.Verify(code,out var type,out var end))
     throw new LicenseException("OFFLINE_CODE_INVALID","激活码不正确或扫码已超时。最多尝试 5 次，请使用当前扫码返回的 16 位码。");
    // Explicit compatibility mode, NOT a public-key signature. Never label it signed.
    var shortLease=new Lease{Version=4,ApiVersion=3,LicenseId="offline-"+codeSession.SessionId,DeviceId=device.DeviceId,Edition="standard",
     LicenseType=type,IsPermanent=end is null,CountdownRequired=end is not null,Features=["scan","clean","optimize"],
     IssuedAt=codeSession.CreatedAt,ServerTime=codeSession.CreatedAt,ExpiresAt=end??DateTimeOffset.MaxValue,LicenseExpiresAt=end,
     LeaseHours=0,RenewalProtocol="offline-code16",Nonce=codeSession.SessionId};
    var shortRecord=new OfflineActivationRecord(shortLease,CodeOnly:true);var shortTime=new TrustedTimeService(clock);shortTime.AcceptOffline(shortLease,codeSession.Elapsed);
    store.Write("trusted-time.dat",shortTime.Snapshot(shortLease));store.Write("offline-license.dat",shortRecord);store.Write<SavedLicense?>("license.dat",null);
    saved=null;offline=shortRecord;Context.Lease=shortLease;Context.ForcedState=null;time.AcceptOffline(shortLease,codeSession.Elapsed);
    lastCheckpoint=clock.Uptime;LastError="";OfflineSession=null;failures=0;expiryAttempted=false;nextRetry=TimeSpan.Zero;
    log.Write("Licensing","OfflineActivation","Accepted",detail:"code-only; "+type);return;
   }
   var envelope=OfflineCredentialFile.Parse(credentialFile);
   var proof=verifier.VerifyOffline(envelope,device.DeviceId);
   var session=OfflineSession;
   if(session is null||proof.RequestHash!=OfflineCredentialFile.Hash(session.Request))throw new LicenseException("CREDENTIAL_SESSION","凭证不属于本次扫码，请选择当前二维码领取的文件，或重新扫码领取。");
   if(session is null||!session.Verify(code,out var offlineType,out var offlineExpiry))throw new LicenseException("OFFLINE_CODE_INVALID","激活码不正确或二维码已超时。最多尝试 5 次，之后请重新生成二维码。");
   var lease=proof.Lease;
   if(proof.CodeHash!=OfflineCredentialFile.Hash(OfflineActivationSession.Normalize(code))||lease.LicenseType!=offlineType||
    (offlineExpiry.HasValue&&(lease.LicenseExpiresAt<offlineExpiry||lease.LicenseExpiresAt>=offlineExpiry.Value.AddMinutes(1))))
    throw new LicenseException("CREDENTIAL_CODE","激活码与签名凭证不匹配，请重新扫码领取。");
   var newTime=new TrustedTimeService(clock);newTime.AcceptOffline(lease,session.Elapsed);
   var next=new OfflineActivationRecord(lease,Envelope:envelope);
   store.Write("trusted-time.dat",newTime.Snapshot(lease));store.Write("offline-license.dat",next);store.Write<SavedLicense?>("license.dat",null);
   saved=null;offline=next;Context.Lease=lease;Context.ForcedState=null;time.AcceptOffline(lease,session.Elapsed);lastCheckpoint=clock.Uptime;LastError="";OfflineSession=null;
   log.Write("Licensing","OfflineActivation","Accepted",detail:offlineType);
  }finally{mutex.Release();}
 }
 public Task<string> DiagnosticsAsync(CancellationToken token=default)=>api.DiagnosticsAsync(verifier,token);
 Lease ValidateCodeOnlyRecord(OfflineActivationRecord record)
 {
  var l=record.Lease;
  if(!record.CodeOnly||l.RenewalProtocol!="offline-code16"||l.Version!=4||l.DeviceId!=device.DeviceId||
    l.Nonce.Length!=32||l.ServerTime!=l.IssuedAt||l.LeaseHours!=0||!l.Features.SequenceEqual(new[]{"scan","clean","optimize"})||
    (l.IsPermanent?l.LicenseType!="permanent"||l.LicenseExpiresAt is not null||l.ExpiresAt!=DateTimeOffset.MaxValue:
      l.LicenseType is not ("duration" or "fixed")||l.LicenseExpiresAt is null||l.ExpiresAt!=l.LicenseExpiresAt||l.ExpiresAt<=l.ServerTime))
   throw new LicenseException("INVALID_LEASE","离线授权记录无效，请用原授权码重新扫码。");
  return l;
 }
}
