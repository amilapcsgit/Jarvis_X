Unicode true
RequestExecutionLevel user
Name "Jarvis_X"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Jarvis_X"
InstallDirRegKey HKCU "Software\Jarvis_X" "InstallDir"
SetCompressor zlib
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Jarvis.exe"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom DependencyPage
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Section "Jarvis Built-in and local models" SEC_CORE
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*"
  WriteRegStr HKCU "Software\Jarvis_X" "InstallDir" "$INSTDIR"
  CreateDirectory "$SMPROGRAMS\Jarvis_X"
  CreateShortcut "$SMPROGRAMS\Jarvis_X\Jarvis_X.lnk" "$INSTDIR\Jarvis.exe"
SectionEnd

Section /o "Desktop shortcut" SEC_DESKTOP
  CreateShortcut "$DESKTOP\Jarvis_X.lnk" "$INSTDIR\Jarvis.exe"
SectionEnd


Function DependencyPage
  !insertmacro MUI_HEADER_TEXT "Existing dependencies" "Jarvis reuses tools already installed on this PC."
  nsDialogs::Create 1018
  Pop $0
  StrCmp $0 error done
  StrCpy $1 "Ollama Desktop can be configured in Jarvis Settings."
  IfFileExists "$LOCALAPPDATA\Programs\Ollama\ollama app.exe" 0 +2
    StrCpy $1 "Found Ollama Desktop in your user Programs folder."
  ${NSD_CreateLabel} 0 4u 100% 24u "$1"
  Pop $0
  StrCpy $1 "Blender can be configured in Jarvis Settings."
  FindFirst $2 $3 "$PROGRAMFILES64\Blender Foundation\Blender *"
  StrCmp $2 "" no_blender
  IfFileExists "$PROGRAMFILES64\Blender Foundation\$3\blender.exe" 0 close_search
    StrCpy $1 "Found $3 in Program Files."
  close_search:
    FindClose $2
  no_blender:
  ${NSD_CreateLabel} 0 32u 100% 24u "$1"
  Pop $0
  StrCpy $1 "Pinokio can be configured later for ComfyUI."
  IfFileExists "$PROFILE\.pinokio\config.json" 0 +2
    StrCpy $1 "Found a Pinokio user configuration."
  ${NSD_CreateLabel} 0 60u 100% 24u "$1"
  Pop $0
  ${NSD_CreateLabel} 0 88u 100% 30u "Built-in Gemma chat is included and is available independently of these tools."
  Pop $0
  nsDialogs::Show
  done:
FunctionEnd
