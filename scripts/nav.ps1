# Selects a navigation item in AK980 Studio via UI Automation: nav.ps1 "Lighting"
param([string]$Name)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process KeyboardStudio | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { throw "No element named $Name" }
$el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 800
"selected $Name"
