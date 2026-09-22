using Microsoft.Win32;
using System.Text;
namespace CleanC.Core;

/// <summary>
/// Conservative index of applications currently registered with Windows.
/// It is used only to identify old AppData folders that are likely leftovers.
/// A folder is never considered orphaned merely because registry access failed.
/// </summary>
public sealed class InstalledApplicationIndex
{
 readonly HashSet<string> tokens=new(StringComparer.OrdinalIgnoreCase);
 public bool Available{get;private set;}
 public InstalledApplicationIndex()
 {
  try
  {
   foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser})
    foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
     ReadHive(hive,view);
   Available=tokens.Count>0;
  }
  catch{Available=false;tokens.Clear();}
 }
 void ReadHive(RegistryHive hive,RegistryView view)
 {
  try
  {
   using var baseKey=RegistryKey.OpenBaseKey(hive,view);
   using var key=baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
   if(key is null)return;
   foreach(var name in key.GetSubKeyNames())
   {
    try
    {
     using var app=key.OpenSubKey(name);if(app is null)continue;
     Add(app.GetValue("DisplayName") as string);
     Add(app.GetValue("Publisher") as string);
     Add(app.GetValue("InstallLocation") as string);
     Add(app.GetValue("DisplayIcon") as string);
    }
    catch{}
   }
  }
  catch{}
 }
 void Add(string? value)
 {
  if(string.IsNullOrWhiteSpace(value))return;
  var normalized=Normalize(value);if(normalized.Length>=4)tokens.Add(normalized);
  foreach(var part in value.Split(new[]{' ','-','_','.','\\','/','(',')','[',']',','},StringSplitOptions.RemoveEmptyEntries))
  {
   var p=Normalize(part);
   if(p.Length>=4&&!Common.Contains(p))tokens.Add(p);
  }
 }
 public bool LooksInstalled(string directoryName)
 {
  if(!Available)return true; // fail closed: no registry evidence means no orphan auto-cleaning
  var n=Normalize(directoryName);if(n.Length<4)return true;
  foreach(var token in tokens)
   if(token.Length>=4&&(token.Contains(n,StringComparison.OrdinalIgnoreCase)||n.Contains(token,StringComparison.OrdinalIgnoreCase)))return true;
  return false;
 }
 static string Normalize(string value)
 {
  var b=new StringBuilder(value.Length);
  foreach(var c in value)if(char.IsLetterOrDigit(c))b.Append(char.ToLowerInvariant(c));
  return b.ToString();
 }
 static readonly HashSet<string> Common=new(StringComparer.OrdinalIgnoreCase){"software","company","corporation","limited","windows","microsoft","program","application","technologies","technology","systems","system"};
}
