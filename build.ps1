$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

Get-Process BrightnessSteps -ErrorAction SilentlyContinue | Stop-Process -Force

& $csc /nologo /optimize /target:winexe /win32icon:"$root\app.ico" `
    /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.dll `
    /out:"$root\BrightnessSteps.exe" "$root\BrightnessSteps.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Start-Process "$root\BrightnessSteps.exe" -WorkingDirectory $root
"built and restarted"

