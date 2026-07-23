<#
  sign-binaries.ps1 — 对 DiskCleanerPro 发布产物做带时间戳 Authenticode 签名

  用途：分发前对 DiskCleanerPro.exe 与 DiskCleaner.Elevated.exe 完成 Authenticode 签名
        （R9/A1 GA 前置。签名须使用购买的代码签名证书，沙箱无法代签）。

  前置：
    - Windows + PowerShell 5.1/7（Set-AuthenticodeSignature 为内置 cmdlet，无需 signtool）
    - 有效的代码签名证书（PFX 格式）

  用法（PowerShell）：
    # 1) 准备 PFX 的 base64（一次性）
    $pfxB64 = [Convert]::ToBase64String((Get-Content .\mycert.pfx -Raw -AsByteStream))
    # 2) 设置环境变量
    $env:CODESIGN_PFX_BASE64 = $pfxB64
    $env:CODESIGN_PASSWORD    = 'your-pfx-password'
    # 3) 运行
    .\scripts\sign-binaries.ps1 -PublishDir .\publish

  或在 CI 中由 build-and-smoke.yml 的 "Sign binaries" 步骤调用同一逻辑。
#>
param(
  [string]$PublishDir = ".\publish",
  [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PublishDir)) {
  Write-Error "发布目录不存在: $PublishDir"
  exit 1
}

$pfxB64 = $env:CODESIGN_PFX_BASE64
if (-not $pfxB64) {
  Write-Error "环境变量 CODESIGN_PFX_BASE64 未设置（应为 PFX 文件的 base64 字符串）"
  exit 1
}

$pw = $env:CODESIGN_PASSWORD
if (-not $pw) {
  Write-Warning "CODESIGN_PASSWORD 未设置，假定 PFX 无密码"
}

# 将 base64 证书写出到临时文件
$tmpPfx = Join-Path $env:TEMP "dc_codesign_$(Get-Random).pfx"
[IO.File]::WriteAllBytes($tmpPfx, [Convert]::FromBase64String($pfxB64))

try {
  if ($pw) {
    $secPw = ConvertTo-SecureString $pw -AsPlainText -Force
    $cert  = Get-PfxCertificate -FilePath $tmpPfx -Password $secPw
  } else {
    $cert  = Get-PfxCertificate -FilePath $tmpPfx
  }

  foreach ($exe in @('DiskCleanerPro.exe', 'DiskCleaner.Elevated.exe')) {
    $path = Join-Path $PublishDir $exe
    if (-not (Test-Path $path)) {
      Write-Error "找不到待签名文件: $path"
      exit 1
    }
    Set-AuthenticodeSignature -FilePath $path `
      -Certificate $cert `
      -TimestampServer $TimestampUrl `
      -HashAlgorithm SHA256 | Out-Null

    $sig = Get-AuthenticodeSignature $path
    if ($sig.Status -ne 'Valid') {
      Write-Error "签名失败: $path -> $($sig.StatusMessage)"
      exit 1
    }
    Write-Host "已签名: $path (时间戳服务: $TimestampUrl)"
  }

  Write-Host "Authenticode 签名完成 ✅"
}
finally {
  if (Test-Path $tmpPfx) { Remove-Item $tmpPfx -Force }
}
