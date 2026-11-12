# Builds MetroCIM Release and packages dist\Export-Setup.exe.
$ErrorActionPreference = 'Stop'

function Expand-LongPath {
    param([Parameter(Mandatory)][string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $missing = New-Object System.Collections.Generic.List[string]
    $probe = $full
    while (-not [System.IO.Directory]::Exists($probe) -and -not [System.IO.File]::Exists($probe)) {
        $name = [System.IO.Path]::GetFileName($probe)
        if ([string]::IsNullOrEmpty($name)) {
            return $full
        }

        $missing.Insert(0, $name)
        $parent = [System.IO.Path]::GetDirectoryName($probe)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $probe) {
            return $full
        }

        $probe = $parent
    }

    if (-not ('MetroCimLongPath' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
public static class MetroCimLongPath {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathName(string shortPath, StringBuilder buffer, uint bufferSize);
    public static string Expand(string path) {
        var buffer = new StringBuilder(1024);
        uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return length == 0 ? path : buffer.ToString();
    }
}
'@
    }

    $long = [MetroCimLongPath]::Expand($probe)
    foreach ($part in $missing) {
        $long = [System.IO.Path]::Combine($long, $part)
    }

    return $long
}

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\MetroCIM\MetroCIM.csproj'
$releaseDir = Join-Path $repo 'src\MetroCIM\bin\Release'
$dist = Join-Path $repo 'dist'
$tempRoot = Expand-LongPath ([System.IO.Path]::GetTempPath())
$stage = Join-Path $tempRoot 'MetroCIM-Setup-Staging'
$setupBuilt = Join-Path $tempRoot 'MetroCIM-Setup.exe'
$setupExe = Join-Path $dist 'Export-Setup.exe'

Write-Host "Building MetroCIM Release..."
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath (Join-Path $releaseDir 'MetroCIM.dll'))) {
    throw "Release build did not produce $releaseDir\MetroCIM.dll."
}

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-MetroCIM.ps1') -Destination (Join-Path $stage 'Install-MetroCIM.ps1') -Force
Copy-Item -LiteralPath (Join-Path $releaseDir 'MetroCIM.addin') -Destination (Join-Path $stage 'MetroCIM.addin') -Force

Get-ChildItem -LiteralPath $releaseDir -File | Where-Object {
    $_.Extension -in '.dll', '.json' -and $_.Name -notlike 'RevitAPI*'
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage $_.Name) -Force
}

@(
    '@echo off'
    'cd /d "%~dp0"'
    'powershell.exe -STA -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0Install-MetroCIM.ps1" %*'
    'exit /b %ERRORLEVEL%'
) | Set-Content -LiteralPath (Join-Path $stage 'launch.cmd') -Encoding ASCII

$files = @(Get-ChildItem -LiteralPath $stage -File)
$fileLines = New-Object System.Collections.Generic.List[string]
$sourceLines = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $files.Count; $i++) {
    $fileLines.Add(('FILE{0}="{1}"' -f $i, $files[$i].Name))
    $sourceLines.Add(('%FILE{0}%=' -f $i))
}

$sedPath = Join-Path $tempRoot 'MetroCIM-Setup.sed'
$stageForSed = $stage.TrimEnd('\') + '\'

$sed = @(
    '[Version]'
    'Class=IEXPRESS'
    'SEDVersion=3'
    '[Options]'
    'PackagePurpose=InstallApp'
    'ShowInstallProgramWindow=0'
    'HideExtractAnimation=1'
    'UseLongFileName=1'
    'InsideCompressed=0'
    'CAB_FixedSize=0'
    'CAB_ResvCodeSigning=0'
    'RebootMode=N'
    'InstallPrompt=%InstallPrompt%'
    'DisplayLicense=%DisplayLicense%'
    'FinishMessage=%FinishMessage%'
    'TargetName=%TargetName%'
    'FriendlyName=%FriendlyName%'
    'AppLaunched=%AppLaunched%'
    'PostInstallCmd=%PostInstallCmd%'
    'AdminQuietInstCmd=%AdminQuietInstCmd%'
    'UserQuietInstCmd=%UserQuietInstCmd%'
    'SourceFiles=SourceFiles'
    '[Strings]'
    'InstallPrompt='
    'DisplayLicense='
    'FinishMessage='
    "TargetName=$setupBuilt"
    'FriendlyName=Export'
    'AppLaunched=launch.cmd'
    'PostInstallCmd=<None>'
    'AdminQuietInstCmd='
    'UserQuietInstCmd='
) + $fileLines + @(
    '[SourceFiles]'
    "SourceFiles0=$stageForSed"
    '[SourceFiles0]'
) + $sourceLines

$sed | Set-Content -LiteralPath $sedPath -Encoding ASCII

if (Test-Path -LiteralPath $setupBuilt) {
    Remove-Item -LiteralPath $setupBuilt -Force
}

Write-Host "Packaging $setupExe..."
$iexpress = Join-Path $env:SystemRoot 'SysWOW64\iexpress.exe'
if (-not (Test-Path -LiteralPath $iexpress)) {
    $iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
}
& $iexpress /N /Q $sedPath
$deadline = (Get-Date).AddSeconds(30)
$lastSize = -1
$stable = 0
while ((Get-Date) -lt $deadline -and $stable -lt 2) {
    if (Test-Path -LiteralPath $setupBuilt) {
        $size = (Get-Item -LiteralPath $setupBuilt).Length
        if ($size -gt 0 -and $size -eq $lastSize) {
            $stable++
        }
        else {
            $stable = 0
            $lastSize = $size
        }
    }

    Start-Sleep -Milliseconds 400
}

if ($stable -lt 2) {
    throw "IExpress did not write $setupBuilt. Check $sedPath."
}

Copy-Item -LiteralPath $setupBuilt -Destination $setupExe -Force
$previousSetup = Join-Path $dist 'MetroCIM-Setup.exe'
if (Test-Path -LiteralPath $previousSetup) {
    Remove-Item -LiteralPath $previousSetup -Force
}

$item = Get-Item -LiteralPath $setupExe
Write-Host ("Setup written: {0} ({1:N0} KB)" -f $item.FullName, ($item.Length / 1KB))
