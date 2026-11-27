Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 16

!include LogicLib.nsh
!include FileFunc.nsh
!include "${PAYLOAD_INCLUDE}"

!define PRODUCT_ID "LLPlayer-Anki-Polish-144e1786"
!define PRODUCT_NAME "LLPlayer Original Review"
!define REG_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_ID}"
!define SHORTCUT "${PRODUCT_NAME} (144e1786).lnk"
!define UNINSTALLER "Uninstall.exe"

Name "${PRODUCT_NAME}"
OutFile "${SETUP_OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_ID}"
Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles
LoadLanguageFile "${NSISDIR}\Contrib\Language files\English.nlf"
LoadLanguageFile "${NSISDIR}\Contrib\Language files\SimpChinese.nlf"

Var Programs
Var Problem

; The installer and uninstaller apply the same local, non-reparse directory checks.
!macro PathFunctions Prefix
Function ${Prefix}CheckDirectories
  Pop $0
  StrCpy $3 $0 3
  StrCpy $4 $3 2 1
  StrCmp $4 ':\' 0 unsafe
  System::Call 'kernel32::GetDriveTypeW(w r3)i.r3'
  IntCmp $3 3 next unsafe unsafe
next:
  StrCmp $0 '' safe
  System::Call 'kernel32::GetFileAttributesW(w r0)i.r1 ?e'
  Pop $2
  ${If} $1 == -1
    ${If} $2 != 2
    ${AndIf} $2 != 3
      Goto unsafe
    ${EndIf}
  ${Else}
    IntOp $2 $1 & 0x410 ; FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT
    IntCmp $2 0x10 0 unsafe unsafe
  ${EndIf}
  ${GetParent} $0 $0
  ; GetParent returns the drive with no slash for a child of its root.
  StrLen $1 $0
  IntCmp $1 2 safe next next
safe:
  Push 1
  Return
unsafe:
  Push 0
FunctionEnd

Function ${Prefix}PreparePaths
  StrCpy $Problem 'Unsafe install directory.'
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR",i ${NSIS_MAX_STRLEN},w .r0,p 0)i.r1'
  ${If} $1 == 0
  ${OrIf} $1 >= ${NSIS_MAX_STRLEN}
    Goto invalid_paths
  ${EndIf}
  StrCpy $INSTDIR $0
  StrLen $1 $INSTDIR
  IntOp $2 $1 + ${PAYLOAD_MAX_RELATIVE}
  ${If} $1 <= 3
  ${OrIf} $2 > 246
    Goto invalid_paths
  ${EndIf}
  Push $INSTDIR
  Call ${Prefix}CheckDirectories
  Pop $0
  StrCmp $0 1 0 invalid_paths

  ; CSIDL_PROGRAMS, current user, without creating a known-folder directory.
  StrCpy $Problem 'Current-user Start Menu is unavailable.'
  System::Call 'shell32::SHGetFolderPathW(p 0,i 2,p 0,i 0,w .r0)i.r1'
  StrCmp $1 0 0 invalid_paths
  StrCpy $Programs $0
  Push $Programs
  Call ${Prefix}CheckDirectories
  Pop $0
  StrCmp $0 1 0 invalid_paths
  Push 1
  Return
invalid_paths:
  Push 0
FunctionEnd
!macroend

!insertmacro PathFunctions ""
!insertmacro PathFunctions "un."

!macro RequireAbsent Path
  StrCpy $0 "${Path}"
  System::Call 'kernel32::GetFileAttributesW(w r0)i.r1 ?e'
  Pop $2
  ${If} $1 != -1
  ${OrIf} $2 > 3
  ${OrIf} $2 < 2
    StrCpy $Problem 'Refusing to overwrite or use an inaccessible path: $0'
    Goto install_failed
  ${EndIf}
!macroend

!macro CheckPayloadDirectory Prefix Path Failure
  Push "$INSTDIR\${Path}"
  Call ${Prefix}CheckDirectories
  Pop $0
  ${If} $0 != 1
    StrCpy $Problem 'Unsafe payload directory: ${Path}'
    Goto ${Failure}
  ${EndIf}
!macroend

Function .onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd

Section "Program files"
  Call PreparePaths
  Pop $0
  StrCmp $0 1 0 install_failed
  System::Call 'advapi32::RegOpenKeyExW(p 0x80000001,w "${REG_KEY}",i 0,i 0x101,*p .r1)i.r0'
  ${If} $0 == 0
    System::Call 'advapi32::RegCloseKey(p r1)'
  ${EndIf}
  ${If} $0 != 2
    StrCpy $Problem 'This build is already installed for this user, or its registration is inaccessible.'
    Goto install_failed
  ${EndIf}
  !insertmacro RequireAbsent "$Programs\${SHORTCUT}"
  !insertmacro RequireAbsent "$INSTDIR\${UNINSTALLER}"
  !insertmacro PayloadDirectories "" install_failed
  !insertmacro PayloadAbsent

  StrCpy $Problem 'Could not write the program files.'
  ClearErrors
  SetOverwrite off
  !insertmacro PayloadInstall
  SetOutPath "$INSTDIR"
  IfErrors install_failed
  WriteUninstaller "$INSTDIR\${UNINSTALLER}"
  IfErrors install_failed

  StrCpy $Problem 'Could not create the current-user Start Menu shortcut.'
  CreateShortcut "$Programs\${SHORTCUT}" "$INSTDIR\LLPlayer.exe"
  IfErrors install_failed

  StrCpy $Problem 'Could not write the current-user uninstall entry.'
  WriteRegStr HKCU "${REG_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${REG_KEY}" "DisplayVersion" "0.3.0-polish.1"
  WriteRegStr HKCU "${REG_KEY}" "DisplayIcon" "$INSTDIR\LLPlayer.exe,0"
  WriteRegStr HKCU "${REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REG_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}"'
  WriteRegStr HKCU "${REG_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALLER}" /S'
  WriteRegStr HKCU "${REG_KEY}" "StartMenuLink" "$Programs\${SHORTCUT}"
  WriteRegStr HKCU "${REG_KEY}" "PayloadSHA256" "${PAYLOAD_SHA256}"
  WriteRegDWORD HKCU "${REG_KEY}" "EstimatedSize" ${PAYLOAD_KIB}
  WriteRegDWORD HKCU "${REG_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REG_KEY}" "NoRepair" 1
  IfErrors registration_failed
  Goto installed

registration_failed:
  Delete "$Programs\${SHORTCUT}"
  DeleteRegKey HKCU "${REG_KEY}"
install_failed:
  SetErrorLevel 2
  MessageBox MB_OK|MB_ICONSTOP "$Problem" /SD IDOK
  Abort
installed:
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd

Section "Uninstall"
  Call un.PreparePaths
  Pop $0
  StrCmp $0 1 0 uninstall_failed
  StrCpy $Problem 'The registered installation does not match this uninstaller.'
  ReadRegStr $0 HKCU "${REG_KEY}" "InstallLocation"
  StrCmp $0 $INSTDIR 0 uninstall_failed
  ReadRegStr $0 HKCU "${REG_KEY}" "PayloadSHA256"
  StrCmp $0 "${PAYLOAD_SHA256}" 0 uninstall_failed
  ReadRegStr $0 HKCU "${REG_KEY}" "StartMenuLink"
  StrCmp $0 "$Programs\${SHORTCUT}" 0 uninstall_failed
  !insertmacro PayloadDirectories "un." uninstall_failed

  StrCpy $Problem 'Some program files could not be removed. Close the player and retry.'
  SetOutPath "$TEMP"
  ClearErrors
  !insertmacro PayloadUninstall
  IfErrors uninstall_failed
  !insertmacro PayloadRemoveEmptyDirectories
  StrCpy $Problem 'Could not remove the current-user shortcut or uninstall entry.'
  ClearErrors
  Delete "$Programs\${SHORTCUT}"
  IfErrors uninstall_failed
  DeleteRegKey HKCU "${REG_KEY}"
  IfErrors uninstall_failed
  Delete "$INSTDIR\${UNINSTALLER}"
  RMDir "$INSTDIR" ; No /r: user settings and all unlisted files stay in place.
  Goto uninstalled

uninstall_failed:
  SetErrorLevel 3
  MessageBox MB_OK|MB_ICONSTOP "$Problem" /SD IDOK
  Abort
uninstalled:
SectionEnd
