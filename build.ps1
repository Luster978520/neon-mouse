$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $taskCompiler)) {
    throw '.NET Framework 4.x C# compiler was not found.'
}
$taskExe = Join-Path $PSScriptRoot 'NeonMouse.exe'
$taskIcon = Join-Path $PSScriptRoot 'app.ico'
& $taskCompiler /nologo /target:winexe /optimize+ /codepage:65001 /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "/win32icon:$taskIcon" "/resource:$taskIcon,NeonMouse.App.ico" "/out:$taskExe" (Join-Path $PSScriptRoot 'NeonMouse.cs') (Join-Path $PSScriptRoot 'Settings.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Exit NeonMouse before rebuilding.' }
Write-Output "Built: $taskExe"
