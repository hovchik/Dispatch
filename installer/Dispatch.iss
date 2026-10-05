; Dispatch installer for Windows (Inno Setup 6.3+ or 7).
;
; Build it with build-installer.ps1 next to this file; it publishes the app and the CLI and passes AppVersion,
; PublishDir and CliPublishDir. By hand:
;   ISCC.exe /DAppVersion=1.2.0 /DPublishDir=out\publish\app /DCliPublishDir=out\publish\cli Dispatch.iss
;
; What it does:
;   - installs self-contained builds (no .NET to install) of the desktop app and the `dispatch` CLI
;   - Start menu and optional desktop shortcuts
;   - optional: puts the `dispatch` CLI on PATH (for CI scripts and terminals)
;   - optional: offers Dispatch under "Open with" for .json and .bru files (opens the import preview)
; The user's collections, history and settings (%LOCALAPPDATA%\Dispatch) are never touched, so upgrades and
; uninstalls keep them.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "out\publish\app"
#endif
#ifndef CliPublishDir
  #define CliPublishDir "out\publish\cli"
#endif
#define AppName "Dispatch"
#define AppExe "Dispatch.exe"
; The CLI is dispatch.exe: it lives in its own folder because Windows file names ignore case.
#define CliDir "{app}\cli"

[Setup]
; Keep this id forever: it is how a new version finds and upgrades the installed one.
AppId={{6B0E5C1F-7A0B-4E8F-9C51-3D2A6F4B8E17}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; Per-machine by default; the user can choose "only for me" (no administrator needed) on the first page.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=force
RestartApplications=no
ChangesEnvironment=yes
ChangesAssociations=yes
SetupIconFile=..\src\Dispatch.App\Assets\dispatch.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
ShowLanguageDialog=auto
Compression=lzma2/max
SolidCompression=yes
OutputDir=out
OutputBaseFilename=Dispatch-Setup-{#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "hy"; MessagesFile: "compiler:Languages\Armenian.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.SetupGroup=Command line and files:
en.TaskPath=Add the dispatch command-line tool to PATH
en.TaskAssoc=Show {#AppName} under "Open with" for .json and .bru files (Postman, Insomnia, Bruno, ...)
en.CliPrompt=Dispatch command line

hy.SetupGroup=Հրամանային տող և ֆայլեր՝
hy.TaskPath=Ավելացնել dispatch հրամանային գործիքը PATH-ում
hy.TaskAssoc=Ցույց տալ {#AppName}-ը «Բացել ծրագրով» ցանկում .json և .bru ֆայլերի համար
hy.CliPrompt=Dispatch հրամանային տող

ru.SetupGroup=Командная строка и файлы:
ru.TaskPath=Добавить консольную утилиту dispatch в PATH
ru.TaskAssoc=Показывать {#AppName} в меню «Открыть с помощью» для файлов .json и .bru
ru.CliPrompt=Командная строка Dispatch

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "addtopath"; Description: "{cm:TaskPath}"; GroupDescription: "{cm:SetupGroup}"
Name: "assoc"; Description: "{cm:TaskAssoc}"; GroupDescription: "{cm:SetupGroup}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CliPublishDir}\*"; DestDir: "{#CliDir}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:CliPrompt}"; Filename: "{cmd}"; Parameters: "/k ""set PATH={#CliDir};%PATH% && dispatch --help"""; \
  WorkingDir: "{userdocs}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; File associations go to HKCU for a per-user install and HKLM otherwise (HKA picks the right one).
Root: HKA; Subkey: "Software\Classes\Dispatch.Collection"; ValueType: string; ValueData: "API collection"; Flags: uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Dispatch.Collection\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Dispatch.Collection\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.bru\OpenWithProgids"; ValueType: string; ValueName: "Dispatch.Collection"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.json\OpenWithProgids"; ValueType: string; ValueName: "Dispatch.Collection"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// PATH lives in HKLM for an all-users install and in HKCU for a per-user one.
function PathRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function PathKey: string;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

function PathContains(const Paths, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddCliToPath;
var
  Paths, Dir: string;
begin
  Dir := ExpandConstant('{#CliDir}');
  if not RegQueryStringValue(PathRoot, PathKey, 'Path', Paths) then Paths := '';
  if PathContains(Paths, Dir) then Exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(PathRoot, PathKey, 'Path', Paths + Dir);
end;

procedure RemoveCliFromPath;
var
  Paths, Dir: string;
  P: Integer;
begin
  Dir := ExpandConstant('{#CliDir}');
  if not RegQueryStringValue(PathRoot, PathKey, 'Path', Paths) then Exit;
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then Exit;
  Delete(Paths, P, Length(Dir) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  RegWriteExpandStringValue(PathRoot, PathKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then AddCliToPath else RemoveCliFromPath;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then RemoveCliFromPath;
end;
