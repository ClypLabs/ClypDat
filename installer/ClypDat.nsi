; ClypDat NSIS installer script.
; Built by the release workflow (.github/workflows/release.yml), which passes:
;   /DCLYPDAT_VERSION=<version>        e.g. 0.1.0
;   /DCLYPDAT_SOURCE_DIR=<path>        the published win-x64-folder to package
;   /DCLYPDAT_OUTPUT_FILE=<path>       output .exe path
; Per-machine install under %ProgramFiles%\ClypDat. Application data remains
; per-user under %LocalAppData%\ClypDat.

!ifndef CLYPDAT_VERSION
  !define CLYPDAT_VERSION "0.0.0"
!endif
!ifndef CLYPDAT_SOURCE_DIR
  !define CLYPDAT_SOURCE_DIR "..\native\publish\win-x64-folder"
!endif
!ifndef CLYPDAT_OUTPUT_FILE
  !define CLYPDAT_OUTPUT_FILE "ClypDat-Setup.exe"
!endif

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

Name "ClypDat"
OutFile "${CLYPDAT_OUTPUT_FILE}"
InstallDir "$PROGRAMFILES64\ClypDat"
InstallDirRegKey HKLM "Software\ClypDat" "InstallDir"
RequestExecutionLevel admin
Unicode true

!define MUI_ABORTWARNING
!define MUI_ICON "..\assets\clypdat-icon.ico"
!define MUI_UNICON "..\assets\clypdat-icon.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "License.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\ClypDat.exe"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Var UpdateProcessId

Function .onInit
  SetRegView 64
  SetShellVarContext all
  ${GetParameters} $R0
  ${GetOptions} "$R0" "/UPDATEPID=" $UpdateProcessId
  ${If} $UpdateProcessId != ""
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Wait-Process -Id $UpdateProcessId -ErrorAction SilentlyContinue"'
  ${EndIf}
FunctionEnd

VIProductVersion "${CLYPDAT_VERSION}.0"
VIAddVersionKey "ProductName" "ClypDat"
VIAddVersionKey "FileVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "ProductVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "FileDescription" "ClypDat Setup"

Section "ClypDat" SecMain
  ; Remove legacy per-user NSIS/MSI installs before registering the new
  ; machine-wide copy. The helper only examines current-user ClypDat entries
  ; and deliberately leaves %LocalAppData%\ClypDat data intact.
  SetOutPath "$PLUGINSDIR"
  File /oname=MigrateLegacyInstall.ps1 "MigrateLegacyInstall.ps1"
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\MigrateLegacyInstall.ps1"'
  Pop $R0
  ${If} $R0 != "0"
    MessageBox MB_ICONSTOP "Could not remove the existing per-user ClypDat installation. Close ClypDat, uninstall the previous copy, then run this installer again."
    Abort
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r "${CLYPDAT_SOURCE_DIR}\*.*"

  WriteRegStr HKLM "Software\ClypDat" "InstallDir" "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\ClypDat"
  CreateShortcut "$SMPROGRAMS\ClypDat\ClypDat.lnk" "$INSTDIR\ClypDat.exe"
  CreateShortcut "$SMPROGRAMS\ClypDat\Uninstall ClypDat.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\ClypDat.lnk" "$INSTDIR\ClypDat.exe"

  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "DisplayName" "ClypDat"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "DisplayIcon" "$INSTDIR\ClypDat.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "DisplayVersion" "${CLYPDAT_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "Publisher" "Stormanzanii"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "NoRepair" 1

  ${If} $UpdateProcessId != ""
    Exec '"$INSTDIR\ClypDat.exe"'
  ${EndIf}
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  RMDir /r "$INSTDIR"
  Delete "$SMPROGRAMS\ClypDat\ClypDat.lnk"
  Delete "$SMPROGRAMS\ClypDat\Uninstall ClypDat.lnk"
  RMDir "$SMPROGRAMS\ClypDat"
  Delete "$DESKTOP\ClypDat.lnk"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat"
  DeleteRegKey HKLM "Software\ClypDat"
SectionEnd
