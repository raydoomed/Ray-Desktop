# P2-10 回归基线测试：编译并运行纯函数单测（无 dotnet SDK/NuGet，用 .NET Framework csc）。
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Could not find csc.exe. Install .NET Framework 4.x or Visual Studio Build Tools.'
}

$root = $PSScriptRoot
$testOut = Join-Path $root 'RayDesktopTests.exe'
$sources = @(
    (Join-Path $root 'RayDesktopTests\Program.cs'),
    (Join-Path $PSScriptRoot '..\src\ChildSessionDesktop\Geometry.cs'),
    (Join-Path $PSScriptRoot '..\src\ChildSessionDesktop\ClipboardProtocol.cs'),
    (Join-Path $PSScriptRoot '..\src\ChildSessionDesktop\ReconnectPolicy.cs')
)

& $compiler /nologo /target:exe ("/out:{0}" -f $testOut) @sources
if ($LASTEXITCODE -ne 0) { throw "Test build failed with exit code $LASTEXITCODE" }

& $testOut
exit $LASTEXITCODE
