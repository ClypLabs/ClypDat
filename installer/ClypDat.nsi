; ClypDat NSIS installer script.
; Built by the release workflow (.github/workflows/release.yml), which passes:
;   /DCLYPDAT_VERSION=<version>        e.g. 0.1.0
;   /DCLYPDAT_SOURCE_DIR=<path>        the published win-x64-folder to package
;   /DCLYPDAT_OUTPUT_FILE=<path>       output .exe path
; Per-user install under %LocalAppData%\Programs\ClypDat. Application data
; remains separately under %LocalAppData%\ClypDat.

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
InstallDir "$LOCALAPPDATA\Programs\ClypDat"
InstallDirRegKey HKCU "Software\ClypDat" "InstallDir"
RequestExecutionLevel user
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
  ${GetParameters} $R0
  ${GetOptions} "$R0" "/UPDATEPID=" $UpdateProcessId
  ${If} $UpdateProcessId != ""
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Wait-Process -Id $UpdateProcessId -ErrorAction SilentlyContinue"'
  ${EndIf}
FunctionEnd

Function RemoveMachineWideInstall
  SetRegView 64
  ReadRegStr $0 HKLM "Software\ClypDat" "InstallDir"
  ${If} $0 == ""
    Return
  ${EndIf}

  StrCpy $1 "$0\Uninstall.exe"
  IfFileExists "$1" machineUninstallerExists machineUninstallerMissing

machineUninstallerExists:
  ExecWait '"$1" /S' $2
  ${If} $2 != "0"
    MessageBox MB_ICONSTOP "Could not remove the existing Program Files ClypDat installation. Complete that uninstall, then run this installer again."
    Abort
  ${EndIf}
  Return

machineUninstallerMissing:
  MessageBox MB_ICONSTOP "Could not find the existing Program Files ClypDat uninstaller. Complete that uninstall, then run this installer again."
  Abort
FunctionEnd

VIProductVersion "${CLYPDAT_VERSION}.0"
VIAddVersionKey "ProductName" "ClypDat"
VIAddVersionKey "FileVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "ProductVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "FileDescription" "ClypDat Setup"

Section "ClypDat" SecMain
  ; 1.1.0 temporarily installed machine-wide. Its elevated uninstaller is
  ; launched here so updates return to the per-user install location without
  ; leaving a duplicate application copy behind.
  Call RemoveMachineWideInstall

  SetOutPath "$INSTDIR"
  File /r "${CLYPDAT_SOURCE_DIR}\*.*"

  WriteRegStr HKCU "Software\ClypDat" "InstallDir" "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\ClypDat"
  CreateShortcut "$SMPROGRAMS\ClypDat\ClypDat.lnk" "$INSTDIR\ClypDat.exe"
  CreateShortcut "$SMPROGRAMS\ClypDat\Uninstall ClypDat.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\ClypDat.lnk" "$INSTDIR\ClypDat.exe"

  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "DisplayName" "ClypDat"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "DisplayIcon" "$INSTDIR\ClypDat.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "DisplayVersion" "${CLYPDAT_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "Publisher" "Stormanzanii"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "NoRepair" 1

  ${If} $UpdateProcessId != ""
    Exec '"$INSTDIR\ClypDat.exe"'
  ${EndIf}
SectionEnd

Section "Uninstall"
  RMDir /r "$INSTDIR"
  Delete "$SMPROGRAMS\ClypDat\ClypDat.lnk"
  Delete "$SMPROGRAMS\ClypDat\Uninstall ClypDat.lnk"
  RMDir "$SMPROGRAMS\ClypDat"
  Delete "$DESKTOP\ClypDat.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat"
  DeleteRegKey HKCU "Software\ClypDat"
SectionEnd
