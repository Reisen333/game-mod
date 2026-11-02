$ErrorActionPreference = 'Stop'
$game = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$managed = Join-Path $game 'The Northwood Lair_Data\Managed'
$bep = Join-Path $game 'BepInEx'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$build = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$references = @(
    (Join-Path $bep 'core\BepInEx.dll'),
    (Join-Path $bep 'core\0Harmony.dll'),
    (Join-Path $bep 'plugins\uuvr-mono-legacy\Uuvr.dll')
)
foreach ($name in @('UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.InputModule.dll',
    'UnityEngine.IMGUIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll','UnityEngine.XRModule.dll','UnityEngine.JSONSerializeModule.dll',
    'UnityEngine.PhysicsModule.dll','UnityEngine.AnimationModule.dll')) {
    $references += Join-Path $managed $name
}
$argsList = @('/nologo','/target:library','/optimize+','/codepage:65001',
    "/out:$build\NorthwoodVrFixes.dll", (Join-Path $PSScriptRoot 'Core.cs'),
    (Join-Path $PSScriptRoot 'Plugin.cs'), (Join-Path $PSScriptRoot 'Controllers.cs'),
    (Join-Path $PSScriptRoot 'ActorView.cs'),
    (Join-Path $PSScriptRoot 'Vendor\openvr_api.cs'))
foreach ($reference in $references) { $argsList += "/reference:$reference" }
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed' }
& $compiler /nologo /target:exe /optimize+ /codepage:65001 "/out:$build\CoreTests.exe" (Join-Path $PSScriptRoot 'Core.cs') (Join-Path $PSScriptRoot 'CoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& (Join-Path $build 'CoreTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
Write-Output "Built: $build\NorthwoodVrFixes.dll"
