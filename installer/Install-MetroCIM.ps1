# Installs MetroCIM into the current user's Revit 2025 Addins folder.
# Packaged setup: files sit next to this script.
# From the repo: falls back to src\MetroCIM\bin\Release.
param(
    [switch]$Uninstall,
    [switch]$Silent,
    [string]$PayloadDir,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

$RevitVersion = '2025'
$ProductName = 'Export'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Export'
$PreviousUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MetroCIM'
$LocalDir = Join-Path $env:LOCALAPPDATA 'Export'

function Get-PayloadDir {
    param([string]$Requested)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        $candidates += $Requested
    }

    $candidates += $PSScriptRoot
    $candidates += (Join-Path $PSScriptRoot '..\src\MetroCIM\bin\Release')

    foreach ($dir in $candidates) {
        if ([string]::IsNullOrWhiteSpace($dir)) {
            continue
        }

        $dll = Join-Path $dir 'MetroCIM.dll'
        if (Test-Path -LiteralPath $dll) {
            return (Resolve-Path -LiteralPath $dir).Path
        }
    }

    throw "MetroCIM.dll was not found. Build Release, then run installer\Build-Installer.ps1."
}

function Test-RevitOpen {
    return [bool](Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
}

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

function Remove-DiskPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.File]::Exists($Path)) {
        [System.IO.File]::Delete($Path)
        return
    }

    if ([System.IO.Directory]::Exists($Path)) {
        [System.IO.Directory]::Delete($Path, $true)
    }
}

function Get-PayloadFiles {
    param([Parameter(Mandatory)][string]$Dir)

    return @(Get-ChildItem -LiteralPath $Dir -File | Where-Object {
            $_.Extension -in '.dll', '.json' -and $_.Name -notlike 'RevitAPI*'
        })
}

function Remove-LegacyManifest {
    param([Parameter(Mandatory)][string]$AddinsDir)

    $legacy = Join-Path $AddinsDir 'RevitXKT.addin'
    Remove-DiskPath -Path $legacy
}

function Register-Uninstall {
    param(
        [Parameter(Mandatory)][string]$AddinsDir,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][int]$SizeKb
    )

    New-Item -ItemType Directory -Force -Path $LocalDir | Out-Null
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $LocalDir 'Install-MetroCIM.ps1') -Force

    $scriptPath = Join-Path $LocalDir 'Install-MetroCIM.ps1'
    $uninstall = "powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Uninstall"

    if (Test-Path -LiteralPath $PreviousUninstallKey) {
        Remove-Item -LiteralPath $PreviousUninstallKey -Force
    }

    if (-not (Test-Path -LiteralPath $UninstallKey)) {
        New-Item -Path $UninstallKey -Force | Out-Null
    }

    New-ItemProperty -Path $UninstallKey -Name DisplayName -Value $ProductName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name Publisher -Value 'Metropolitan CIM' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name DisplayVersion -Value $Version -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name UninstallString -Value $uninstall -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name InstallLocation -Value $AddinsDir -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $UninstallKey -Name EstimatedSize -Value $SizeKb -PropertyType DWord -Force | Out-Null
}

function Remove-UninstallRegistration {
    if (Test-Path -LiteralPath $UninstallKey) {
        Remove-Item -LiteralPath $UninstallKey -Force
    }

    if (-not (Test-Path -LiteralPath $LocalDir)) {
        return
    }

    $runningHere = [string]::Equals(
        (Resolve-Path -LiteralPath $PSScriptRoot).Path,
        (Resolve-Path -LiteralPath $LocalDir).Path,
        [System.StringComparison]::OrdinalIgnoreCase)

    if (-not $runningHere) {
        Remove-DiskPath -Path $LocalDir
        return
    }

    $cleanup = Join-Path $env:TEMP 'metrocim-uninstall-cleanup.cmd'
    @(
        '@echo off'
        'timeout /t 2 /nobreak >nul'
        "rmdir /s /q `"$LocalDir`""
        'del /f /q "%~f0"'
    ) | Set-Content -LiteralPath $cleanup -Encoding ASCII
    Start-Process -FilePath $cleanup -WindowStyle Hidden
}

function Install-MetroCim {
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$AddinsDir,
        [switch]$ManageArp
    )

    if ($ManageArp -and (Test-RevitOpen)) {
        throw 'Close Revit before installing Export. Revit keeps the add-in file locked.'
    }

    $addin = Join-Path $SourceDir 'MetroCIM.addin'
    if (-not (Test-Path -LiteralPath $addin)) {
        throw "MetroCIM.addin was not found in $SourceDir."
    }

    $files = Get-PayloadFiles -Dir $SourceDir
    if (-not ($files | Where-Object { $_.Name -eq 'MetroCIM.dll' })) {
        throw "MetroCIM.dll was not found in $SourceDir."
    }

    $binDir = Join-Path $AddinsDir 'MetroCIM'
    [System.IO.Directory]::CreateDirectory($binDir) | Out-Null
    [System.IO.File]::Copy($addin, (Join-Path $AddinsDir 'MetroCIM.addin'), $true)

    $expected = @{}
    foreach ($file in $files) {
        [System.IO.File]::Copy($file.FullName, (Join-Path $binDir $file.Name), $true)
        $expected[$file.Name] = $true
    }

    foreach ($existing in [System.IO.Directory]::GetFiles($binDir)) {
        $name = [System.IO.Path]::GetFileName($existing)
        if (-not $expected.ContainsKey($name)) {
            [System.IO.File]::Delete($existing)
        }
    }

    Remove-LegacyManifest -AddinsDir $AddinsDir

    if ($ManageArp) {
        $dll = Join-Path $SourceDir 'MetroCIM.dll'
        $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
        if ([string]::IsNullOrWhiteSpace($version)) {
            $version = '1.0.0'
        }

        $bytes = ($files | Measure-Object -Property Length -Sum).Sum
        $sizeKb = [Math]::Max(1, [int][Math]::Ceiling($bytes / 1KB))
        Register-Uninstall -AddinsDir $AddinsDir -Version $version -SizeKb $sizeKb
    }
}

function Uninstall-MetroCim {
    param(
        [Parameter(Mandatory)][string]$AddinsDir,
        [switch]$ManageArp
    )

    if ($ManageArp -and (Test-RevitOpen)) {
        throw 'Close Revit before uninstalling Export. Revit keeps the add-in file locked.'
    }

    $addin = Join-Path $AddinsDir 'MetroCIM.addin'
    $binDir = Join-Path $AddinsDir 'MetroCIM'
    $existed = [System.IO.File]::Exists($addin) -or [System.IO.Directory]::Exists($binDir)
    Remove-DiskPath -Path $addin
    Remove-DiskPath -Path $binDir

    Remove-LegacyManifest -AddinsDir $AddinsDir

    if ($ManageArp) {
        Remove-UninstallRegistration
    }

    return $existed
}

$manageArp = [string]::IsNullOrWhiteSpace($Destination)
if ($manageArp) {
    $target = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
}
else {
    $target = $Destination
}

$target = Expand-LongPath $target
$LocalDir = Expand-LongPath $LocalDir

function Show-ExportDialog {
    param(
        [string]$Title,
        [string]$Message,
        [string[]]$Buttons = @('OK')
    )

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $Title
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
    $form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $form.ShowInTaskbar = $false
    $form.BackColor = [System.Drawing.Color]::White

    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Message
    $label.Font = $form.Font
    $label.ForeColor = [System.Drawing.Color]::Black
    $label.AutoSize = $true
    $label.MaximumSize = New-Object System.Drawing.Size(440, 0)
    $label.Location = New-Object System.Drawing.Point(20, 18)
    $form.Controls.Add($label)

    $buttonWidth = 108
    $buttonHeight = 32
    $bar = New-Object System.Windows.Forms.Panel
    $bar.Dock = [System.Windows.Forms.DockStyle]::Bottom
    $bar.Height = 56
    $bar.BackColor = [System.Drawing.Color]::White
    $x = 480 - 20 - $buttonWidth
    for ($i = $Buttons.Count - 1; $i -ge 0; $i--) {
        $button = New-Object System.Windows.Forms.Button
        $button.Text = $Buttons[$i]
        $button.Font = $form.Font
        $button.Size = New-Object System.Drawing.Size($buttonWidth, $buttonHeight)
        $button.Location = New-Object System.Drawing.Point($x, 12)
        $button.UseVisualStyleBackColor = $true
        $button.Add_Click({
                $form.Tag = $this.Text
                $form.Close()
            }.GetNewClosure())
        if ($i -eq 0) {
            $form.AcceptButton = $button
        }
        if ($i -eq $Buttons.Count - 1) {
            $form.CancelButton = $button
        }
        $bar.Controls.Add($button)
        $x -= $buttonWidth + 8
    }

    $form.Controls.Add($bar)
    $form.Controls.SetChildIndex($bar, 0)
    $form.ClientSize = New-Object System.Drawing.Size(480, ($label.Bottom + 16 + $bar.Height))
    [void]$form.ShowDialog()
    $chosen = [string]$form.Tag
    $form.Dispose()
    return $chosen
}

function Show-Info([string]$Message) {
    if ($Silent) {
        Write-Output $Message
        return
    }

    Show-ExportDialog -Title $ProductName -Message $Message -Buttons @('OK') | Out-Null
}

function Show-Warning([string]$Message) {
    if ($Silent) {
        Write-Error $Message
        return
    }

    Show-ExportDialog -Title $ProductName -Message $Message -Buttons @('OK') | Out-Null
}

if (-not $Silent) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    if (-not ('ExportDpi' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class ExportDpi {
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
}
'@
    }

    [void][ExportDpi]::SetProcessDPIAware()
    [System.Windows.Forms.Application]::EnableVisualStyles()
    [System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
}

if ($Silent -and $Uninstall) {
    $removed = Uninstall-MetroCim -AddinsDir $target -ManageArp:$manageArp
    if ($removed) {
        Write-Output "Removed Export from $target"
    }
    else {
        Write-Output "Export is not installed in $target"
    }
    exit 0
}

if ($Silent) {
    $payload = Get-PayloadDir -Requested $PayloadDir
    Install-MetroCim -SourceDir $payload -AddinsDir $target -ManageArp:$manageArp
    Write-Output "Installed Export to $target"
    exit 0
}

if ($Uninstall) {
    $answer = Show-ExportDialog -Title $ProductName -Message "Remove Export from Revit $RevitVersion?" -Buttons @('Yes', 'No')

    if ($answer -ne 'Yes') {
        exit 0
    }

    try {
        Uninstall-MetroCim -AddinsDir $target -ManageArp:$manageArp | Out-Null
        Show-Info "Export was removed.`r`n`r`nRestart Revit if it was already open."
    }
    catch {
        Show-Warning $_.Exception.Message
        exit 1
    }

    exit 0
}

$payloadRoot = $null
$payloadError = $null
try {
    $payloadRoot = Get-PayloadDir -Requested $PayloadDir
}
catch {
    $payloadError = $_.Exception.Message
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Export Setup'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.BackColor = [System.Drawing.Color]::White

$heading = New-Object System.Windows.Forms.Label
$heading.Text = 'Export'
$heading.Font = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Bold)
$heading.ForeColor = [System.Drawing.Color]::Black
$heading.AutoSize = $true
$heading.Location = New-Object System.Drawing.Point(20, 16)
$form.Controls.Add($heading)

$body = New-Object System.Windows.Forms.Label
$body.Text = "Installs Export glTF, Export XKT, and Export IFC on the Automation tab for the current Windows user."
$body.Font = $form.Font
$body.ForeColor = [System.Drawing.Color]::FromArgb(32, 32, 32)
$body.AutoSize = $true
$body.MaximumSize = New-Object System.Drawing.Size(460, 0)
if ($payloadError) {
    $body.Text = $payloadError
}
$body.Location = New-Object System.Drawing.Point(20, ($heading.Bottom + 12))
$form.Controls.Add($body)

$buttonBar = New-Object System.Windows.Forms.Panel
$buttonBar.Dock = [System.Windows.Forms.DockStyle]::Bottom
$buttonBar.Height = 64
$buttonBar.BackColor = [System.Drawing.Color]::White

$buttonWidth = 108
$buttonHeight = 34
$closeButton = New-Object System.Windows.Forms.Button
$closeButton.Text = 'Close'
$closeButton.Font = $form.Font
$closeButton.Size = New-Object System.Drawing.Size($buttonWidth, $buttonHeight)
$closeButton.Location = New-Object System.Drawing.Point((500 - 20 - $buttonWidth), 14)
$closeButton.UseVisualStyleBackColor = $true
$closeButton.Add_Click({ $form.Close() })
$buttonBar.Controls.Add($closeButton)

$uninstallButton = New-Object System.Windows.Forms.Button
$uninstallButton.Text = 'Uninstall'
$uninstallButton.Font = $form.Font
$uninstallButton.Size = New-Object System.Drawing.Size($buttonWidth, $buttonHeight)
$uninstallButton.Location = New-Object System.Drawing.Point(($closeButton.Left - 8 - $buttonWidth), 14)
$uninstallButton.UseVisualStyleBackColor = $true
$buttonBar.Controls.Add($uninstallButton)

$installButton = New-Object System.Windows.Forms.Button
$installButton.Text = 'Install'
$installButton.Font = $form.Font
$installButton.Size = New-Object System.Drawing.Size($buttonWidth, $buttonHeight)
$installButton.Location = New-Object System.Drawing.Point(($uninstallButton.Left - 8 - $buttonWidth), 14)
$installButton.UseVisualStyleBackColor = $true
$installButton.Enabled = [string]::IsNullOrWhiteSpace($payloadError)
$buttonBar.Controls.Add($installButton)
$form.AcceptButton = $installButton
$form.CancelButton = $closeButton
$form.Controls.Add($buttonBar)
$form.Controls.SetChildIndex($buttonBar, 0)
$form.ClientSize = New-Object System.Drawing.Size(500, ($body.Bottom + 18 + $buttonBar.Height))

$installButton.Add_Click({
        $installButton.Enabled = $false
        $uninstallButton.Enabled = $false
        try {
            Install-MetroCim -SourceDir $payloadRoot -AddinsDir $target -ManageArp:$manageArp
            Show-Info "Export is installed for Revit $RevitVersion.`r`n`r`nRestart Revit, then use the Automation tab.`r`n`r`nExport XKT: Node.js`r`nnpm: xeokit-convert"
            $form.Close()
        }
        catch {
            Show-Warning $_.Exception.Message
        }
        finally {
            $installButton.Enabled = $true
            $uninstallButton.Enabled = $true
        }
    })

$uninstallButton.Add_Click({
        $answer = Show-ExportDialog -Title $ProductName -Message "Remove Export from Revit $RevitVersion?" -Buttons @('Yes', 'No')
        if ($answer -ne 'Yes') {
            return
        }

        $installButton.Enabled = $false
        $uninstallButton.Enabled = $false
        try {
            Uninstall-MetroCim -AddinsDir $target -ManageArp:$manageArp | Out-Null
            Show-Info "Export was removed.`r`n`r`nRestart Revit if it was already open."
            $form.Close()
        }
        catch {
            Show-Warning $_.Exception.Message
        }
        finally {
            $installButton.Enabled = $true
            $uninstallButton.Enabled = $true
        }
    })

[void]$form.ShowDialog()
exit 0
