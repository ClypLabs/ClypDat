; EVE NSIS installer script.
; Built by the release workflow (.github/workflows/release.yml), which passes:
;   /DEVE_VERSION=<version>        e.g. 0.1.0
;   /DEVE_SOURCE_DIR=<path>        the published win-x64-folder to package
;   /DEVE_OUTPUT_FILE=<path>       output .exe path
; Per-user install under %LocalAppData%\Programs\EVE - no admin/UAC required,
; matching where the app already keeps its own data (%LocalAppData%\EVE).

!ifndef EVE_VERSION
  !define EVE_VERSION "0.0.0"
!endif
!ifndef EVE_SOURCE_DIR
  !define EVE_SOURCE_DIR "..\native\publish\win-x64-folder"
!endif
!ifndef EVE_OUTPUT_FILE
  !define EVE_OUTPUT_FILE "EVE-Setup.exe"
!endif

!include "MUI2.nsh"

Name "EVE"
OutFile "${EVE_OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\EVE"
InstallDirRegKey HKCU "Software\EVE" "InstallDir"
RequestExecutionLevel user
Unicode true

!define MUI_ABORTWARNING
!define MUI_ICON "..\assets\eve-icon.ico"
!define MUI_UNICON "..\assets\eve-icon.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\EVE.exe"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${EVE_VERSION}.0"
VIAddVersionKey "ProductName" "EVE"
VIAddVersionKey "FileVersion" "${EVE_VERSION}"
VIAddVersionKey "ProductVersion" "${EVE_VERSION}"
VIAddVersionKey "FileDescription" "EVE Setup"

Section "EVE" SecMain
  SetOutPath "$INSTDIR"
  File /r "${EVE_SOURCE_DIR}\*.*"

  WriteRegStr HKCU "Software\EVE" "InstallDir" "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\EVE"
  CreateShortcut "$SMPROGRAMS\EVE\EVE.lnk" "$INSTDIR\EVE.exe"
  CreateShortcut "$SMPROGRAMS\EVE\Uninstall EVE.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\EVE.lnk" "$INSTDIR\EVE.exe"

  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "DisplayName" "EVE"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "DisplayIcon" "$INSTDIR\EVE.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "DisplayVersion" "${EVE_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "Publisher" "Stormanzanii"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE" "NoRepair" 1
SectionEnd

Section "Uninstall"
  RMDir /r "$INSTDIR"
  Delete "$SMPROGRAMS\EVE\EVE.lnk"
  Delete "$SMPROGRAMS\EVE\Uninstall EVE.lnk"
  RMDir "$SMPROGRAMS\EVE"
  Delete "$DESKTOP\EVE.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EVE"
  DeleteRegKey HKCU "Software\EVE"
SectionEnd
