Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process MFAAvalonia -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '小工具')
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { Write-Output 'TEXT 小工具 not found (menu collapsed?)'; exit }
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$cur = $el
$target = $null
for ($i = 0; $i -lt 8 -and $cur -ne [System.Windows.Automation.AutomationElement]::RootElement; $i++) {
  if ($cur.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -or $cur.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem') { $target = $cur; break }
  $cur = $walker.GetParent($cur)
}
if (-not $target) { Write-Output 'no button ancestor found'; exit }
Write-Output ('invoking ancestor: ' + $target.Current.ControlType.ProgrammaticName)
$inv = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$inv.Invoke()
Write-Output 'INVOKED'
