using CleanC.Core;
namespace CleanC.App;
public sealed class ShellViewModel : ObservableObject
{
 string status="准备就绪";bool busy;
 public string Status{get=>status;set=>Set(ref status,value);}
 public bool Busy{get=>busy;set=>Set(ref busy,value);}
 public ScanSummary? LastScan{get;set;}
 public string ScanRoot{get;}=Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
}

