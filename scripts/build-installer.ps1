# build-installer.ps1
# 一键构建 DiskCleaner Pro 安装包（setup.exe）：publish + 本机签名 + 准备 staging + NSIS 编译。
#
# 用法（普通 PowerShell 即可，无需管理员；发布产物签名需要管理员，会自动提示）：
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -NoSign   # CI 用：跳过本机签名，由目标机器 install-hook 签名
#
# 依赖：
#   - .NET SDK（优先用户级 C:\Users\15964\.dotnet\dotnet.exe，否则回退 PATH 中的 dotnet）
#   - makensis（NSIS 编译器）。若不在 PATH，脚本自动从 SourceForge 下载 NSIS 便携版到 $env:TEMP 使用。
param(
  [switch]$NoSign
)
$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
$root = Split-Path $scriptDir -Parent          # DiskCleanerPro\
$publish   = Join-Path $root "publish_fix"
$staging   = Join-Path $root "build\installer-staging"
$dotnet    = "C:\Users\15964\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

# 1) 自包含发布
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Write-Host "=== dotnet publish (win-x64 self-contained) ==="
& $dotnet publish (Join-Path $root "src\DiskCleaner\DiskCleaner.csproj") -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

# 2) 本机签名 publish_fix（仅让 setup 内 exe 非完全裸签；目标机器 install-hook 会重签为本机证书）
if (-not $NoSign) {
  Write-Host "=== 本机签名发布产物 ==="
  & (Join-Path $scriptDir "self-sign.ps1") -PublishDir $publish -InstallTrust
  if ($LASTEXITCODE -ne 0) { throw "self-sign 失败" }
}

# 3) 准备 staging 目录（publish_fix 内容 + self-sign.ps1 + install-hook.ps1）
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
Copy-Item (Join-Path $publish "*") $staging -Recurse
Copy-Item (Join-Path $scriptDir "self-sign.ps1") $staging
Copy-Item (Join-Path $scriptDir "install-hook.ps1") $staging
# 删掉源机器指纹文件，强制 install-hook 在目标机器生成本机证书
Remove-Item (Join-Path $staging "signing-thumbprint.txt") -Force -ErrorAction SilentlyContinue

# 4) 获取 makensis（NSIS 编译器）
$makensis = $null
$makensisCmd = Get-Command makensis -ErrorAction SilentlyContinue
if ($makensisCmd) {
  $makensis = $makensisCmd.Source
} else {
  $nsisZip = Join-Path $env:TEMP "nsis.zip"
  $nsisDir = Join-Path $env:TEMP "nsis"
  # nsis-3.11.zip 解压后内部是 nsis-3.11/ 子目录
  $candidate = Join-Path $nsisDir "nsis-3.11\makensis.exe"
  if (-not (Test-Path $candidate)) {
    Write-Host "=== 下载 NSIS 便携版 ==="
    # SourceForge 需要 TLS 1.2+；旧版 PowerShell 默认 TLS 1.0/1.1 会连不上或拿到 HTML 下载页
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $url = "https://downloads.sourceforge.net/project/nsis/NSIS%203/3.11/nsis-3.11.zip"
    Remove-Item $nsisZip -Force -ErrorAction SilentlyContinue
    Remove-Item $nsisDir -Recurse -Force -ErrorAction SilentlyContinue

    $downloaded = $false
    # 优先用系统 curl.exe（Windows 10 1803+/11 自带），对 SourceForge 重定向最稳
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
      Write-Host "使用 curl.exe 下载..."
      & $curl.Source -L -o $nsisZip $url
      if ($LASTEXITCODE -eq 0) { $downloaded = $true }
    }
    # 回退 WebClient
    if (-not $downloaded) {
      Write-Host "使用 WebClient 下载..."
      $wc = New-Object System.Net.WebClient
      $wc.Headers.Add("User-Agent", "curl/8.0.0")
      $wc.DownloadFile($url, $nsisZip)
    }

    $fileInfo = Get-Item $nsisZip
    if ($fileInfo.Length -lt 1MB) {
      $preview = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($nsisZip))
      throw "NSIS ZIP 下载异常（仅 $($fileInfo.Length) 字节），内容似乎是网页：$preview"
    }
    Expand-Archive $nsisZip -DestinationPath $nsisDir -Force
  }
  $makensis = $candidate
}
if (-not (Test-Path $makensis)) { throw "找不到 makensis.exe" }
Write-Host "使用 NSIS: $makensis"

# 5) 编译安装包（在 $root 下运行，使 installer.nsi 中的相对路径 installer-staging 正确解析）
Write-Host "=== 编译安装包 ==="
Push-Location $root
try {
  & $makensis (Join-Path $scriptDir "installer.nsi")
  if ($LASTEXITCODE -ne 0) { throw "NSIS 编译失败" }
} finally {
  Pop-Location
}

$setup = Join-Path $root "DiskCleanerPro-Setup.exe"
if (-not (Test-Path $setup)) { throw "未生成 setup.exe" }
Write-Host ""
Write-Host "安装包已生成: $setup"
Write-Host "分发给新机器后，双击安装（一次 UAC）即自动完成本机签名与配置，无需手动跑脚本。"
