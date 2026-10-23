$windowsPlatform = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Get-StardewRuntimeDir {
    if ($windowsPlatform) { return (Join-Path $env:LOCALAPPDATA 'Cortico/Stardew') }
    $dataRoot = if ($env:XDG_DATA_HOME) { $env:XDG_DATA_HOME } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.local/share' }
    return (Join-Path $dataRoot 'Cortico/Stardew')
}

function Get-StardewClientProcesses([string]$ModsDir) {
    if ($windowsPlatform) {
        return (Get-CimInstance Win32_Process -Filter "Name = 'StardewModdingAPI.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($ModsDir) })
    }
    # The official Unix launcher can exec another process. Its environment is
    # inherited, so check the dedicated mods path instead of a process name.
    Get-ChildItem /proc -Directory | Where-Object Name -Match '^\d+$' | ForEach-Object {
        try {
            $processEnv = [IO.File]::ReadAllText((Join-Path $_.FullName 'environ')).Split([char]0)
            $processArgs = [IO.File]::ReadAllText((Join-Path $_.FullName 'cmdline')).Split([char]0)
            if (($processEnv -contains "SMAPI_MODS_PATH=$ModsDir") -or (($processArgs -contains '--mods-path') -and ($processArgs -contains $ModsDir))) { $_.Name }
        } catch { }
    }
}

function Get-StardewLauncher([string]$GameDir) {
    $name = if ($windowsPlatform) { 'StardewModdingAPI.exe' } else { 'StardewValley' }
    $launcher = Join-Path $GameDir $name
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'StardewModdingAPI.dll') -PathType Leaf)) { throw '游戏目录中没有完整的 SMAPI 安装。请先安装对应系统的 SMAPI。' }
    return $launcher
}
