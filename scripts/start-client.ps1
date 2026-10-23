param([string]$RuntimeDir = (Join-Path $env:LOCALAPPDATA 'Cortico\Stardew'))
$ErrorActionPreference = 'Stop'
$clientPath = Join-Path $RuntimeDir 'client.json'
if (-not (Test-Path -LiteralPath $clientPath)) { throw '请先运行 install.ps1 安装专用客户端。' }
$clientConfig = Get-Content -LiteralPath $clientPath -Raw | ConvertFrom-Json
$existing = Get-CimInstance Win32_Process -Filter "Name = 'StardewModdingAPI.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($clientConfig.modsDir) }
if ($existing) { Write-Host 'bot客户端已经运行。请对她说“用 join 加入我的农场”，不要重复开窗口。'; exit 0 }
$listener = Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $clientConfig.bridgePort -State Listen -ErrorAction SilentlyContinue
if (-not $listener) { Write-Host '提示：Cortico 星露谷 World 尚未监听。先重启并启用扩展；客户端会自动等待重连。' }
Write-Host '你的窗口：合作模式 -> 主持农场，保留一间空闲小屋；游戏选项里的“开始本地合作”是手柄分屏，请取消那个加入提示。'
Write-Host 'bot窗口：停在标题即可。对她说“用 join 加入我的农场”，她会通过 127.0.0.1 选择自己的角色或创建新农场工。'
if ($clientConfig.characterName) { Write-Host "此部署选择的农场工：$($clientConfig.characterName)。已有角色的名字和进度会保留。" }
Write-Host '新角色默认名bot、喜好星露谷、女性、游戏默认外观；已有同名角色会直接复用。她的窗口按 F8 可暂停/恢复。'
# 独立游戏窗口可供用户检查联机状态和使用 F8。
$process = Start-Process -FilePath (Join-Path $clientConfig.gameDir 'StardewModdingAPI.exe') -ArgumentList @('--mods-path', ('"' + $clientConfig.modsDir + '"'), '--no-terminal') -WorkingDirectory $clientConfig.gameDir -WindowStyle Normal -PassThru
Write-Host "bot客户端已启动，进程 ID：$($process.Id)"
