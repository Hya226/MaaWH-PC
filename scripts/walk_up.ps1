Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process MFAAvalonia -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '小工具')
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$cur = $el
for ($i = 0; $i -lt 10 -and $cur -ne $null -and $cur -ne [System.Windows.Automation.AutomationElement]::RootElement; $i++) {
  $ct = $cur.Current.ControlType.ProgrammaticName
  $patterns = ''
  foreach ($p in $cur.GetSupportedPatterns()) { $patterns += $p.ProgrammaticName + ' ' }
  Write-Output ("L$i " + $ct + " [" + $cur.Current.Name + "] " + $patterns)
  $cur = $walker.GetParent($cur)
}
