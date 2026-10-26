# Presses a button in the running app via UI Automation, by AutomationId or name: invoke.ps1 "key-115" | "Apply to keyboard"
# Works even when the window is behind other windows (no mouse involved).
param([string]$Target, [string]$Process = "AK980Studio")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process $Process | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$byId = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Target)
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId)
if (-not $el) {
  $byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Target)
  $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
}
if (-not $el) { throw "No element '$Target'" }
$el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 600
"invoked $Target"
