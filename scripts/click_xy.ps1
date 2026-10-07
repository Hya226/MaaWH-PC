Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace Win32 -Name Mouse -MemberDefinition '
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);'
$proc = Get-Process MFAAvalonia -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '小工具')
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { Write-Output 'text not found'; exit }
$r = $el.Current.BoundingRectangle
$cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
[Win32.Mouse]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 120
[Win32.Mouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
[Win32.Mouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Write-Output ("clicked at " + $cx + "," + $cy)
