; Bootstrapper for the raw per-machine MSI. It removes prior current-user
; ClypDat installs before starting Windows Installer, which cannot upgrade an
; MSI across per-user/per-machine context boundaries itself.
!ifndef CLYPDAT_MSI_FILE
  !define CLYPDAT_MSI_FILE "..\ClypDat.msi"
!endif
!ifndef CLYPDAT_OUTPUT_FILE
  !define CLYPDAT_OUTPUT_FILE "ClypDat-MSI-Setup.exe"
!endif

!include "MUI2.nsh"

Name "ClypDat MSI Setup"
OutFile "${CLYPDAT_OUTPUT_FILE}"
RequestExecutionLevel user
Unicode true

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "License.txt"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Section "ClypDat MSI" SecMain
  SetOutPath "$PLUGINSDIR"
  File /oname=MigrateLegacyInstall.ps1 "MigrateLegacyInstall.ps1"
  File /oname=ClypDat.msi "${CLYPDAT_MSI_FILE}"
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\MigrateLegacyInstall.ps1"'
  Pop $R0
  StrCmp $R0 "0" +3
    MessageBox MB_ICONSTOP "Could not remove the existing per-user ClypDat installation. Close ClypDat, uninstall the previous copy, then run this installer again."
    Abort
  ExecWait '"$SYSDIR\msiexec.exe" /i "$PLUGINSDIR\ClypDat.msi"' $R0
  IntCmp $R0 0 +3
    MessageBox MB_ICONSTOP "ClypDat MSI installation failed with exit code $R0."
    Abort
SectionEnd
