$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Could not find the .NET Framework C# compiler (csc.exe). Install .NET Framework 4.x or Visual Studio Build Tools.'
}

$output = Join-Path $PSScriptRoot 'Raydesktop'
$references = @('/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
function Publish-BuildOutput([string]$temporary, [string]$target) {
    [IO.File]::Copy($temporary, $target, $true)
    [IO.File]::Delete($temporary)
}

# ---- Ray Desktop (ChildSessionDesktop) 源文件 ----
$childSources = @(
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\Program.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\Strings.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\ClipboardFileRelay.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\DesktopForm.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\Geometry.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\ClipboardProtocol.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\ReconnectPolicy.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\ChildSessionControl.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\ToolbarForm.cs'),
    (Join-Path $PSScriptRoot 'src\ChildSessionDesktop\TooltipForm.cs')
)
$childManifest = Join-Path $PSScriptRoot 'src\ChildSessionDesktop\app.manifest'
$icon = Join-Path $PSScriptRoot 'assets\RayDesktop.ico'
$childTarget = Join-Path $output 'Raydesktop.exe'
$childTemporary = Join-Path $output 'Raydesktop.build.exe'

# ---- 版本号与 AssemblyInfo（P2-14）：发布时更新 $version，脚本据此生成程序集版本 ----
$version = '1.3.0.0'
$childAssemblyInfo = Join-Path ([IO.Path]::GetTempPath()) ('RayDesktopAssemblyInfo-' + [Guid]::NewGuid().ToString('N') + '.cs')
[IO.File]::WriteAllText($childAssemblyInfo,
    "using System.Reflection;`r`n[assembly: AssemblyTitle(`"Ray Desktop`")]`r`n[assembly: AssemblyVersion(`"$version`")]`r`n[assembly: AssemblyFileVersion(`"$version`")]`r`n",
    [Text.Encoding]::UTF8)
$childSources = @($childSources) + @($childAssemblyInfo)
# setup binary gets the same injected version (P1-9-2 version check as unsigned-helper fallback)
$setupAssemblyInfo = Join-Path ([IO.Path]::GetTempPath()) ('RayDesktopSetupAssemblyInfo-' + [Guid]::NewGuid().ToString('N') + '.cs')
[IO.File]::WriteAllText($setupAssemblyInfo,
    "using System.Reflection;`r`n[assembly: AssemblyTitle(`"Ray Desktop Setup`")]`r`n[assembly: AssemblyVersion(`"$version`")]`r`n[assembly: AssemblyFileVersion(`"$version`")]`r`n",
    [Text.Encoding]::UTF8)

# ---- AxImp.exe detection: registry-installed SDK roots + RAYDESKTOP_SDK_ROOT override + standard dirs ----
# Reads the real install root from the registry (works even when the SDK lives on a custom drive/path),
# honors an optional RAYDESKTOP_SDK_ROOT override, and keeps standard Program Files as a fallback.
$aximp = $null
$sdkRoots = New-Object System.Collections.Generic.List[string]
if ($env:RAYDESKTOP_SDK_ROOT) { $sdkRoots.Add($env:RAYDESKTOP_SDK_ROOT) }
foreach ($reg in @(
    'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots',
    'HKLM:\SOFTWARE\Microsoft\Microsoft SDKs\Windows',
    'HKLM:\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows')) {
    try {
        $item = Get-ItemProperty -LiteralPath $reg -ErrorAction Stop
        foreach ($prop in @('KitsRoot10', 'KitsRoot81', 'CurrentInstallFolder')) {
            $val = $item.$prop
            if ($val) { $sdkRoots.Add([string]$val) }
        }
    }
    catch { }
}
foreach ($root in @(
    'C:\Program Files (x86)\Windows Kits\10\bin',
    'C:\Program Files (x86)\Microsoft SDKs\Windows',
    'C:\Program Files\Microsoft SDKs\Windows')) {
    $sdkRoots.Add($root)
}
$seen = @{}
foreach ($root in $sdkRoots) {
    if (-not $root -or $seen.ContainsKey($root)) { continue }
    $seen[$root] = $true
    if (-not [IO.Directory]::Exists($root)) { continue }
    $candidates = $null
    try {
        $candidates = @([IO.Directory]::GetFiles($root, 'AxImp.exe', [IO.SearchOption]::AllDirectories))
    }
    catch {
        continue
    }
    if ($candidates.Length -gt 0) {
        $candidates = @($candidates | Sort-Object -Descending)
        $aximp = $candidates[0]   # pick highest-version hit, avoid old version / wrong arch
        break
    }
}
if (-not $aximp) {
    throw 'Could not find AxImp.exe under the Windows SDK directories. Install the Windows SDK (which includes the .NET Framework 4.x tools) and re-run this script.'
}

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
    & $compiler /nologo /target:winexe /optimize+ /platform:anycpu /debug- ("/out:{0}" -f $childTemporary) ("/win32icon:{0}" -f $icon) ("/win32manifest:{0}" -f $childManifest) @childReferences @childSources
    if ($LASTEXITCODE -ne 0) { throw "Child-session GUI build failed with exit code $LASTEXITCODE" }

    & $compiler /nologo /target:winexe /optimize+ /platform:anycpu /debug- ("/out:{0}" -f $setupTemporary) ("/win32manifest:{0}" -f $setupManifest) @setupReferences $setupSource $setupAssemblyInfo
    if ($LASTEXITCODE -ne 0) { throw "Child-session setup helper build failed with exit code $LASTEXITCODE" }

    Publish-BuildOutput $childTemporary $childTarget
    Publish-BuildOutput $setupTemporary $setupTarget
    Copy-Item -LiteralPath $interop -Destination (Join-Path $output 'MSTSCLib.dll') -Force
    Copy-Item -LiteralPath $axInterop -Destination (Join-Path $output 'AxInterop.MSTSCLib.dll') -Force
    Write-Host 'Built Raydesktop.exe, EnableChildSessions.exe, and the two RDP interop dependencies.'
}
finally {
    foreach ($temporary in @($childTemporary, $setupTemporary, $childAssemblyInfo, $setupAssemblyInfo)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    if (Test-Path -LiteralPath $interopDirectory) {
        Get-ChildItem -LiteralPath $interopDirectory -Force | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
        }
        Remove-Item -LiteralPath $interopDirectory -Force
    }
}
