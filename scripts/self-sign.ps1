# self-sign.ps1
# 生成自签名代码签名证书，并一键签名 DiskCleanerPro 发布产物（免费、自用/内部分发）。
#
# 用法：
#   pwsh scripts/self-sign.ps1 -PublishDir ./publish
#   pwsh scripts/self-sign.ps1 -PublishDir ./publish -InstallTrust
#
# 说明：
#   - 用 New-SelfSignedCertificate 生成代码签名证书（EKU = Code Signing）。
#   - 导出 PFX（含私钥）并对 ./publish 下的 DiskCleanerPro.exe / DiskCleaner.Elevated.exe
#     做 Authenticode 签名（Set-AuthenticodeSignature，SHA256 + 可选时间戳）。
#   - 可选 -InstallTrust：将自签根装入 LocalMachine\Root（受信任根），
#     使 DiskCleaner.Elevated.exe 的 IsAuthenticodeSigned 门禁可通过（需管理员运行）。
#   - 无公开信任 CA，未装根的目标机会弹"未知发布者"；本机装根后 Elevated 助手门禁通过。

param(
  [string]$PublishDir = ".\publish",
  [string]$Subject = "CN=DiskCleanerPro Self-Signed",
  [string]$PfxPath = ".\diskcleaner-selfsigned.pfx",
  [string]$TimestampUrl = "http://timestamp.digicert.com",
  [switch]$InstallTrust
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PublishDir)) {
  Write-Error "发布目录不存在: $PublishDir（请先 dotnet publish 产出 exe）"
  exit 1
}

# 1) 生成自签名代码签名证书
Write-Host "生成自签名代码签名证书..."
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
  -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
  -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5)

# 2) 导出 PFX（含私钥）
$pw = [Guid]::NewGuid().ToString()
$secPw = ConvertTo-SecureString $pw -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $secPw -ChainOption EndEntityCertOnly
Write-Host "PFX 已导出: $PfxPath"

# 3) 可选：装入 LocalMachine 受信任根（让 Elevated 助手签名校验通过，需管理员）
if ($InstallTrust) {
  $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if (-not $isAdmin) {
    Write-Error "InstallTrust 需以管理员身份运行 PowerShell（右键'以管理员身份运行'）"
    exit 1
  }
  $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
  $store.Open("ReadWrite"); $store.Add($cert); $store.Close()
  Write-Host "已装入 LocalMachine\Root（受信任根），Elevated 助手签名校验可通过"
}

# 4) 签名两个 exe（时间戳不可达时退化为无时间戳）
foreach ($exe in @('DiskCleanerPro.exe', 'DiskCleaner.Elevated.exe')) {
  $path = Join-Path $PublishDir $exe
  if (-not (Test-Path $path)) { Write-Error "找不到 $path"; exit 1 }
  try {
    Set-AuthenticodeSignature -FilePath $path -Certificate $cert -TimestampServer $TimestampUrl -HashAlgorithm SHA256
  }
  catch {
    Write-Warning "时间戳服务器不可达，退化为无时间戳签名: $_"
    Set-AuthenticodeSignature -FilePath $path -Certificate $cert -HashAlgorithm SHA256
  }
  $sig = Get-AuthenticodeSignature $path
  if ($sig.Status -eq 'Unsigned' -or $sig.Status -eq 'HashMismatch') {
    Write-Error "签名失败: $path -> $($sig.StatusMessage)"; exit 1
  }
  Write-Host "已签名: $path (状态: $($sig.Status))"
}

# 4.5) 写出本证书指纹，供 C# 侧钉死签名者（signing-thumbprint.txt 已在 .gitignore，不入库）
try {
  Set-Content -Path (Join-Path $PublishDir "signing-thumbprint.txt") -Value $cert.Thumbprint -Encoding UTF8
  Write-Host "已写出签名者指纹到 signing-thumbprint.txt（新版 C# 侧据此钉死）: $($cert.Thumbprint)"
} catch {
  Write-Warning "无法写出 signing-thumbprint.txt: $_"
}

# 5) 清理 CurrentUser\My 中的临时证书副本（私钥已在 PFX；若装根则根仍在 LocalMachine）
Remove-Item $cert.PSPath -Force

Write-Host ""
Write-Host "自签名完成。PFX: $PfxPath   密码: $pw"
Write-Host "说明：未装根的目标机会弹'未知发布者'；本机用 -InstallTrust 则 Elevated 助手门禁通过。"
