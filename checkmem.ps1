# EyeLite memory usage
$p = Get-Process EyeLite -ErrorAction SilentlyContinue
if (-not $p) {
    Write-Host "EyeLite is not running." -ForegroundColor Yellow
} else {
    Write-Host "===== EyeLite memory =====" -ForegroundColor Cyan
    Write-Host ("Private commit : {0,8:N1} MB" -f ($p.PrivateMemorySize64 / 1MB))
    Write-Host ("Working set    : {0,8:N1} MB" -f ($p.WorkingSet64 / 1MB))

    $pws = $null
    try {
        $c = Get-Counter ('\Process(EyeLite)\Working Set - Private') -ErrorAction Stop
        $pws = $c.CounterSamples[0].CookedValue / 1MB
    } catch { }
    if ($null -ne $pws) {
        Write-Host ("Private WS (Task Manager value): {0,6:N1} MB" -f $pws)
    }

    Write-Host ""
    Write-Host ("Threads  : {0}" -f $p.Threads.Count)
    Write-Host ("Handles  : {0}" -f $p.HandleCount)
    Write-Host ("Started  : {0}" -f $p.StartTime)
    Write-Host ("Uptime   : {0:N0} min" -f ((Get-Date) - $p.StartTime).TotalMinutes)
}
