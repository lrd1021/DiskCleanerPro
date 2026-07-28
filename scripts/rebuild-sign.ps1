# rebuild-sign.ps1
# 一条命令完成：dotnet publish（产出 publish_fix）+ Authenticode 自签名（+ 安装本机受信任根）。
# 目的：消除"改完代码后忘了重发布 / 忘了重签 / 不小心跑了旧 exe"的反复问题。
#        以前是"AI 发布 -> 用户手动签名"两步分离，容易漏；现在是一步到位。
#
# 用法（必须以管理员身份运行 PowerShell，右键"以管理员身份运行"）：
#   cd C:\Users\15964\WorkBuddy\2026-07-21-14-29-30\DiskCleanerPro
#   powershell -ExecutionPolicy Bypass -File scripts\rebuild-sign.ps1
#
#   powershell -ExecutionPolicy Bypass -File scripts\rebuild-sign.ps1 -NoTrust
#       仅签名、不装本机受信任根（Elevated 助手会弹"未知发布者"，主程序仍可点"仍要运行"打开）
#
# 完成后直接运行：publish_fix\DiskCleanerPro.exe

param(
  [string]$Solution    = ".\DiskCleanerPro.sln",
  [string]$PublishDir  = ".\publish_fix",
  [switch]$NoTrust
)

$ErrorActionPreference = 'Stop'

# 1) 定位 .NET 8 SDK（优先用户级托管运行时，因为管理员 PATH 可能只有旧 SDK）
function Test-DotNetVersion($path) {
  try { $ver = & $path --version 2>$null; return ($ver -and $ver.StartsWith('8.')) } catch { return $false }
}
$managed = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
$dotnet = $null
if ((Test-Path $managed) -and (Test-DotNetVersion $managed)) {
  $dotnet = $managed
} elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
  if (Test-DotNetVersion 'dotnet') { $dotnet = 'dotnet' }
}
if (-not $dotnet) {
  Write-Error "找不到 .NET 8 SDK。已检查：`n  PATH dotnet`n  $managed`n请先安装 .NET 8 SDK 或确认路径正确。"
  exit 1
}
Write-Host "使用 dotnet: $dotnet"

# 2) 发布（Release / win-x64 / 自包含 / 无 pdb）
Write-Host "==> [1/2] 发布到 $PublishDir ..."
& $dotnet publish $Solution -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $PublishDir
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish 失败（退出码 $LASTEXITCODE）"; exit 1 }
Write-Host "    发布完成。"

# 3) 自签名（复用 self-sign.ps1；默认装受信任根，Elevated 助手门禁可通过）
Write-Host "==> [2/2] 自签名（InstallTrust=$(if($NoTrust){'false'}else{'true'}), 需本步以管理员运行）..."
$signScript = Join-Path $PSScriptRoot 'self-sign.ps1'
if ($NoTrust) {
  & $signScript -PublishDir $PublishDir
} else {
  & $signScript -PublishDir $PublishDir -InstallTrust
}
if ($LASTEXITCODE -ne 0) { Write-Error "签名失败（退出码 $LASTEXITCODE）"; exit 1 }

Write-Host ""
Write-Host "============================================================"
Write-Host "全部完成。请运行以下 exe（已是最新且已签名）："
Write-Host "    $PublishDir\DiskCleanerPro.exe"
Write-Host "============================================================"