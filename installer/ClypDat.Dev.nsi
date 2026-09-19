; Separate per-user Dev channel installer. It installs an immutable initial
; payload under versions\<build-id> and leaves the stable ClypDat product alone.
!ifndef CLYPDAT_VERSION
  !define CLYPDAT_VERSION "0.0.0"
!endif
!ifndef CLYPDAT_SOURCE_DIR
  !define CLYPDAT_SOURCE_DIR "..\native\publish\dev-installer"
!endif
!ifndef CLYPDAT_OUTPUT_FILE
  !define CLYPDAT_OUTPUT_FILE "ClypDat-Dev-Setup.exe"
!endif
!ifndef CLYPDAT_BUILD_ID
  !define CLYPDAT_BUILD_ID "initial"
!endif

!include "MUI2.nsh"
!include "LogicLib.nsh"

Name "ClypDat Dev"
OutFile "${CLYPDAT_OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\ClypDat-Dev"
RequestExecutionLevel user
Unicode true
!define MUI_ICON "..\assets\clypdat-icon.ico"
!define MUI_UNICON "..\assets\clypdat-icon.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "License.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\ClypDat-Dev.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${CLYPDAT_VERSION}.0"
VIAddVersionKey "ProductName" "ClypDat Dev"
VIAddVersionKey "CompanyName" "ClypLabs"
VIAddVersionKey "FileVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "ProductVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "FileDescription" "ClypDat Dev Channel Setup"

Section "ClypDat Dev" SecMain
  SetOutPath "$INSTDIR"
  File "${CLYPDAT_SOURCE_DIR}\ClypDat-Dev.exe"
  File "${CLYPDAT_SOURCE_DIR}\License.txt"
  ; The launcher and updater share this data-root versions directory.
  SetOutPath "$LOCALAPPDATA\ClypDat-Dev\versions\${CLYPDAT_BUILD_ID}"
  File /r "${CLYPDAT_SOURCE_DIR}\versions\${CLYPDAT_BUILD_ID}\*.*"
  SetOutPath "$INSTDIR"
  WriteRegStr HKCU "Software\ClypDat-Dev" "InstallDir" "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\ClypDat Dev"
  CreateShortcut "$SMPROGRAMS\ClypDat Dev\ClypDat Dev.lnk" "$INSTDIR\ClypDat-Dev.exe"
  CreateShortcut "$SMPROGRAMS\ClypDat Dev\Uninstall ClypDat Dev.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\ClypDat Dev.lnk" "$INSTDIR\ClypDat-Dev.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat-Dev" "DisplayName" "ClypDat Dev"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat-Dev" "DisplayIcon" "$INSTDIR\ClypDat-Dev.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat-Dev" "DisplayVersion" "${CLYPDAT_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat-Dev" "Publisher" "ClypLabs"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat-Dev" "UninstallString" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Uninstall"
  ; $INSTDIR comes from MUI_PAGE_DIRECTORY, so the user can point the install at any
  ; folder - and this recursive delete would then take that whole folder with it.
  ; Same guard as ClypDat.nsi: only remove a directory that actually looks like ours.
  StrCpy $0 "$INSTDIR" "" -12
  ${If} $INSTDIR == ""
  ${OrIf} $INSTDIR == "$PROGRAMFILES64"
  ${OrIf} $INSTDIR == "$PROGRAMFILES"
  ${OrIf} $INSTDIR == "$WINDIR"
  ${OrIf} $INSTDIR == "$SYSDIR"
  ${OrIf} $INSTDIR == "$DESKTOP"
  ${OrIf} $INSTDIR == "$DOCUMENTS"
  ${OrIf} $INSTDIR == "$LOCALAPPDATA"
  ${OrIf} $INSTDIR == "$APPDATA"
  ${OrIf} $INSTDIR == "$PROFILE"
  ${OrIf} $0 != "\ClypDat-Dev"
    MessageBox MB_ICONSTOP "Refusing to uninstall from $INSTDIR - it is not a ClypDat Dev install directory. Remove the folder by hand if you are sure."
    Abort
  ${EndIf}
  RMDir /r "$INSTDIR"
  Delete "$SMPROGRAMS\ClypDat Dev\ClypDat Dev.lnk"
  Delete "$SMPROGRAMS\ClypDat Dev\Uninstall ClypDat Dev.lnk"
  RMDir "$SMPROGRAMS\ClypDat Dev"
  Delete "$DESKTOP\ClypDat Dev.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat-Dev"
  DeleteRegKey HKCU "Software\ClypDat-Dev"
SectionEnd
