using System.Diagnostics;
using System.Runtime.InteropServices;
namespace CleanC.Licensing;
public interface ITimeSource {DateTimeOffset SystemUtc{get;}TimeSpan Uptime{get;}string BootId{get;}}
public sealed class WindowsTimeSource : ITimeSource
{
 public DateTimeOffset SystemUtc=>DateTimeOffset.UtcNow;
 public TimeSpan Uptime=>TimeSpan.FromMilliseconds(Environment.TickCount64);
 public string BootId{get;}=GetBootId();
 static string GetBootId(){int n=Marshal.SizeOf<BootInfo>();return NtQuerySystemInformation(90,out var info,n,out _)==0?info.BootIdentifier.ToString():"unavailable";}
 [StructLayout(LayoutKind.Sequential)]struct BootInfo{public Guid BootIdentifier;public int FirmwareType;public ulong BootFlags;}
 [DllImport("ntdll.dll")]static extern int NtQuerySystemInformation(int type,out BootInfo data,int size,out int length);
}
public sealed record TrustedTimeState(DateTimeOffset LastServerTimeUtc,DateTimeOffset LastValidatedAtUtc,DateTimeOffset LastObservedTrustedUtc,DateTimeOffset LocalUtc,TimeSpan Uptime,string BootId,string LeaseNonce,DateTimeOffset LeaseValidUntilUtc,DateTimeOffset? ExpiresAtUtc);
public sealed class TrustedTimeService(ITimeSource source)
{
 private DateTimeOffset anchor;
 private TimeSpan anchorUptime;
 public bool Initialized{get;private set;}
 public bool RollbackSuspected{get;private set;}
 public DateTimeOffset Now=>Initialized?anchor+Max(TimeSpan.Zero,source.Uptime-anchorUptime):throw new InvalidOperationException("尚无可信时间。");
 static TimeSpan Max(TimeSpan a,TimeSpan b)=>a>b?a:b;
 public void Accept(Lease lease){anchor=lease.ServerTime;anchorUptime=source.Uptime;Initialized=true;RollbackSuspected=false;}
 public void AcceptOffline(Lease lease,TimeSpan elapsed){Accept(lease);anchor+=elapsed;}
 public void Restore(Lease lease,TrustedTimeState? state)
 {
  if(state is null||state.LeaseNonce!=lease.Nonce||state.LastServerTimeUtc!=lease.ServerTime||state.LastObservedTrustedUtc<lease.ServerTime){Accept(lease);RollbackSuspected=true;return;}
  var local=source.SystemUtc;
  var elapsed=local-state.LocalUtc;
  RollbackSuspected=elapsed<TimeSpan.FromMinutes(-2);
  if(source.BootId!="unavailable"&&source.BootId==state.BootId){if(source.Uptime<state.Uptime)RollbackSuspected=true;elapsed=source.Uptime-state.Uptime;}
  anchor=state.LastObservedTrustedUtc+Max(TimeSpan.Zero,elapsed);anchorUptime=source.Uptime;Initialized=true;
 }
 public TrustedTimeState Snapshot(Lease lease)=>new(lease.ServerTime,lease.ServerTime,Now,source.SystemUtc,source.Uptime,source.BootId,lease.Nonce,lease.ExpiresAt,lease.LicenseExpiresAt);
}
