<#
  diag-explorer.ps1 - DiskCleaner 临时文件清理「黑屏」诊断工具（v3）
  用途：定位清理时桌面/任务栏黑一下，是否由 explorer.exe 崩溃重启引起，并抓取故障模块名。
  设计原则：完全独立，不耦合清理主流程，不修改任何已发布产物，无需重签。

  用法：
    1) 取证模式（黑屏后运行一次即可）：
         powershell -ExecutionPolicy Bypass -File scripts\diag-explorer.ps1
       导出最近 N 条 explorer 相关事件 / 崩溃转储摘要到 %TEMP%\DiskCleaner\diag\explorer-crash.log。

    2) 监控模式（清理前先开着，自动记录）：
         powershell -ExecutionPolicy Bypass -File scripts\diag-explorer.ps1 -Watch
       后台监听 explorer.exe 进程；一旦检测到新 PID 出现，等待 10 秒后抓取多个日志通道落盘。
       Ctrl+C 退出。

  注意：
    - 普通用户可读 Application 日志，但 WER/Operational、System 日志部分通道、WER 报告目录
      需要管理员权限。建议黑屏后用管理员 PowerShell 跑一次取证模式，结果最完整。
#>
param(
  [switch]$Watch,
  [int]$Count = 30,
  [int]$IntervalMs = 1000,
  [int]$WaitSeconds = 10
)

$logDir = Join-Path $env:TEMP 'DiskCleaner\diag'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'explorer-crash.log'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
  IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Add-Log {
  param([string]$Line, [string]$Color = 'White')
  Add-Content -Path $logFile -Value $Line -Encoding UTF8
  Write-Host $Line -ForegroundColor $Color
}

function Get-WerReportModule {
  # 从 WER ReportQueue/Archive 的文本报告里提取关键字段（Fault Module、BucketId 等）
  $werDirs = @(
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue\AppCrash_explorer.exe*'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive\AppCrash_explorer.exe*')
  )
  $result = @()
  foreach ($dir in $werDirs) {
    Get-ChildItem -Path $dir -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 10 | ForEach-Object {
      $report = Get-ChildItem -Path $_.FullName -Filter '*.wer' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
      if (-not $report) { return }
      $content = Get-Content -Path $report.FullName -Raw -ErrorAction SilentlyContinue
      # WER .wer 文件通常用 Sig[...].Name/Value 或 FaultModuleName 两种写法
      $module = if ($content -match 'FaultModuleName\s*=\s*([^\r\n]+)') { $Matches[1].Trim() }
                elseif ($content -match 'Sig\[3\]\.Name\s*=\s*Fault Module Name\s*\r?\nSig\[3\]\.Value\s*=\s*([^\r\n]+)') { $Matches[1].Trim() }
                else { '' }
      $bucket  = if ($content -match 'Response\.LegacyBucketId\s*=\s*([^\r\n]+)') { $Matches[1].Trim() }
                 elseif ($content -match 'Response\.BucketId\s*=\s*([^\r\n]+)') { $Matches[1].Trim() }
                 else { '' }
      $exCode  = if ($content -match 'Sig\[6\]\.Name\s*=\s*Exception Code\s*\r?\nSig\[6\]\.Value\s*=\s*([^\r\n]+)') { $Matches[1].Trim() }
                 elseif ($content -match 'ExceptionCode\s*=\s*([^\r\n]+)') { $Matches[1].Trim() }
                 else { '' }
      $result += [PSCustomObject]@{ Time = $_.LastWriteTime; Report = $report.FullName; FaultModule = $module; BucketId = $bucket; ExceptionCode = $exCode }
    }
  }
  return $result
}

function Get-ExplorerCrashDumps {
  $paths = @(
    (Join-Path $env:LOCALAPPDATA 'CrashDumps\explorer.exe*'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue\AppCrash_explorer.exe*\*.dmp'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive\AppCrash_explorer.exe*\*.dmp')
  )
  foreach ($p in $paths) {
    Get-ChildItem -Path $p -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 50 | ForEach-Object {
      [PSCustomObject]@{
        Path     = $_.FullName
        Time     = $_.LastWriteTime
        Size     = $_.Length
        IsDump   = $_.Extension -eq '.dmp'
      }
    }
  }
}

function Get-ExplorerEvents {
  param([int]$Max)
  $out = @()

  # 1) Application 日志：Application Error 1000、WER 1001
  try {
    $filter = @{ LogName = 'Application'; ID = 1000, 1001 }
    $events = Get-WinEvent -FilterHashtable $filter -MaxEvents 200 -ErrorAction SilentlyContinue
    foreach ($e in $events) {
      $msg = $e.Message
      if ($msg -notmatch 'explorer') { continue }
      $app = $mod = $code = ''
      if ($e.Id -eq 1000 -and $e.ProviderName -eq 'Application Error') {
        # Windows 事件 ID 1000 有结构化 Properties（与语言无关），比正则更可靠：
        # 0=应用名, 1=应用版本, 2=应用时间戳, 3=错误模块名, 4=错误模块版本, 5=错误模块时间戳, 6=异常代码
        $props = $e.Properties
        if ($props.Count -ge 7) {
          $app  = $props[0].Value
          $mod  = $props[3].Value
          $code = $props[6].Value
        }
      }
      # 消息正则兜底（兼容中英文与中文不同叫法：错误模块/故障模块）
      if (-not $app) { $app = if ($msg -match '(?:错误应用程序名称|Faulting application name|错误应用程序):\s*(\S+)') { $Matches[1] } else { '' } }
      if (-not $mod) { $mod = if ($msg -match '(?:错误模块名称|故障模块名称|Faulting module name):\s*(\S+)') { $Matches[1] } else { '(未提取到)' } }
      if (-not $code) { $code = if ($msg -match '(?:异常代码|Exception code|错误代码):\s*(\S+)') { $Matches[1] } else { '' } }
      $out += [PSCustomObject]@{ Time = $e.TimeCreated; Provider = $e.ProviderName; App = $app; FaultModule = $mod; ExceptionCode = $code; Channel = 'Application' }
      if ($out.Count -ge $Max) { return $out }
    }
  } catch { Add-Log "[WARN] 读取 Application 日志失败: $_" 'DarkYellow' }

  # 2) System 日志：Shell 重启 / 关键服务停止
  try {
    $filter = @{ LogName = 'System'; ID = 4001, 4002, 6005, 6006, 6008, 7001, 7031, 7032, 7034 }
    $events = Get-WinEvent -FilterHashtable $filter -MaxEvents 100 -ErrorAction SilentlyContinue
    foreach ($e in $events) {
      $msg = $e.Message
      if ($msg -notmatch 'explorer|Shell|外壳|桌面') { continue }
      $out += [PSCustomObject]@{ Time = $e.TimeCreated; Provider = $e.ProviderName; App = 'explorer/system'; FaultModule = $msg.Substring(0, [Math]::Min(120, $msg.Length)); ExceptionCode = "ID=$($e.Id)"; Channel = 'System' }
      if ($out.Count -ge $Max) { return $out }
    }
  } catch { Add-Log "[WARN] 读取 System 日志失败: $_" 'DarkYellow' }

  # 3) WER Operational 通道（需要管理员）
  try {
    $events = Get-WinEvent -LogName 'Microsoft-Windows-WER/Operational' -MaxEvents 100 -ErrorAction SilentlyContinue |
      Where-Object { $_.Message -match 'explorer' -or $_.Message -match 'crash' -or $_.Message -match 'AppCrash' }
    foreach ($e in $events) {
      $out += [PSCustomObject]@{ Time = $e.TimeCreated; Provider = $e.ProviderName; App = 'explorer'; FaultModule = $e.Message.Substring(0, [Math]::Min(120, $e.Message.Length)); ExceptionCode = "ID=$($e.Id)"; Channel = 'WER/Operational' }
      if ($out.Count -ge $Max) { return $out }
    }
  } catch { Add-Log "[WARN] 读取 WER/Operational 失败（需管理员权限）: $_" 'DarkYellow' }

  # 4) 应用程序兼容性通道（需要管理员）
  try {
    $events = Get-WinEvent -LogName 'Microsoft-Windows-Application-Experience/Program-Compatibility-Assistant' -MaxEvents 50 -ErrorAction SilentlyContinue |
      Where-Object { $_.Message -match 'explorer' }
    foreach ($e in $events) {
      $out += [PSCustomObject]@{ Time = $e.TimeCreated; Provider = $e.ProviderName; App = 'explorer'; FaultModule = '(PCA)'; ExceptionCode = "ID=$($e.Id)"; Channel = 'PCA' }
      if ($out.Count -ge $Max) { return $out }
    }
  } catch { Add-Log "[WARN] 读取 PCA 日志失败（需管理员权限）: $_" 'DarkYellow' }

  return $out
}

function Log-Event {
  param($e)
  $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | EXPLORER事件 | 通道=$($e.Channel) | 时间=$($e.Time) | 来源=$($e.Provider) | 应用=$($e.App) | 故障模块=$($e.FaultModule) | 异常代码=$($e.ExceptionCode)"
  Add-Log $line 'Yellow'
}

function Log-Dumps {
  $dumps = Get-ExplorerCrashDumps
  if ($dumps) {
    Add-Log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | 发现 explorer 崩溃转储（WER/CrashDumps）:" 'Yellow'
    foreach ($d in $dumps) {
      Add-Log "  [$($d.Time)] $($d.Path) ($(($d.Size/1MB).ToString('0.0')) MB)" 'Yellow'
    }
  }
  $werModules = Get-WerReportModule
  if ($werModules) {
    Add-Log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | WER 报告中的故障模块 / Bucket:" 'Yellow'
    foreach ($w in $werModules) {
      Add-Log "  [$($w.Time)] FaultModule=$($w.FaultModule) | ExceptionCode=$($w.ExceptionCode) | BucketId=$($w.BucketId) | $($w.Report)" 'Yellow'
    }
  }
}

# ---- 取证模式（默认）----
if (-not $Watch) {
  Add-Log "== 取证模式：导出最近 $Count 条 explorer 相关事件 / 转储摘要 ==" 'Cyan'
  if (-not $isAdmin) {
    Add-Log "[WARN] 当前不是管理员权限。WER/Operational、WER 报告目录、System 部分日志可能读取为空，建议以管理员重跑。" 'DarkYellow'
  }
  $evts = Get-ExplorerEvents -Max $Count
  Log-Dumps
  if ($evts.Count -eq 0) {
    Add-Log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | 未找到 explorer 相关事件（可能不是崩溃，或无日志/权限不足）" 'Green'
  } else {
    foreach ($e in $evts) { Log-Event $e }
  }
  Add-Log "结果已写入: $logFile" 'Gray'
  exit
}

# ---- 监控模式 ----
Add-Log "== 监控模式：监听 explorer.exe 重启（Ctrl+C 退出）==" 'Cyan'
if (-not $isAdmin) {
  Add-Log "[WARN] 当前不是管理员。捕获到事件后若 WER/Operational 为空，请以管理员重跑一次。" 'DarkYellow'
}
$known = @{ }
Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object { $known[$_.Id] = $true }
Add-Log "已记录当前 explorer PID: $($known.Keys -join ', ')" 'Gray'

while ($true) {
  Start-Sleep -Milliseconds $IntervalMs
  $current = @{ }
  Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object { $current[$_.Id] = $true }

  $hasNew = $current.Keys | Where-Object { -not $known.ContainsKey($_) }
  if ($known.Count -gt 0 -and $hasNew) {
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Log "[$ts] 检测到 explorer.exe 重启！等待 $($WaitSeconds)s 后抓取日志..." 'Red'

    # 第一次等待后抓取；若 Application 为空，再等 10 秒重试一次（事件异步落盘）
    Start-Sleep -Seconds $WaitSeconds
    $evts = Get-ExplorerEvents -Max 5
    if ($evts.Count -eq 0) {
      Add-Log "[$ts] 首次抓取无日志，再等待 10s 让事件落盘..." 'DarkYellow'
      Start-Sleep -Seconds 10
      $evts = Get-ExplorerEvents -Max 5
    }
    Log-Dumps

    $found = $false
    foreach ($e in $evts) {
      if ($e.App -like 'explorer*' -or $e.Provider -match 'explorer' -or $e.Channel -match 'WER|System' -or $e.FaultModule -notlike '(未提取到)') {
        Log-Event $e
        $found = $true
      }
    }
    if (-not $found) {
      Add-Log "[$ts] explorer 重启，但事件日志未找到对应崩溃记录（可能是 Windows 主动刷新 Shell，非崩溃）" 'Yellow'
    }
  }
  $known = $current
}
