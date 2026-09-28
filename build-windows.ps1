param([switch]$Test)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$outputDir = Join-Path $projectDir 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$core = Join-Path $projectDir 'src\PatchEngine.cs'
$gui = Join-Path $projectDir 'src\Program.cs'
$manifest = Join-Path $projectDir 'src\app.manifest'
$app = Join-Path $outputDir 'FNFDriftLinkFix.exe'
& $compiler /nologo /optimize+ /target:winexe /platform:anycpu "/out:$app" "/win32manifest:$manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $core $gui
if ($LASTEXITCODE -ne 0) { throw 'Windows app build failed.' }

if ($Test) {
    $testExe = Join-Path $outputDir 'PatchEngineTests.exe'
    $testSource = Join-Path $projectDir 'tests\PatchEngineTests.cs'
    & $compiler /nologo /optimize+ /target:exe /platform:anycpu "/out:$testExe" /reference:System.dll /reference:System.Core.dll $core $testSource
    if ($LASTEXITCODE -ne 0) { throw 'Windows test build failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Windows patcher tests failed.' }
    $smoke = Start-Process -FilePath $app -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
    if (!$smoke.WaitForExit(30000)) {
        Stop-Process -Id $smoke.Id
        throw 'Windows app smoke test timed out.'
    }
    if ($smoke.ExitCode -ne 0) { throw 'Windows app smoke test failed.' }
    Write-Output 'Windows app smoke test passed.'
}
Write-Output "Built $app"
