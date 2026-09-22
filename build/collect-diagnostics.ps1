$ErrorActionPreference = "SilentlyContinue"

$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$logDir = Join-Path $env:ProgramData ("CleanC\Logs\" + $sid)
$fallbackDir = Join-Path $env:LOCALAPPDATA "CleanC\Logs"
$out = Join-Path ([Environment]::GetFolderPath("Desktop")) "CleanC-diagnostics.txt"

"CleanC diagnostics" | Out-File $out -Encoding utf8
("Collected: " + [DateTimeOffset]::Now.ToString("O")) | Out-File $out -Append -Encoding utf8
("Windows: " + [Environment]::OSVersion.VersionString) | Out-File $out -Append -Encoding utf8
("SID: " + $sid) | Out-File $out -Append -Encoding utf8
"" | Out-File $out -Append -Encoding utf8

"=== CleanC log files ===" | Out-File $out -Append -Encoding utf8
foreach ($dir in @($logDir, $fallbackDir)) {
    if (Test-Path $dir) {
        ("Directory: " + $dir) | Out-File $out -Append -Encoding utf8
        Get-ChildItem $dir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 12 FullName,Length,LastWriteTime | Format-Table -AutoSize | Out-String | Out-File $out -Append -Encoding utf8
        foreach ($file in (Get-ChildItem $dir -File -Filter "*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 5)) {
            ("--- " + $file.FullName + " (tail 80) ---") | Out-File $out -Append -Encoding utf8
            Get-Content $file.FullName -Tail 80 | Out-File $out -Append -Encoding utf8
        }
    }
}

"" | Out-File $out -Append -Encoding utf8
"=== Windows Application events mentioning CleanC ===" | Out-File $out -Append -Encoding utf8
Get-WinEvent -FilterHashtable @{ LogName="Application"; StartTime=(Get-Date).AddHours(-24) } |
    Where-Object { $_.Message -match "CleanC.exe|CleanC" } |
    Select-Object -First 20 TimeCreated,Id,ProviderName,LevelDisplayName,Message |
    Format-List | Out-String | Out-File $out -Append -Encoding utf8

"" | Out-File $out -Append -Encoding utf8
"=== Scan database files ===" | Out-File $out -Append -Encoding utf8
$dbDir = Join-Path $env:LOCALAPPDATA "CleanC\Data"
if (Test-Path $dbDir) {
    Get-ChildItem $dbDir -File | Select-Object FullName,Length,LastWriteTime | Format-Table -AutoSize | Out-String | Out-File $out -Append -Encoding utf8
}

Write-Host ("Diagnostics written to: " + $out)


"`n=== Windows Error Reporting / WinUI events (last 4 hours) ===" | Out-File $out -Append -Encoding utf8
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddHours(-4)} -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -in @('Application Error','Windows Error Reporting','.NET Runtime') -and ($_.Message -match 'CleanC|Microsoft.UI.Xaml|0xc000027b') } |
  Select-Object -First 40 TimeCreated,Id,ProviderName,LevelDisplayName,Message | Format-List | Out-String -Width 240 | Out-File $out -Append -Encoding utf8
