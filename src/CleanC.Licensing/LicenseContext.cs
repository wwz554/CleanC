using CleanC.Core;
namespace CleanC.Licensing;
public sealed class LicenseContext(TrustedTimeService time) : ICapabilityGate
{
 public Lease? Lease {get;internal set;}
 public LicenseState? ForcedState{get;internal set;}
 public LicenseState State
 {
  get {
   if(ForcedState.HasValue)return ForcedState.Value;
   if(Lease is null)return LicenseState.Uninitialized;
   if(time.RollbackSuspected)return LicenseState.ClockRollbackSuspected;
   if(Lease.LicenseExpiresAt is {} end&&time.Now>=end)return LicenseState.Expired;
   if(time.Now>=Lease.ExpiresAt)return LicenseState.LeaseExpired;
   return LicenseState.Active;
  }
 }
 public string Countdown=>Lease is null?"尚未激活":Lease.IsPermanent?"永久授权":Display.Countdown(Lease.LicenseExpiresAt!.Value-time.Now);
 public DateTimeOffset? TrustedNow=>time.Initialized?time.Now:null;
 public void Demand(FeatureCapability feature){if(State!=LicenseState.Active||!Lease!.Has(feature))throw new LicenseException("CAPABILITY_DENIED",State==LicenseState.Active?"此授权未开放该功能。":StatusText);}
 public string StatusText=>State switch{
 LicenseState.Active=>"已激活",LicenseState.Activating=>"正在激活",
 LicenseState.Expired or LicenseState.ExpiredOffline=>"授权已到期，请联网验证或输入新的授权码。",
 LicenseState.LeaseExpired=>"需要联网验证授权：本次离线租约已结束。",
 LicenseState.ClockRollbackSuspected=>"系统时间发生异常变化，请连接网络验证授权。",
 LicenseState.InvalidSignature=>"授权签名或本地数据异常，请联网重新激活。",
 LicenseState.DeviceMismatch=>"设备身份不匹配，请重新验证。",
 LicenseState.Suspended or LicenseState.Revoked=>"授权已停用或设备已解绑。",
 LicenseState.ServerUnavailable=>"授权服务暂时不可用，请连接网络后重试。",
 _=>"请输入授权码以激活 CleanC。"
 };
}

