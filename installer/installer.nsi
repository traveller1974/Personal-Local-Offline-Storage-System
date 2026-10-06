Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!ifndef APP_VERSION
 !define APP_VERSION "1.1.1"
!endif
!ifndef APP_ID
 !define APP_ID "LocalStockManager"
!endif
!ifndef SHORTCUT_LABEL
 !define SHORTCUT_LABEL "本地库存管理"
!endif
Name "本地库存管理"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\LocalStockManager"
InstallDirRegKey HKCU "Software\${APP_ID}" "InstallPath"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "本地库存管理"
VIAddVersionKey /LANG=2052 "FileDescription" "本地库存管理离线安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "LocalStockManager"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
 SetShellVarContext current
 SetRegView 64
 ${IfNot} ${RunningX64}
  MessageBox MB_ICONSTOP "本程序仅支持Windows 11 x64（Intel / AMD）电脑。"
  Abort
 ${EndIf}
 ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
 ${If} $0 < 22000
  MessageBox MB_ICONSTOP "请在Windows 11电脑上安装。"
  Abort
 ${EndIf}
 System::Call 'kernel32::OpenMutexW(i 0x00100000,i 0,w "Local\${APP_ID}") p.r0'
 ${If} $0 != 0
  System::Call 'kernel32::CloseHandle(p r0)'
  MessageBox MB_ICONSTOP "请先关闭本地库存管理，再安装或升级。"
  Abort
 ${EndIf}
FunctionEnd

Section "本地库存管理（必需）" Main
 SectionIn RO
 ; Never overwrite a pre-existing folder belonging to another program.
 IfFileExists "$INSTDIR\*.*" 0 OwnDirectory
 IfFileExists "$INSTDIR\installed.marker" OwnDirectory 0
 MessageBox MB_ICONSTOP "目标目录已包含其他文件，请使用本软件独立的安装目录。"
 Abort
 OwnDirectory:
 SetOutPath "$INSTDIR"
 File /r "${PUBLISH_DIR}\*.*"
 FileOpen $0 "$INSTDIR\installed.marker" w
 FileWrite $0 "LocalStockManager-1b2162c4-34c7-4c1c-b124-550f11084150$\r$\n"
 FileClose $0
 WriteUninstaller "$INSTDIR\Uninstall.exe"
 CreateShortcut "$SMPROGRAMS\${SHORTCUT_LABEL}.lnk" "$INSTDIR\LocalStockManager.exe"
 WriteRegStr HKCU "Software\${APP_ID}" "InstallPath" "$INSTDIR"
 WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayName" "本地库存管理"
 WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "DisplayVersion" "${APP_VERSION}"
 WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "InstallLocation" "$INSTDIR"
 WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
 WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "NoModify" 1
 WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}" "NoRepair" 1
SectionEnd
Section /o "创建桌面快捷方式" Desktop
 CreateShortcut "$DESKTOP\${SHORTCUT_LABEL}.lnk" "$INSTDIR\LocalStockManager.exe"
SectionEnd

Function un.onInit
 SetShellVarContext current
 SetRegView 64
 System::Call 'kernel32::OpenMutexW(i 0x00100000,i 0,w "Local\${APP_ID}") p.r0'
 ${If} $0 != 0
  System::Call 'kernel32::CloseHandle(p r0)'
  MessageBox MB_ICONSTOP "请先关闭本地库存管理，再卸载。"
  Abort
 ${EndIf}
 ${GetRoot} "$INSTDIR" $1
 ${If} $INSTDIR == $1
  Abort
 ${EndIf}
 FileOpen $0 "$INSTDIR\installed.marker" r
 ${If} $0 == ""
  MessageBox MB_ICONSTOP "安装目录标识缺失，已停止卸载。"
  Abort
 ${EndIf}
 FileRead $0 $1
 FileClose $0
 ${If} $1 != "LocalStockManager-1b2162c4-34c7-4c1c-b124-550f11084150$\r$\n"
  MessageBox MB_ICONSTOP "安装目录标识无效，已停止卸载。"
  Abort
 ${EndIf}
FunctionEnd
Section "Uninstall"
 ; This directory is owned by the installer and verified by un.onInit.
 ; Data lives separately in LOCALAPPDATA\LocalStockManager\Data and is retained.
 RMDir /r "$INSTDIR"
 Delete "$SMPROGRAMS\${SHORTCUT_LABEL}.lnk"
 Delete "$DESKTOP\${SHORTCUT_LABEL}.lnk"
 DeleteRegKey HKCU "Software\${APP_ID}"
 DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"
SectionEnd
