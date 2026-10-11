param([string]$OutputName = 'voicemeeter-bug-20261008.exe')
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot $OutputName
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
$icon = Join-Path $PSScriptRoot 'voicemeeter-bug-icon-20261008.ico'
$compileArgs = @('/nologo','/target:winexe','/main:VoicemeeterBug.Program','/platform:x64','/optimize+','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',('/out:' + $output))
if (Test-Path -LiteralPath $icon) { $compileArgs += '/win32icon:' + $icon }
foreach($mood in @('idle','blink','happy','curious','worried','sad','sick','alarmed','sleep')) {
    $asset=Join-Path $PSScriptRoot ('bug-'+$mood+'-20261008.png')
    if(-not(Test-Path -LiteralPath $asset)){throw ('Missing Bug sprite: '+$asset)}
    $compileArgs += '/resource:'+$asset+',VoicemeeterBug.'+$mood
}
$sources = @('voicemeeter-bug-20261008.cs','audio-recovery-20261008.cs','bug-art-20261008.cs','telemetry-log-20261011.cs','audio-recovery-tests-20261008.cs','telemetry-log-tests-20261011.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @compileArgs $sources
if ($LASTEXITCODE -ne 0) { throw 'Voicemeeter Bug build failed.' }
Write-Output $output
