# install-hook.ps1
# 由 NSIS 安装程序在安装完成后以管理员身份调用（RequestExecutionLevel admin 继承提权令牌）。
#
# 职责：
#   1. 在本机（目标机器）生成自签代码签名证书——每台机器各自生成自己的，根装入 LocalMachine\Root。
#      这样把 setup.exe 拷到任意新机器，双击安装（一次 UAC）后即完全可用，
#      无需用户手动跑签名脚本，也不降低安全性（恶意程序无该根私钥，无法伪造签名）。
#   2. 对安装目录下的 DiskCleanerPro.exe / DiskCleaner.Elevated.exe 做 Authenticode 签名。
#   3. 建立开始菜单（所有用户）与桌面（当前用户）快捷方式。
#
# 安全性说明：C# 侧 IsAuthenticodeSigned 在未配置 KnownSignerThumbprints 时仅校验"签名链可信"
# （根在 LocalMachine\Root 即通过），不钉死具体指纹。因此目标机器自生成的证书可通过 Elevated 门禁。
param(
  [string]$InstallDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'

Write-Host "DiskCleaner Pro 安装后处理：开始在本机配置代码签名..."

# 1) 清除随包可能带来的"源机器"签名指纹文件，确保最终写入的是本机证书指纹
$st = Join-Path $InstallDir "signing-thumbprint.txt"
if (Test-Path $st) { Remove-Item $st -Force }

# 2) 调用 self-sign.ps1：本机生成证书 + 装入 LocalMachine\Root + 签名两个 exe + 写出本机指纹
$selfSign = Join-Path $InstallDir "self-sign.ps1"
if (-not (Test-Path $selfSign)) { Write-Error "找不到 self-sign.ps1: $selfSign"; exit 1 }
& $selfSign -PublishDir $InstallDir -InstallTrust
if ($LASTEXITCODE -ne 0) { Write-Error "self-sign 失败"; exit 1 }

# 3) 清理导出的 PFX（私钥已嵌入 exe 签名，PFX 留在安装目录有泄露风险，删除之）
$pfx = Join-Path $InstallDir "diskcleaner-selfsigned.pfx"
if (Test-Path $pfx) { Remove-Item $pfx -Force }

# 4) 创建快捷方式（开始菜单-所有用户 + 桌面-当前用户）
try {
  $ws = New-Object -ComObject WScript.Shell
  $exePath = Join-Path $InstallDir "DiskCleanerPro.exe"
  $startMenu = Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) "Programs"
  if (-not (Test-Path $startMenu)) { New-Item -ItemType Directory -Path $startMenu | Out-Null }
  $lnk = Join-Path $startMenu "DiskCleaner Pro.lnk"
  $s = $ws.CreateShortcut($lnk)
  $s.TargetPath = $exePath; $s.WorkingDirectory = $InstallDir; $s.Description = "DiskCleaner Pro - C盘清理工具"
  $s.Save()
  $desktop = [Environment]::GetFolderPath('Desktop')
  $lnk2 = Join-Path $desktop "DiskCleaner Pro.lnk"
  $s2 = $ws.CreateShortcut($lnk2)
  $s2.TargetPath = $exePath; $s2.WorkingDirectory = $InstallDir; $s2.Description = "DiskCleaner Pro - C盘清理工具"
  $s2.Save()
  Write-Host "已创建开始菜单与桌面快捷方式"
} catch {
  Write-Warning "创建快捷方式失败（不影响主程序运行）: $_"
}

Write-Host "安装后处理完成。DiskCleaner Pro 现已就绪，可直接从开始菜单 / 桌面启动。"
