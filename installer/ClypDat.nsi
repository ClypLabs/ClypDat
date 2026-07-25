; ClypDat NSIS installer script.
; Built by the release workflow (.github/workflows/release.yml), which passes:
;   /DCLYPDAT_VERSION=<version>        e.g. 0.1.0
;   /DCLYPDAT_SOURCE_DIR=<path>        the published win-x64-folder to package
;   /DCLYPDAT_OUTPUT_FILE=<path>       output .exe path
; Per-user install under %LocalAppData%\Programs\ClypDat - no admin/UAC required,
; matching where the app already keeps its own data (%LocalAppData%\ClypDat).

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

VIProductVersion "${CLYPDAT_VERSION}.0"
VIAddVersionKey "ProductName" "ClypDat"
VIAddVersionKey "FileVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "ProductVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "FileDescription" "ClypDat Setup"

Section "ClypDat" SecMain
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
