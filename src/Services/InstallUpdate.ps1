param(
    [Parameter(Mandatory = $true)][int]$ParentProcessId,
    [Parameter(Mandatory = $true)][string]$ExpectedHash
)

$ErrorActionPreference = 'Stop'
$updateDirectory = Split-Path -Parent $PSCommandPath
$InstallerPath = Join-Path $updateDirectory 'installer.exe'
$readyPath = Join-Path $updateDirectory 'ready'
$commitPath = Join-Path $updateDirectory 'commit'
$logPath = Join-Path $updateDirectory 'update.log'
$updateWasCommitted = $false
$parentProcess = $null

function Get-InstalledApplicationPath {
    # Keep the AppId in sync with installer/PDFFluent.iss. The current installer
    # uses administrative, 64-bit install mode, so read the matching registry view.
    $uninstallSubKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C5925B07-56D3-41C5-B506-DE3F00F7CC02}_is1'
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    $installation = $null
    try {
        $installation = $registry.OpenSubKey($uninstallSubKey)
        if ($null -eq $installation) {
            throw 'The Fluent PDF installation record could not be found.'
        }
        $directory = [string]$installation.GetValue('InstallLocation')
        if ([string]::IsNullOrWhiteSpace($directory) -or -not [System.IO.Path]::IsPathRooted($directory)) {
            throw 'The Fluent PDF installation directory is invalid.'
        }
        $executable = Join-Path $directory 'pdf_fluent.exe'
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw 'The installed Fluent PDF executable could not be found.'
        }
        return $executable
    }
    finally {
        if ($null -ne $installation) { $installation.Dispose() }
        $registry.Dispose()
    }
}

try {
    $parentProcess = Get-Process -Id $ParentProcessId -ErrorAction Stop
    # The parent keeps running if this helper cannot reach its ready state.
    [System.IO.File]::WriteAllText($readyPath, '')
    if (-not $parentProcess.WaitForExit(1800000)) { return }
    if (-not (Test-Path -LiteralPath $commitPath)) { return }
    $updateWasCommitted = $true

    # Recheck immediately before elevation, after the reader has released its files.
    if ((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $ExpectedHash) {
        throw 'The update installer failed SHA-256 verification.'
    }
    $installerArguments = @(
        '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/RESTARTEXITCODE=3010',
        ('/LOG="{0}"' -f $logPath)
    )
    # Do not pass /DIR: Inno Setup resolves the previous installation itself.
    # /SILENT skips the wizard but shows Inno's progress window. Keep the
    # installer window visible; only the PowerShell helper runs hidden.
    # This helper stays under the original user. Only the installer is elevated;
    # the relaunched reader must not inherit administrator privileges.
    $installer = Start-Process -FilePath $InstallerPath -ArgumentList $installerArguments `
        -Verb RunAs -WindowStyle Normal -Wait -PassThru
    try {
        if ($installer.ExitCode -eq 3010) {
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show(
                'The update requires a Windows restart to finish. Please restart Windows before opening Fluent PDF.',
                'Fluent PDF update') | Out-Null
            return
        }
        elseif ($installer.ExitCode -ne 0) {
            throw "The installer exited with code $($installer.ExitCode). See $logPath"
        }
    }
    finally { $installer.Dispose() }

    # Read the successful install's actual location instead of relaunching the
    # caller, which may have been a Debug build or a portable copy.
    $applicationToRestart = Get-InstalledApplicationPath
    Start-Process -FilePath $applicationToRestart `
        -WorkingDirectory (Split-Path -Parent $applicationToRestart) -WindowStyle Normal
}
catch {
    $_.ToString() | Out-File -LiteralPath $logPath -Append -Encoding utf8
    if ($updateWasCommitted) {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            "The update was cancelled, failed, or Fluent PDF could not be reopened. Please open Fluent PDF manually.`nLog: $logPath",
            'Fluent PDF update') | Out-Null
    }
}
finally {
    if ($null -ne $parentProcess) { $parentProcess.Dispose() }
    # Inno Setup has exited before cleanup. Retry briefly for antivirus/file locks.
    foreach ($filePath in @($InstallerPath, $readyPath, $commitPath, $PSCommandPath)) {
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try {
                if (Test-Path -LiteralPath $filePath) {
                    Remove-Item -LiteralPath $filePath -Force -ErrorAction Stop
                }
                break
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
    }
}
