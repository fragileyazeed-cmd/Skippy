<#
.SYNOPSIS
  Measures what the running Skippy costs: CPU, RAM, GPU engines and VRAM.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\measure.ps1 -Seconds 30
#>
param([int]$Seconds = 30)

$p = Get-Process Skippy -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1
if (-not $p) { Write-Host "Skippy is not running."; exit 1 }
$id = $p.Id
$cores = [Environment]::ProcessorCount
Write-Host "Measuring Skippy (pid $id) for $Seconds s..."

$cpu0 = $p.TotalProcessorTime
$t0 = Get-Date
$gpu = Get-Counter -Counter "\GPU Engine(pid_${id}_*)\Utilization Percentage" -SampleInterval 1 -MaxSamples $Seconds -ErrorAction SilentlyContinue
$p.Refresh()
$cpuPct = ($p.TotalProcessorTime - $cpu0).TotalSeconds / ((Get-Date) - $t0).TotalSeconds / $cores * 100

$engines = $gpu.CounterSamples |
  Group-Object { ($_.InstanceName -split 'engtype_')[1] } |
  ForEach-Object { [pscustomobject]@{ Engine = $_.Name; 'Avg %' = [math]::Round(($_.Group | Measure-Object CookedValue -Sum).Sum / $Seconds, 2) } } |
  Where-Object { $_.'Avg %' -gt 0 }
$vram = (Get-Counter "\GPU Process Memory(pid_${id}_*)\Dedicated Usage" -ErrorAction SilentlyContinue).CounterSamples | Measure-Object CookedValue -Sum

Write-Host ""
Write-Host ("CPU          {0:N2} % of all {1} cores" -f $cpuPct, $cores)
Write-Host ("RAM          {0:N0} MB working set, {1:N0} MB private" -f ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB))
Write-Host ("VRAM         {0:N0} MB" -f ($vram.Sum / 1MB))
Write-Host ("Handles      {0}   Threads {1}" -f $p.HandleCount, $p.Threads.Count)
Write-Host ""
Write-Host "GPU engines used by Skippy (3D is shared with games; 'video codec' is the separate encoder chip):"
$engines | Format-Table -AutoSize
