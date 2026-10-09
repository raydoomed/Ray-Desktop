$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Could not find the .NET Framework C# compiler.' }

$output = Join-Path $PSScriptRoot 'Raydesktop'
$references = @('/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
function Publish-BuildOutput([string]$temporary, [string]$target) {
    [IO.File]::Copy($temporary, $target, $true)
    [IO.File]::Delete($temporary)
}

$childSource = Join-Path $PSScriptRoot 'src\ChildSessionDesktop\Program.cs'
$clipboardRelaySource = Join-Path $PSScriptRoot 'src\ChildSessionDesktop\ClipboardFileRelay.cs'
$icon = Join-Path $PSScriptRoot 'assets\RayDesktop.ico'
$childTarget = Join-Path $output 'Raydesktop.exe'
$childTemporary = Join-Path $output 'Raydesktop.build.exe'
$aximpCandidates = @(
    'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\AxImp.exe',
    'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.7 Tools\AxImp.exe',
    'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.6 Tools\AxImp.exe'
)
$aximp = $aximpCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $aximp) { throw 'Could not find AxImp.exe. Install the Windows SDK .NET Framework 4.x tools, or set $aximp in build.ps1.' }
$interopDirectory = Join-Path ([IO.Path]::GetTempPath()) ('RayDesktopInterop-' + [Guid]::NewGuid().ToString('N'))
$axInterop = Join-Path $interopDirectory 'AxInterop.MSTSCLib.dll'
$interop = Join-Path $interopDirectory 'MSTSCLib.dll'
$setupSource = Join-Path $PSScriptRoot 'src\ChildSessionSetup\Program.cs'
$setupManifest = Join-Path $PSScriptRoot 'src\ChildSessionSetup\app.manifest'
$setupTarget = Join-Path $output 'EnableChildSessions.exe'
$setupTemporary = Join-Path $output 'EnableChildSessions.build.exe'
$setupReferences = @('/r:System.dll', '/r:System.Windows.Forms.dll')

New-Item -ItemType Directory -Force -Path $interopDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $output | Out-Null
try {
    & $aximp /nologo /silent ("/out:{0}" -f $axInterop) (Join-Path $env:WINDIR 'System32\mstscax.dll')
    if ($LASTEXITCODE -ne 0) { throw "RDP ActiveX wrapper generation failed with exit code $LASTEXITCODE" }

    $childReferences = $references + @(("/r:{0}" -f $interop), ("/r:{0}" -f $axInterop))
    & $compiler /nologo /target:winexe ("/out:{0}" -f $childTemporary) ("/win32icon:{0}" -f $icon) @childReferences $childSource $clipboardRelaySource
    if ($LASTEXITCODE -ne 0) { throw "Child-session GUI build failed with exit code $LASTEXITCODE" }

    & $compiler /nologo /target:winexe ("/out:{0}" -f $setupTemporary) ("/win32manifest:{0}" -f $setupManifest) @setupReferences $setupSource
    if ($LASTEXITCODE -ne 0) { throw "Child-session setup helper build failed with exit code $LASTEXITCODE" }

    Publish-BuildOutput $childTemporary $childTarget
    Publish-BuildOutput $setupTemporary $setupTarget
    Copy-Item -LiteralPath $interop -Destination (Join-Path $output 'MSTSCLib.dll') -Force
    Copy-Item -LiteralPath $axInterop -Destination (Join-Path $output 'AxInterop.MSTSCLib.dll') -Force
    Write-Host 'Built Raydesktop.exe, EnableChildSessions.exe, and the two RDP interop dependencies.'
}
finally {
    foreach ($temporary in @($childTemporary, $setupTemporary)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    if (Test-Path -LiteralPath $interopDirectory) {
        Get-ChildItem -LiteralPath $interopDirectory -Force | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
        }
        Remove-Item -LiteralPath $interopDirectory -Force
    }
}
