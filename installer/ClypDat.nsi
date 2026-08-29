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
    ; /UPDATEPID= is interpolated into the -Command string below, and GetOptions
    ; terminates its value at whitespace - which PowerShell does not need. A value
    ; like "1;Start-Process(('calc'))" would run as a second statement, turning this
    ; user-trusted installer into an arbitrary-code launcher for anyone who can
    ; invoke it. ClypDat itself only ever passes Environment.ProcessId.
    ;
    ; IntOp parses the leading integer and stops, so round-tripping the value and
    ; comparing it as a string rejects anything not purely digits: "1;calc" becomes
    ; "1", which no longer matches the original.
    IntOp $R1 $UpdateProcessId + 0
    ${If} "$R1" != "$UpdateProcessId"
      DetailPrint "Ignoring malformed /UPDATEPID value."
    ${ElseIf} $R1 <= 0
      DetailPrint "Ignoring out-of-range /UPDATEPID value."
    ${Else}
      nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Wait-Process -Id $R1 -ErrorAction SilentlyContinue"'
    ${EndIf}
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
VIAddVersionKey "CompanyName" "ClypLabs"
VIAddVersionKey "FileVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "ProductVersion" "${CLYPDAT_VERSION}"
VIAddVersionKey "FileDescription" "ClypDat Setup"

Section "ClypDat" SecMain
  ; Ask a current ClypDat to stop recording and exit before replacing any
  ; files. Older versions have no IPC listener, so the helper waits ten
  ; seconds and then stops only processes it can prove belong to ClypDat.
  ; A remaining verified process means a locked install: abort rather than
  ; copying a partial update over it.
  SetOutPath "$PLUGINSDIR"
  File /oname=ClypDatInstallerShutdown.ps1 "ClypDatInstallerShutdown.ps1"
  ; Use NSIS's built-in ExecWait so the child exit code is captured reliably
  ; on both interactive installs and WinGet's silent /S installs.
  ExecWait '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\ClypDatInstallerShutdown.ps1"' $0
  ${If} $0 != "0"
    ; WinGet runs the installer with /S. A MessageBox in that path blocks its
    ; unattended validation until timeout, so return a failure code instead.
    IfSilent clypdatShutdownSilentFailure
    MessageBox MB_ICONSTOP "ClypDat is still running and could not be stopped. Close ClypDat, then run this installer again."
    Abort
clypdatShutdownSilentFailure:
    SetErrorLevel 1
    Quit
  ${EndIf}

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
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "Publisher" "ClypLabs"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat" "NoRepair" 1

  ${If} $UpdateProcessId != ""
    ; The old UI process has exited before this point, but Windows can still
    ; briefly retain its named single-instance mutex while process teardown
    ; completes. --restart makes the new process retry that narrow handoff
    ; window instead of treating itself as a duplicate and immediately exiting.
    Exec '"$INSTDIR\ClypDat.exe" --restart'
  ${EndIf}
SectionEnd

Section "Uninstall"
  ; $INSTDIR comes from MUI_PAGE_DIRECTORY, so the user can point the install at any
  ; folder - and this recursive delete would then take that whole folder with it.
  ; Installing to C:\ or to Program Files and later uninstalling would have deleted
  ; far more than ClypDat. Only remove a directory that actually looks like ours.
  StrCpy $0 "$INSTDIR" "" -7
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
  ${OrIf} $0 != "ClypDat"
    MessageBox MB_ICONSTOP "Refusing to uninstall from $INSTDIR - it is not a ClypDat install directory. Remove the folder by hand if you are sure."
    Abort
  ${EndIf}
  RMDir /r "$INSTDIR"
  Delete "$SMPROGRAMS\ClypDat\ClypDat.lnk"
  Delete "$SMPROGRAMS\ClypDat\Uninstall ClypDat.lnk"
  RMDir "$SMPROGRAMS\ClypDat"
  Delete "$DESKTOP\ClypDat.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClypDat"
  DeleteRegKey HKCU "Software\ClypDat"
SectionEnd
