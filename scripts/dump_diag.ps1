Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process MFAAvalonia -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($e in $all) {
  try {
    $n = $e.Current.Name
    if ($n -and ($n.Contains('interface=') -or $n.Contains('EXC:') -or $n.Contains('博物研学') -or $n.Contains('冬谷竞赛') -or $n.Contains('遗境') -or $n.Contains('小工具'))) {
      Write-Output ('HIT [' + $e.Current.ControlType.ProgrammaticName + '] ' + $n.Substring(0, [Math]::Min(110, $n.Length)))
    }
  } catch {}
}
