[CmdletBinding()]
param(
    [ValidateSet('Update', 'Rollback', 'Status')]
    [string]$Action = 'Update',

    [string]$SourcePath,

    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\WALL Z'),

    [string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'WALL Z'),

    [string]$AppExeName = 'WALL Z.exe',

    [string]$BackupId,

    [bool]$LaunchAfter = $true,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$BackupRoot = Join-Path $StateRoot 'Backups'
$MetadataFile = Join-Path $StateRoot 'update-state.json'
$ProcessName = [System.IO.Path]::GetFileNameWithoutExtension($AppExeName)
$script:TemporaryPackageRoot = $null

function Write-Section {
    param([string]$Text)
    Write-Host ""
    Write-Host $Text -ForegroundColor Cyan
}

function Confirm-Token {
    param(
        [string]$Message,
        [string]$Token
    )

    if ($Force) {
        return $true
    }

    Write-Host $Message -ForegroundColor Yellow
    $response = Read-Host "Type $Token to continue"
    return $response -eq $Token
}

function Get-FileVersionText {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return 'not installed'
    }

    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        if ($info.ProductVersion) {
            return $info.ProductVersion
        }

        if ($info.FileVersion) {
            return $info.FileVersion
        }
    }
    catch {
        return 'unknown'
    }

    return 'unknown'
}

function Ensure-Directory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Stop-LivePaperLite {
    $running = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    if ($running) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 600
    }

    $stillRunning = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    if ($stillRunning) {
        & taskkill /IM $AppExeName /F | Out-Null
        Start-Sleep -Milliseconds 600
    }
}

function Get-AppRootFromPackage {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Source path not found: $Path"
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer) {
        $directExe = Join-Path $item.FullName $AppExeName
        if (Test-Path -LiteralPath $directExe) {
            return $item.FullName
        }

        $found = Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Filter $AppExeName -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if ($found) {
            return $found.DirectoryName
        }

        throw "Could not find $AppExeName inside folder: $Path"
    }

    if ($item.Extension -ieq '.zip') {
        $tempRoot = Join-Path $env:TEMP ("LivePaperLite-Update-" + [guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $item.FullName -DestinationPath $tempRoot -Force
        $script:TemporaryPackageRoot = $tempRoot
        return Get-AppRootFromPackage -Path $tempRoot
    }

    throw "Unsupported source path type: $Path. Use a folder or .zip package."
}

function Copy-DirectoryMirror {
    param(
        [string]$Source,
        [string]$Destination
    )

    Ensure-Directory -Path $Destination

    $null = & robocopy $Source $Destination /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
    $exitCode = $LASTEXITCODE

    if ($exitCode -ge 8) {
        throw "Robocopy failed while copying from $Source to $Destination (exit code $exitCode)."
    }
}

function Backup-CurrentInstall {
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        return $null
    }

    $items = Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue
    if (-not $items) {
        return $null
    }

    Ensure-Directory -Path $BackupRoot

    $backupName = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $BackupRoot $backupName
    Ensure-Directory -Path $backupPath

    Copy-DirectoryMirror -Source $InstallDir -Destination $backupPath
    return $backupPath
}

function Get-BackupList {
    if (-not (Test-Path -LiteralPath $BackupRoot)) {
        return @()
    }

    return Get-ChildItem -LiteralPath $BackupRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
}

function Get-RollbackSource {
    if ($BackupId) {
        $target = Join-Path $BackupRoot $BackupId
        if (Test-Path -LiteralPath $target) {
            return $target
        }

        throw "Backup not found: $BackupId"
    }

    $latest = Get-BackupList | Select-Object -First 1
    if ($latest) {
        return $latest.FullName
    }

    throw "No rollback backup found under $BackupRoot"
}

function Save-State {
    param(
        [string]$ActionName,
        [string]$Source,
        [string]$Backup
    )

    Ensure-Directory -Path $StateRoot

    $state = [ordered]@{
        action       = $ActionName
        timestamp    = (Get-Date).ToString('o')
        installDir   = $InstallDir
        sourcePath   = $Source
        backupPath   = $Backup
        appExeName   = $AppExeName
        installedVer = Get-FileVersionText -Path (Join-Path $InstallDir $AppExeName)
    }

    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $MetadataFile -Encoding UTF8
}

function Show-Status {
    $exePath = Join-Path $InstallDir $AppExeName
    Write-Section "LivePaper Lite status"
    Write-Host "Install dir : $InstallDir"
    Write-Host "Exe exists  : $(Test-Path -LiteralPath $exePath)"
    Write-Host "Version     : $(Get-FileVersionText -Path $exePath)"
    Write-Host "State root  : $StateRoot"

    $backups = Get-BackupList
    if ($backups.Count -eq 0) {
        Write-Host "Backups     : none"
    }
    else {
        Write-Host "Backups     :"
        foreach ($backup in $backups) {
            Write-Host "  - $($backup.Name)"
        }
    }

    if (Test-Path -LiteralPath $MetadataFile) {
        try {
            $state = Get-Content -LiteralPath $MetadataFile -Raw | ConvertFrom-Json
            Write-Host "Last action : $($state.action) at $($state.timestamp)"
            Write-Host "Last source : $($state.sourcePath)"
            if ($state.backupPath) {
                Write-Host "Last backup : $($state.backupPath)"
            }
        }
        catch {
            Write-Host "Last action : $MetadataFile"
        }
    }
}

function Invoke-Update {
    param([string]$PackagePath)

    try {
        if ([string]::IsNullOrWhiteSpace($PackagePath)) {
            $PackagePath = $PSScriptRoot
        }

        $sourceRoot = Get-AppRootFromPackage -Path $PackagePath

        if ([System.IO.Path]::GetFullPath($sourceRoot).TrimEnd('\') -ieq
            [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\')) {
            throw "Source path points to the current install directory. Use a newer build folder or a zip package."
        }

        $message = @"
This will update LivePaper Lite.

Source:  $sourceRoot
Target:  $InstallDir
Backup:  $BackupRoot

The app will be closed, files will be replaced, and the previous install will be kept as a rollback backup.
"@

        if (-not (Confirm-Token -Message $message -Token 'UPDATE')) {
            Write-Host 'Update cancelled.'
            return
        }

        Stop-LivePaperLite
        $backupPath = Backup-CurrentInstall

        if (Test-Path -LiteralPath $InstallDir) {
            Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
        }

        Ensure-Directory -Path $InstallDir
        Copy-DirectoryMirror -Source $sourceRoot -Destination $InstallDir
        $backupValue = if ($backupPath) { $backupPath } else { '' }
        Save-State -ActionName 'Update' -Source $sourceRoot -Backup $backupValue

        Write-Section 'Update complete'
        Write-Host "Installed version: $(Get-FileVersionText -Path (Join-Path $InstallDir $AppExeName))"
        if ($LaunchAfter) {
            Start-Process -FilePath (Join-Path $InstallDir $AppExeName) -WorkingDirectory $InstallDir -ArgumentList '--startup'
        }
    }
    finally {
        if ($script:TemporaryPackageRoot -and (Test-Path -LiteralPath $script:TemporaryPackageRoot)) {
            Remove-Item -LiteralPath $script:TemporaryPackageRoot -Recurse -Force -ErrorAction SilentlyContinue
            $script:TemporaryPackageRoot = $null
        }
    }
}

function Invoke-Rollback {
    $rollbackSource = Get-RollbackSource

    $message = @"
This will roll LivePaper Lite back to a previous backup.

Backup:  $rollbackSource
Target:  $InstallDir

The current install will be stopped first and the backup will be restored over it.
"@

    if (-not (Confirm-Token -Message $message -Token 'ROLLBACK')) {
        Write-Host 'Rollback cancelled.'
        return
    }

    Stop-LivePaperLite

    $safetyBackup = Backup-CurrentInstall

    if (Test-Path -LiteralPath $InstallDir) {
        Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    }

    Ensure-Directory -Path $InstallDir
    Copy-DirectoryMirror -Source $rollbackSource -Destination $InstallDir
    $rollbackBackupValue = if ($safetyBackup) { $safetyBackup } else { '' }
    Save-State -ActionName 'Rollback' -Source $rollbackSource -Backup $rollbackBackupValue

    Write-Section 'Rollback complete'
    Write-Host "Restored version: $(Get-FileVersionText -Path (Join-Path $InstallDir $AppExeName))"
    if ($LaunchAfter) {
        Start-Process -FilePath (Join-Path $InstallDir $AppExeName) -WorkingDirectory $InstallDir -ArgumentList '--startup'
    }
}

switch ($Action) {
    'Update'   { Invoke-Update -PackagePath $SourcePath }
    'Rollback' { Invoke-Rollback }
    'Status'   { Show-Status }
}
