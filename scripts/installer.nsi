; installer.nsi - DiskCleaner Pro 安装程序
; 由 scripts/build-installer.ps1 调用 makensis 编译为 DiskCleanerPro-Setup.exe
;
; 行为：
;   - 以管理员权限运行（RequestExecutionLevel admin），以便安装后钩子能把自签根装入 LocalMachine\Root。
;   - 递归复制 installer-staging 全部内容（publish_fix 文件 + self-sign.ps1 + install-hook.ps1）到 $PROGRAMFILES\DiskCleanerPro。
;   - 安装完成后调用 install-hook.ps1 在本机生成证书 + 装根 + 签名 + 建快捷方式。
;   - 写入标准卸载注册表项，并生成 uninstall.exe。
!include "MUI2.nsh"
!include "FileFunc.nsh"

Name "DiskCleaner Pro"
OutFile "DiskCleanerPro-Setup.exe"
InstallDir "$PROGRAMFILES\DiskCleanerPro"
InstallDirRegKey HKLM "Software\DiskCleanerPro" "InstallDir"
RequestExecutionLevel admin

!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "Main" SecMain
  SetOutPath "$INSTDIR"
  ; 递归复制 staging 目录全部内容（publish_fix 文件 + self-sign.ps1 + install-hook.ps1）
  File /r "installer-staging\*.*"

  ; 写卸载信息
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro" "DisplayName" "DiskCleaner Pro"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro" "DisplayIcon" "$INSTDIR\DiskCleanerPro.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro" "Publisher" "DiskCleanerPro"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro" "NoRepair" 1

  ; 写安装目录到注册表（供升级检测 / InstallDirRegKey）
  WriteRegStr HKLM "Software\DiskCleanerPro" "InstallDir" "$INSTDIR"

  ; 生成卸载程序
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; 安装后在本机配置代码签名（继承管理员令牌运行 PowerShell）
  ExecWait 'powershell -ExecutionPolicy Bypass -NoProfile -File "$INSTDIR\install-hook.ps1" -InstallDir "$INSTDIR"'
SectionEnd

Section "Uninstall"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\DiskCleanerPro"
  DeleteRegKey HKLM "Software\DiskCleanerPro"
  Delete "$SMPROGRAMS\DiskCleaner Pro.lnk"
  Delete "$DESKTOP\DiskCleaner Pro.lnk"
SectionEnd
