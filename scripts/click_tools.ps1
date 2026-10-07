Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process MFAAvalonia -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$wr = $root.Current.BoundingRectangle
$wx = [double]$wr.X
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$buttons = @()
foreach ($e in $all) {
  try {
    $r = $e.Current.BoundingRectangle
    if ($r.Width -le 0 -or $r.Width -gt 5000 -or $r.Height -le 0) { continue }
    if ([double]$r.X -ge $wx -and [double]$r.X -le ($wx + 90) -and [double]$r.Y -ge ($wr.Y + 70) -and [double]$r.Y -le ($wr.Y + 500) -and $e.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') {
      $buttons += [pscustomobject]@{ El = $e; Y = [double]$r.Y }
    }
  } catch {}
}
$sorted = $buttons | Sort-Object Y
if ($sorted.Count -ge 2) {
  $sorted[1].El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Output 'INVOKED'
  Start-Sleep -Seconds 3
} else { Write-Output ('buttons: ' + $sorted.Count) }
