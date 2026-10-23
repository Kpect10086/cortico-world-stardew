param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string]$DeploymentDir,
    [string]$RuntimeDir = '',
    [string]$CharacterName = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'platform.ps1')
if (-not $RuntimeDir) { $RuntimeDir = Get-StardewRuntimeDir }
$GameDir = [IO.Path]::GetFullPath($GameDir)
$DeploymentDir = [IO.Path]::GetFullPath($DeploymentDir)
$RuntimeDir = [IO.Path]::GetFullPath($RuntimeDir)
$packageDir = Split-Path -Parent $PSScriptRoot
$running = Get-StardewClientProcesses (Join-Path $RuntimeDir 'Mods')
if ($running) { throw '专用客户端正在运行；请关闭它后安装更新。' }
Get-StardewLauncher $GameDir | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $DeploymentDir 'config.json'))) { throw 'Cortico 部署目录无效。' }
$dll = Join-Path $packageDir 'dist\smapi\CorticoStardew.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    & dotnet build (Join-Path $packageDir 'smapi\CorticoStardew.csproj') -c Release "-p:GameDir=$GameDir" --nologo
    if ($LASTEXITCODE -ne 0) { throw '游戏 Mod 编译失败。' }
    New-Item -ItemType Directory -Force (Split-Path -Parent $dll) | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageDir 'smapi\bin\Release\net6.0\CorticoStardew.dll') -Destination $dll
}
$modsDir = Join-Path $RuntimeDir 'Mods'
$modDir = Join-Path $modsDir 'CorticoStardew'
New-Item -ItemType Directory -Force $modDir | Out-Null
# 客户端继承房主现有 Mod；仅桥接 Mod 使用独立配置。
foreach ($mod in Get-ChildItem -LiteralPath (Join-Path $GameDir 'Mods') -Directory) {
    if ($mod.Name -eq 'CorticoStardew') { continue }
    $target = Join-Path $modsDir $mod.Name
    if (Test-Path -LiteralPath $target) {
        $existing = Get-Item -LiteralPath $target
        if ($existing.LinkType -notin @('Junction', 'SymbolicLink') -or [IO.Path]::GetFullPath($existing.Target) -ne $mod.FullName) { throw "已有不同的 Mod 目录，未覆盖：$target" }
    } else {
        $linkType = if ($windowsPlatform) { 'Junction' } else { 'SymbolicLink' }
        New-Item -ItemType $linkType -Path $target -Target $mod.FullName | Out-Null
    }
}
$settingsPath = Join-Path $DeploymentDir 'config.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
if ($null -eq $settings.worlds) { $settings.worlds = @{} }
if ($settings.worlds -isnot [System.Collections.IDictionary]) { throw '配置 worlds 必须为对象。' }
$config = $settings.worlds.stardew
$configChanged = -not $settings.worlds.ContainsKey('stardew')
if ($null -eq $config) { $config = @{}; $configChanged = $true }
if ($config -isnot [System.Collections.IDictionary]) { throw '配置 worlds.stardew 必须为对象。' }
$defaults = @{ enabled = $true; allowActions = $true; allowShipping = $false; bridgePort = 24983; wakeIntervalSec = 30; characterName = 'bot' }
foreach ($key in $defaults.Keys) {
    if (-not $config.Contains($key)) { $config[$key] = $defaults[$key]; $configChanged = $true }
}
if ($CharacterName) {
    if ($CharacterName.Trim().Length -lt 1 -or $CharacterName.Trim().Length -gt 20 -or $CharacterName -match '[\[\]\x00-\x1f\x7f]') { throw '农场工名字无效。' }
    if ($config.characterName -cne $CharacterName.Trim()) { $config.characterName = $CharacterName.Trim(); $configChanged = $true }
}
if ([int]$config.bridgePort -lt 1024 -or [int]$config.bridgePort -gt 65535) { throw '桥接端口无效。' }
$dataDir = Join-Path $DeploymentDir 'data\stardew'
New-Item -ItemType Directory -Force $dataDir | Out-Null
$credentialsPath = Join-Path $dataDir 'bridge.json'
if (-not (Test-Path -LiteralPath $credentialsPath)) {
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    @{ token = [Convert]::ToHexString($bytes).ToLowerInvariant() } | ConvertTo-Json | Set-Content -LiteralPath $credentialsPath -Encoding utf8
}
$credentials = Get-Content -LiteralPath $credentialsPath -Raw | ConvertFrom-Json
if ($credentials.token.Length -lt 32) { throw '已有桥接凭据无效，未覆盖。' }
Copy-Item -LiteralPath $dll -Destination (Join-Path $modDir 'CorticoStardew.dll')
Copy-Item -LiteralPath (Join-Path $packageDir 'smapi\manifest.json') -Destination (Join-Path $modDir 'manifest.json')
$modConfigPath = Join-Path $modDir 'config.json'
if (Test-Path -LiteralPath $modConfigPath) {
    $oldConfig = Get-Content -LiteralPath $modConfigPath -Raw | ConvertFrom-Json
    if ($oldConfig.Token -ne $credentials.token -or $oldConfig.Port -ne $config.bridgePort) {
        Copy-Item -LiteralPath $modConfigPath -Destination "$modConfigPath.$(Get-Date -Format yyyyMMdd-HHmmss).bak"
    }
}
@{ Port = [int]$config.bridgePort; Token = $credentials.token } | ConvertTo-Json | Set-Content -LiteralPath $modConfigPath -Encoding utf8
if (-not $windowsPlatform) {
    & chmod 700 $dataDir $modDir
    if ($LASTEXITCODE -ne 0) { throw '无法设置桥接凭据目录权限。' }
    & chmod 600 $credentialsPath $modConfigPath
    if ($LASTEXITCODE -ne 0) { throw '无法设置桥接凭据文件权限。' }
}
@{ gameDir = $GameDir; deploymentDir = $DeploymentDir; runtimeDir = $RuntimeDir; modsDir = $modsDir; bridgePort = [int]$config.bridgePort; characterName = $(if($config.characterName) { $config.characterName } else { 'bot' }) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RuntimeDir 'client.json') -Encoding utf8
if ($configChanged) {
    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $dataDir "config.before-stardew-$(Get-Date -Format yyyyMMdd-HHmmss).json")
    $settings.worlds.stardew = $config
    $temporaryPath = "$settingsPath.stardew-tmp"
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
    Move-Item -LiteralPath $temporaryPath -Destination $settingsPath -Force
}
Write-Host "专用游戏 Mod 已安装：$modDir"
Write-Host "Cortico 扩展包目录：$packageDir"
Write-Host '还需在 Cortico 扩展页安装此目录；已运行的 Cortico 需下次重启后加载新扩展。'
