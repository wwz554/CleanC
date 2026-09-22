using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace CleanC.Core;
public enum SafetyLevel { Safe, Optional, UserData, Protected }
public enum FeatureCapability { Scan, Cleanup, SystemRepair, AdvancedAnalysis }
public enum LicenseState { Uninitialized, Activating, Active, LeaseExpired, Expired, ExpiredOffline, ClockRollbackSuspected, Revoked, Suspended, DeviceMismatch, InvalidSignature, ServerUnavailable }
public interface ICapabilityGate { void Demand(FeatureCapability capability); }
public record FileSnapshot(string Path, long Size, DateTime LastWriteUtc, DateTime CreationUtc, FileAttributes Attributes, ulong FileId = 0, uint Volume = 0, uint Links = 1);
public record Classification(SafetyLevel Safety, string Category, string Reason, string? RuleId = null) { public bool DefaultSelected => Safety == SafetyLevel.Safe; }
public record ScanItem(long Id, FileSnapshot File, Classification Classification, bool Selected = false);
public record ScanProgress(long Files, long Directories, long Bytes, long SafeBytes, string CurrentPath, int Skipped);
public record ScanSummary(string Root, long Files, long Directories, long Bytes, long SafeBytes, int Skipped, bool Canceled, string Engine, TimeSpan Elapsed);
public record DirectoryStat(string Path, string Name, long Bytes, long Files, bool IsDirectory);
public record CleanupOutcome(string Path, string Result, long Bytes, string Detail);
public record CleanupReport(DateTimeOffset StartedAt, bool DryRun, long FreedBytes, int Deleted, int Skipped, bool Canceled, IReadOnlyList<CleanupOutcome> Items);
public record CleanupProgress(int Completed, int Total, long FreedBytes, string CurrentPath);
public interface IUpdateService { Task<string> CheckAsync(CancellationToken token); }
public interface IIntegrityService { Task<bool> VerifyAsync(CancellationToken token); }
public record Result<T>(T? Value, string? Error) { public bool IsSuccess => Error is null; }
public abstract class ObservableObject : INotifyPropertyChanged
{
 public event PropertyChangedEventHandler? PropertyChanged;
 protected bool Set<T>(ref T field,T value,[CallerMemberName] string? name=null) { if(EqualityComparer<T>.Default.Equals(field,value)) return false; field=value; PropertyChanged?.Invoke(this,new(name)); return true; }
 protected void Raise([CallerMemberName] string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
public static class Display
{
 public static string Bytes(long n) { string[] u=["B","KB","MB","GB","TB"]; double v=n; int i=0; while(v>=1024&&i<4){v/=1024;i++;} return $"{v:0.##} {u[i]}"; }
 public static string Countdown(TimeSpan remaining) { if(remaining<TimeSpan.Zero)remaining=TimeSpan.Zero; return remaining.TotalDays>=1 ? $"{(int)remaining.TotalDays}天 {remaining:hh\\:mm\\:ss}" : $"{remaining:hh\\:mm\\:ss}"; }
}


