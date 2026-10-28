; Grid Display — Inno Setup script
;
; Built by tools\release.ps1, or by hand:
;   ISCC /DAppVersion=1.0.0 installer\GridDisplay.iss
;
; This file must stay UTF-8 **with BOM** — Inno reads a BOM-less .iss as ANSI and every
; Korean string in it comes out as mojibake, the same trap as tools\release.ps1.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\publish"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

#define AppName "Grid Display"
#define AppExeName "GridDisplay.exe"
#define AppPublisher "SMIC"
#define AppUrl "https://github.com/MinaryHub/GridDisplay"

[Setup]
; Must never change: it is what marks a later run as an upgrade rather than a second install.
AppId={{AB6CC625-9F64-42F3-8F9E-14384028E047}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user, deliberately. The app updates itself by copying over its own install folder
; (see UpdateInstaller), which a standard user cannot do under Program Files — installing
; there would leave every update blocked behind an admin prompt the wall cannot answer.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\GridDisplay
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#OutDir}
OutputBaseFilename=GridDisplay-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The wizard and the Programs-and-Features entry both show the app's own icon.
SetupIconFile={#SourcePath}\..\packaging\Assets\GridDisplay.ico

; The wall may well be running when an update is installed by hand; let Restart Manager
; close it rather than failing on a locked GridDisplay.exe.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Windows 시작 시 자동으로 실행"; GroupDescription: "추가 옵션:"; Flags: unchecked

[Files]
; The XML files beside the WebView2 assemblies are IntelliSense documentation, never loaded at
; run time. The .pdb is kept: UpdateInstaller writes full exception traces to its log, and
; without symbols those lose their file and line numbers.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*.xml"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "GridDisplay"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup
; Clear a previous install's autostart when the task is turned off on a re-run.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "GridDisplay"; Flags: deletevalue; Check: not WantsStartup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The updater can leave files behind that this installer never wrote.
Type: filesandordirs; Name: "{app}"

[Code]
function WantsStartup(): Boolean;
begin
  Result := WizardIsTaskSelected('startup');
end;

{ .NET 8 Desktop Runtime ships as a versioned folder under the shared framework. Checking for
  the folder rather than the registry keeps working whichever installer put it there. }
function HasDotNet8Desktop(): Boolean;
var
  Base: String;
  Rec: TFindRec;
begin
  Result := False;
  Base := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(Base) then
    Exit;

  if FindFirst(Base + '\*', Rec) then
  try
    repeat
      if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        if Copy(Rec.Name, 1, 2) = '8.' then
        begin
          Result := True;
          Exit;
        end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

{ WebView2 registers its version under EdgeUpdate. Windows 11 has it in the box; Windows 10
  often does not, and without it every browser cell comes up empty. }
function WebView2Present(Root: Integer; Key: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Version)
    and (Version <> '') and (Version <> '0.0.0.0');
end;

// Braces are written singly here. [Code] strings are not run through constant expansion, so
// the doubling used in the sections above would put two literal braces in the key name.
function HasWebView2(): Boolean;
var
  ClientKey, ClientKey32: String;
begin
  ClientKey := 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  ClientKey32 := 'Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

  Result := WebView2Present(HKEY_LOCAL_MACHINE, ClientKey32)
    or WebView2Present(HKEY_LOCAL_MACHINE, ClientKey)
    or WebView2Present(HKEY_CURRENT_USER, ClientKey);
end;

{ Warn, never block. A missing prerequisite is worth saying out loud, but refusing to install
  would only strand someone who is about to install it anyway. }
function InitializeSetup(): Boolean;
var
  Missing: String;
begin
  Missing := '';

  if not HasDotNet8Desktop() then
    Missing := Missing + '  · .NET 8 Desktop Runtime (x64)' + #13#10 +
               '      https://dotnet.microsoft.com/download/dotnet/8.0' + #13#10#13#10;

  if not HasWebView2() then
    Missing := Missing + '  · Microsoft Edge WebView2 런타임 — 브라우저 칸에 필요합니다' + #13#10 +
               '      https://developer.microsoft.com/microsoft-edge/webview2/' + #13#10#13#10;

  if Missing = '' then
  begin
    Result := True;
    Exit;
  end;

  Result := MsgBox(
    '이 프로그램을 실행하려면 다음이 필요한데, 찾지 못했습니다.' + #13#10#13#10 +
    Missing +
    '지금 설치를 계속하고 위 항목은 나중에 설치해도 됩니다.' + #13#10 +
    '계속할까요?',
    mbConfirmation, MB_YESNO) = IDYES;
end;

{ Settings live outside the install folder, so removing the program leaves them behind unless
  the user says otherwise. Ask rather than assume: a reinstall onto the same wall usually wants
  the sets kept. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Roaming, Local: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  { A silent uninstall must never stop on a prompt nobody is there to answer. }
  if UninstallSilent then
    Exit;

  Roaming := ExpandConstant('{userappdata}\GridDisplay');
  Local := ExpandConstant('{localappdata}\GridDisplay');
  if not (DirExists(Roaming) or DirExists(Local)) then
    Exit;

  if MsgBox('세트 구성과 브라우저 로그인 정보도 함께 지울까요?' + #13#10#13#10 +
            '"아니요"를 고르면 다시 설치할 때 그대로 복원됩니다.',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    DelTree(Roaming, True, True, True);
    DelTree(Local, True, True, True);
  end;
end;
