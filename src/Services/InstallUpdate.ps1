param(
    [Parameter(Mandatory = $true)][string]$ApplicationPath,
    [Parameter(Mandatory = $true)][int]$ParentProcessId,
    [Parameter(Mandatory = $true)][string]$ExpectedHash
)

$ErrorActionPreference = 'Stop'
$updateDirectory = Split-Path -Parent $PSCommandPath
$InstallerPath = Join-Path $updateDirectory 'installer.exe'
$readyPath = Join-Path $updateDirectory 'ready'
$commitPath = Join-Path $updateDirectory 'commit'
$logPath = Join-Path $updateDirectory 'update.log'
$restartApplication = $false
$parentProcess = $null
try {
    $parentProcess = Get-Process -Id $ParentProcessId -ErrorAction Stop
    # The parent keeps running if this helper cannot reach its ready state.
    [System.IO.File]::WriteAllText($readyPath, '')
    if (-not $parentProcess.WaitForExit(1800000)) { return }
    if (-not (Test-Path -LiteralPath $commitPath)) { return }
    $restartApplication = $true

    # Recheck immediately before elevation, after the reader has released its files.
    if ((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $ExpectedHash) {
        throw 'The update installer failed SHA-256 verification.'
    }
    $installDirectory = Split-Path -Parent $ApplicationPath
    $installerArguments = @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/RESTARTEXITCODE=3010',
        ('/DIR="{0}"' -f $installDirectory),
        ('/LOG="{0}"' -f $logPath)
    )
    # This helper stays under the original user. Only the installer is elevated;
    # the relaunched reader must not inherit administrator privileges.
    $installer = Start-Process -FilePath $InstallerPath -ArgumentList $installerArguments `
        -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    try {
        if ($installer.ExitCode -eq 3010) {
            $restartApplication = $false
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show(
                'The update requires a Windows restart to finish. Please restart Windows before opening Fluent PDF.',
                'Fluent PDF update') | Out-Null
        }
        elseif ($installer.ExitCode -ne 0) {
            throw "The installer exited with code $($installer.ExitCode). See $logPath"
        }
    }
    finally { $installer.Dispose() }
}
catch {
    $_.ToString() | Out-File -LiteralPath $logPath -Append -Encoding utf8
    if ($restartApplication) {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            "The update was cancelled or could not be completed. Fluent PDF will reopen.`nLog: $logPath",
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
    if ($restartApplication -and (Test-Path -LiteralPath $ApplicationPath)) {
        Start-Process -FilePath $ApplicationPath -WorkingDirectory (Split-Path -Parent $ApplicationPath)
    }
}
