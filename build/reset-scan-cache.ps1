$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'CleanC\Data'
$marker = Join-Path $env:LOCALAPPDATA 'CleanC\startup.running'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Write-Host '仅重置 CleanC 扫描缓存。不会删除 License / Logs / Recovery。' -ForegroundColor Cyan
foreach($name in @('CleanC.db','CleanC.db-wal','CleanC.db-shm')) {
    $p = Join-Path $data $name
    if(Test-Path -LiteralPath $p) {
        Rename-Item -LiteralPath $p -NewName ($name + '.manual-reset-' + $stamp) -Force
        Write-Host "已隔离: $p"
    }
}
if(Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
Write-Host '完成。现在重新启动 CleanC。' -ForegroundColor Green
