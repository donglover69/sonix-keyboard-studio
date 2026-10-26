# Decompiles a whole binary into one C file with Ghidra headless + DumpAll.java.
# Usage: pwsh -File re\ghidra\dump.ps1 [-Binary re\bin\DeviceDriver.exe] [-Out re\out\all.c] [-Min 401000] [-Max 556000] [-Project DeviceDriver]
# Set REA_TOOLS if the toolchain isn't in %USERPROFILE%\tools (ghidra_12.1.4_PUBLIC\, jdk-21*\).
param(
    [string]$Binary = "re\bin\DeviceDriver.exe",
    [string]$Out = "re\out\all.c",
    [string]$Min = "401000",
    [string]$Max = "556000",
    [string]$Project = "DeviceDriver"
)
$T = if ($env:REA_TOOLS) { $env:REA_TOOLS } else { Join-Path $env:USERPROFILE "tools" }
$jdk = Get-ChildItem $T -Directory -Filter "jdk-21*" | Select-Object -First 1
$env:JAVA_HOME = $jdk.FullName
$env:Path = "$($jdk.FullName)\bin;$env:Path"
$ghidra = Join-Path $T "ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$proj = Join-Path $root "re\out\ghidra"
New-Item -ItemType Directory -Force $proj | Out-Null
& $ghidra $proj $Project -import (Join-Path $root $Binary) -overwrite -scriptPath $PSScriptRoot -postScript DumpAll.java (Join-Path $root $Out) $Min $Max
