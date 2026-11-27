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
InstallDir "$LOCALAPPDATA\${PRODUCT_ID}"
Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles
LoadLanguageFile "${NSISDIR}\Contrib\Language files\English.nlf"
LoadLanguageFile "${NSISDIR}\Contrib\Language files\SimpChinese.nlf"

Var Programs
Var Problem

Var Stage
Var StageApp
Var StageLink
Var Promoted
Var NewLink
Var NewKey
Var LinkHash
Var UninstallerHash
Var Crypto
Var Buffer
Var Candidate
Var ExpectedHash
Var ActualHash
Var FileMode
Var HeldFile

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

  Push 1
  Return
invalid_paths:
  Push 0
FunctionEnd

Function ${Prefix}GetPrograms
  ; CSIDL_PROGRAMS, current user, without creating a known-folder directory.
  StrCpy $Problem 'Current-user Start Menu is unavailable.'
  System::Call 'shell32::SHGetFolderPathW(p 0,i 2,p 0,i 0,w .r0)i.r1'
  StrCmp $1 0 0 invalid_programs
  StrCpy $Programs $0
  Push $Programs
  Call ${Prefix}CheckDirectories
  Pop $0
  StrCmp $0 1 0 invalid_programs
  Push 1
  Return
invalid_programs:
  Push 0
FunctionEnd
!macroend

!insertmacro PathFunctions ""
!insertmacro PathFunctions "un."

; These functions use only Win32 libraries. Hash the bytes read from the very
; handle used to mark a file for deletion; do not delete a replacement by name.
!macro FileFunctions Prefix
Function ${Prefix}StartCrypto
  StrCpy $HeldFile 0
  System::Call 'advapi32::CryptAcquireContextW(*p .r0,p 0,p 0,i 24,i 0xF0000000)i.r1'
  StrCmp $1 0 crypto_init_failed
  StrCpy $Crypto $0
  System::Alloc 262144
  Pop $Buffer
  StrCmp $Buffer 0 crypto_init_failed
  Push 1
  Return
crypto_init_failed:
  Push 0
FunctionEnd

Function ${Prefix}FinishCrypto
  ${If} $HeldFile != 0
    System::Call 'kernel32::CloseHandle(p $HeldFile)'
    StrCpy $HeldFile 0
  ${EndIf}
  ${If} $Buffer != 0
    System::Free $Buffer
    StrCpy $Buffer 0
  ${EndIf}
  ${If} $Crypto != 0
    System::Call 'advapi32::CryptReleaseContext(p $Crypto,i 0)'
    StrCpy $Crypto 0
  ${EndIf}
FunctionEnd

; 0 = hashed/matching, 1 = absent, 2 = changed or not a plain file, 3 = I/O failure.
; Modes: verify (no deletion), remove (try disposition), hold (defer disposition).
Function ${Prefix}CheckFile
  StrCpy $ActualHash ''
  StrCpy $0 -1
  StrCpy $6 0
  StrCpy $4 3
  System::Call 'kernel32::GetFileAttributesW(w "$Candidate")i.r1 ?e'
  Pop $2
  ${If} $1 == -1
    ${If} $2 == 2
    ${OrIf} $2 == 3
      StrCpy $4 1
    ${EndIf}
    Goto file_done
  ${EndIf}
  IntOp $2 $1 & 0x410
  StrCmp $2 0 0 file_changed
  StrCpy $2 0x80000000
  ${If} $FileMode != 'verify'
    StrCpy $2 0x80010000 ; GENERIC_READ | DELETE
  ${EndIf}
  System::Call 'kernel32::CreateFileW(w "$Candidate",i r2,i 1,p 0,i 3,i 0x08200000,p 0)p.r0'
  StrCmp $0 -1 file_done
  System::Call 'kernel32::GetFileInformationByHandleEx(p r0,i 9,p $Buffer,i 8)i.r1'
  StrCmp $1 0 file_done
  System::Call '*$Buffer(i .r1)'
  IntOp $2 $1 & 0x410
  StrCmp $2 0 0 file_changed
  System::Call 'advapi32::CryptCreateHash(p $Crypto,i 0x800c,p 0,i 0,*p .r6)i.r1'
  StrCmp $1 0 file_done
file_read:
  System::Call 'kernel32::ReadFile(p r0,p $Buffer,i 262144,*i .r3,p 0)i.r1'
  StrCmp $1 0 file_done
  StrCmp $3 0 file_digest
  System::Call 'advapi32::CryptHashData(p r6,p $Buffer,i r3,i 0)i.r1'
  StrCmp $1 0 file_done file_read
file_digest:
  System::Call 'advapi32::CryptGetHashParam(p r6,i 2,p $Buffer,*i 32,i 0)i.r1'
  StrCmp $1 0 file_done
  System::Call 'crypt32::CryptBinaryToStringW(p $Buffer,i 32,i 0x4000000c,w .r5,*i 65)i.r1'
  StrCmp $1 0 file_done
  StrCpy $ActualHash $5
  ${If} $ExpectedHash != ''
    StrCmp $ActualHash $ExpectedHash 0 file_changed
  ${EndIf}
  ${If} $FileMode == 'remove'
    System::Call '*$Buffer(i 1)'
    System::Call 'kernel32::SetFileInformationByHandle(p r0,i 4,p $Buffer,i 1)i.r1'
    StrCmp $1 0 file_done
  ${ElseIf} $FileMode == 'hold'
    StrCpy $HeldFile $0
    StrCpy $0 -1
  ${EndIf}
  StrCpy $4 0
  Goto file_done
file_changed:
  StrCpy $4 2
file_done:
  ${If} $6 != 0
    System::Call 'advapi32::CryptDestroyHash(p r6)'
  ${EndIf}
  ${If} $0 != -1
    System::Call 'kernel32::CloseHandle(p r0)'
  ${EndIf}
  Push $4
FunctionEnd
!macroend

!insertmacro FileFunctions ""
!insertmacro FileFunctions "un."

!macro RequireAbsent Path
  StrCpy $0 "${Path}"
  System::Call 'kernel32::GetFileAttributesW(w r0)i.r1 ?e'
  Pop $2
  ${If} $1 != -1
  ${OrIf} $2 > 3
  ${OrIf} $2 < 2
    StrCpy $Problem 'Choose an unused directory. Refusing to overwrite or use an inaccessible path: $0'
    Goto install_failed
  ${EndIf}
!macroend

!macro CheckPayloadDirectory Prefix Path Failure
  Push "${Path}"
  Call ${Prefix}CheckDirectories
  Pop $0
  ${If} $0 != 1
    StrCpy $Candidate "${Path}"
    Goto ${Failure}
  ${EndIf}
!macroend

!macro CheckPayloadFile Prefix Path Hash Failure
  StrCpy $Candidate "${Path}"
  StrCpy $ExpectedHash "${Hash}"
  Call ${Prefix}CheckFile
  Pop $0
  ${If} $FileMode == 'verify'
    StrCmp $0 0 0 ${Failure}
  ${Else}
    StrCmp $0 3 ${Failure}
  ${EndIf}
!macroend

Function .onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $Promoted 0
  StrCpy $NewLink 0
  StrCpy $NewKey 0
  StrCpy $Crypto 0
  StrCpy $Buffer 0
  StrCpy $HeldFile 0
FunctionEnd

Section "Program files"
  Call PreparePaths
  Pop $0
  StrCmp $0 1 0 install_failed
  Call GetPrograms
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
  !insertmacro RequireAbsent "$INSTDIR"

  ; Stage on the same volume. The final directory is published with a no-replace rename.
  StrCpy $Problem 'Could not create a private staging directory next to the install target.'
  ${GetParent} "$INSTDIR" $R0
  System::Call 'ole32::CoCreateGuid(g .r0)i.r1'
  StrCmp $1 0 0 install_failed
  StrCpy $0 $0 13 1
  StrCpy $1 "$R0\.llpr-$0"
  StrLen $2 $1
  IntOp $3 $2 + ${PAYLOAD_MAX_RELATIVE}
  ${If} $3 > 242
    Goto install_failed
  ${EndIf}
  System::Call 'kernel32::CreateDirectoryW(w r1,p 0)i.r0'
  StrCmp $0 0 install_failed
  StrCpy $Stage $1
  StrCpy $StageApp "$Stage\p"
  StrCpy $StageLink "$Stage\setup.lnk"

  StrCpy $Problem 'Could not write the program files.'
  ClearErrors
  SetOverwrite off
  !insertmacro PayloadInstall
  WriteUninstaller "$StageApp\${UNINSTALLER}"
  IfErrors install_rollback

  StrCpy $Problem 'Could not start SHA-256 verification.'
  Call StartCrypto
  Pop $0
  StrCmp $0 1 0 install_rollback
  StrCpy $FileMode 'verify'
  !insertmacro PayloadCheck '' "$StageApp" install_bad_payload
  StrCpy $Candidate "$StageApp\${UNINSTALLER}"
  StrCpy $ExpectedHash ''
  Call CheckFile
  Pop $0
  StrCmp $0 0 0 install_bad_payload
  StrCpy $UninstallerHash $ActualHash

  StrCpy $Problem 'The final install directory was not empty as expected, or could not be published.'
  SetOutPath "$Stage"
  System::Call 'kernel32::MoveFileExW(w "$StageApp",w "$INSTDIR",i 8)i.r0'
  StrCmp $0 0 install_rollback
  StrCpy $Promoted 1

  StrCpy $Problem 'Could not create the current-user Start Menu shortcut.'
  ClearErrors
  SetOutPath "$INSTDIR"
  IfErrors install_rollback
  CreateShortcut "$StageLink" "$INSTDIR\LLPlayer.exe"
  IfErrors install_rollback
  StrCpy $Candidate $StageLink
  StrCpy $ExpectedHash ''
  Call CheckFile
  Pop $0
  StrCmp $0 0 0 install_rollback
  StrCpy $LinkHash $ActualHash
  System::Call 'kernel32::CopyFileW(w "$StageLink",w "$Programs\${SHORTCUT}",i 1)i.r0'
  StrCmp $0 0 install_rollback
  StrCpy $NewLink 1

  StrCpy $Problem 'Could not write the current-user uninstall entry.'
  System::Call 'advapi32::RegCreateKeyExW(p 0x80000001,w "${REG_KEY}",i 0,p 0,i 0,i 0x2011f,p 0,*p .r1,*i .r2)i.r0'
  StrCmp $0 0 0 install_rollback
  System::Call 'advapi32::RegCloseKey(p r1)'
  StrCmp $2 1 0 install_rollback
  StrCpy $NewKey 1
  ClearErrors
  WriteRegStr HKCU "${REG_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${REG_KEY}" "DisplayVersion" "0.3.0-polish.1"
  WriteRegStr HKCU "${REG_KEY}" "DisplayIcon" "$INSTDIR\LLPlayer.exe,0"
  WriteRegStr HKCU "${REG_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${REG_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}"'
  WriteRegStr HKCU "${REG_KEY}" "UninstallerSHA256" "$UninstallerHash"
  WriteRegStr HKCU "${REG_KEY}" "StartMenuLink" "$Programs\${SHORTCUT}"
  WriteRegStr HKCU "${REG_KEY}" "StartMenuLinkSHA256" "$LinkHash"
  WriteRegStr HKCU "${REG_KEY}" "PayloadSHA256" "${PAYLOAD_SHA256}"
  WriteRegDWORD HKCU "${REG_KEY}" "EstimatedSize" ${PAYLOAD_KIB}
  WriteRegDWORD HKCU "${REG_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${REG_KEY}" "NoRepair" 1
  IfErrors install_rollback
  SetOutPath "$INSTDIR"
  Delete "$StageLink"
  RMDir "$Stage"
  Call FinishCrypto
  SetErrorLevel 0
  Goto installed

install_bad_payload:
  StrCpy $Problem 'The extracted program file did not pass SHA-256 verification: $Candidate'
install_rollback:
  ${If} $NewKey == 1
    System::Call 'advapi32::RegDeleteKeyExW(p 0x80000001,w "${REG_KEY}",i 0x100,i 0)i.r0'
    StrCmp $0 0 0 rollback_incomplete
  ${EndIf}
  ${If} $NewLink == 1
    StrCpy $FileMode 'remove'
    StrCpy $Candidate "$Programs\${SHORTCUT}"
    StrCpy $ExpectedHash $LinkHash
    Call CheckFile
    Pop $0
    StrCmp $0 3 rollback_incomplete
  ${EndIf}
  ${If} $Promoted == 1
    SetOutPath "$Stage"
    System::Call 'kernel32::MoveFileExW(w "$INSTDIR",w "$StageApp",i 8)i.r0'
    StrCmp $0 0 rollback_incomplete
  ${EndIf}
  ; Only named output in the newly reserved staging directory. No recursive deletion.
  !insertmacro PayloadDirectories '' "$StageApp" rollback_incomplete
  SetOutPath "$TEMP"
  ClearErrors
  !insertmacro PayloadDiscardStage
  Delete "$StageApp\${UNINSTALLER}"
  Delete "$StageLink"
  !insertmacro PayloadRemoveEmptyDirectories "$StageApp"
  RMDir "$StageApp"
  RMDir "$Stage"
  IfErrors rollback_incomplete install_failed
rollback_incomplete:
  StrCpy $Problem '$Problem$\r$\nCleanup could not finish. Inspect only this attempt: $INSTDIR ; $Stage'
install_failed:
  Call FinishCrypto
  DetailPrint "$Problem"
  SetErrorLevel 2
  MessageBox MB_OK|MB_ICONSTOP "$Problem" /SD IDOK
  Abort
installed:
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $Crypto 0
  StrCpy $Buffer 0
  StrCpy $HeldFile 0
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
  ReadRegStr $UninstallerHash HKCU "${REG_KEY}" "UninstallerSHA256"
  ReadRegStr $LinkHash HKCU "${REG_KEY}" "StartMenuLinkSHA256"
  StrLen $0 $UninstallerHash
  StrCmp $0 64 0 uninstall_failed
  StrLen $0 $LinkHash
  StrCmp $0 64 0 uninstall_failed

  StrCpy $Problem 'Could not start SHA-256 verification.'
  Call un.StartCrypto
  Pop $0
  StrCmp $0 1 0 uninstall_failed
  SetOutPath "$TEMP"
  StrCpy $FileMode 'remove'
  !insertmacro PayloadDirectories 'un.' "$INSTDIR" uninstall_locked
  !insertmacro PayloadCheck 'un.' "$INSTDIR" uninstall_locked
  !insertmacro PayloadRemoveEmptyDirectories "$INSTDIR"

  ; Opening by the original fingerprint before removing shell integration keeps a locked
  ; uninstaller retryable. If user-replaced, leave it and remove only our registration.
  StrCpy $Candidate "$INSTDIR\${UNINSTALLER}"
  StrCpy $ExpectedHash $UninstallerHash
  StrCpy $FileMode 'hold'
  Call un.CheckFile
  Pop $0
  StrCmp $0 3 uninstall_locked

  ; A redirected Programs folder is not an excuse to delete elsewhere.
  Call un.GetPrograms
  Pop $0
  ${If} $0 == 1
    ReadRegStr $0 HKCU "${REG_KEY}" "StartMenuLink"
    ${If} $0 == "$Programs\${SHORTCUT}"
      StrCpy $Candidate $0
      StrCpy $ExpectedHash $LinkHash
      StrCpy $FileMode 'remove'
      Call un.CheckFile
      Pop $0
      StrCmp $0 3 uninstall_locked
    ${Else}
      DetailPrint 'The saved Start Menu link is outside the current Programs directory; it was left alone.'
    ${EndIf}
  ${EndIf}

  ${If} $HeldFile != 0
    StrCpy $Candidate "$INSTDIR\${UNINSTALLER}"
    System::Call '*$Buffer(i 1)'
    System::Call 'kernel32::SetFileInformationByHandle(p $HeldFile,i 4,p $Buffer,i 1)i.r0'
    StrCmp $0 0 uninstall_locked
  ${EndIf}
  System::Call 'advapi32::RegDeleteKeyExW(p 0x80000001,w "${REG_KEY}",i 0x100,i 0)i.r0'
  ${If} $0 != 0
    StrCpy $Problem 'Could not remove the current-user uninstall entry; retry after resolving access.'
    ${If} $HeldFile != 0
      System::Call '*$Buffer(i 0)'
      System::Call 'kernel32::SetFileInformationByHandle(p $HeldFile,i 4,p $Buffer,i 1)i.r0'
      ${If} $0 == 0
        StrCpy $Problem 'Could not remove the uninstall entry or restore the uninstall file. Use the verified setup evidence for recovery.'
      ${EndIf}
    ${EndIf}
    Goto uninstall_failed
  ${EndIf}
  Call un.FinishCrypto
  RMDir "$INSTDIR" ; Files outside the manifest and modified copies stay in place.
  SetErrorLevel 0
  Goto uninstalled

uninstall_locked:
  StrCpy $Problem 'Could not inspect or remove this program path. Close the player or other handles and retry: $Candidate'
uninstall_failed:
  Call un.FinishCrypto
  DetailPrint "$Problem"
  SetErrorLevel 3
  MessageBox MB_OK|MB_ICONSTOP "$Problem" /SD IDOK
  Abort
uninstalled:
SectionEnd
