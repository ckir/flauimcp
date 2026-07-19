; Inno Setup script for FlaUI.Mcp. Build with: ISCC.exe installer\flaui-mcp.iss
; Expects the published single-file exe at: publish\flaui-mcp.exe (see release CI / Task 11).
#define AppName "FlaUI.Mcp"
#define AppVersion "0.17.1"
#define ExeName "flaui-mcp.exe"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=FlaUI.Mcp
DefaultDirName={localappdata}\Programs\FlaUI.Mcp
DefaultGroupName=FlaUI.Mcp
PrivilegesRequired=lowest
OutputBaseFilename=flaui-mcp-setup
; OutputDir is relative to THIS script's dir (installer/), so ..\dist = repo-root dist/
; — where release.yml's Checksums + Create-Release steps expect flaui-mcp-setup.exe.
OutputDir=..\dist
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes

[Files]
Source: "..\publish\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "addtopath"; Description: "Add FlaUI.Mcp to PATH"; Flags: checkedonce

[Registry]
; Optional PATH addition (per-user) when the task is selected.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Tasks: addtopath; \
  Check: NeedsAddPath('{app}')

[Run]
; Configure every detected agent right after files are placed.
Filename: "{app}\{#ExeName}"; Parameters: "install --agent all"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Configuring agents..."

[UninstallRun]
; Revert agent config (targeted) + sweep our backups. Two variants chosen by the "remove
; configuration?" prompt (InitializeUninstall): --purge-data also deletes the ~/.flaui-mcp data dir.
Filename: "{app}\{#ExeName}"; Parameters: "uninstall --agent all --purge-data"; \
  Flags: runhidden waituntilterminated; RunOnceId: "FlauiMcpUnconfigurePurge"; Check: ShouldPurge
Filename: "{app}\{#ExeName}"; Parameters: "uninstall --agent all"; \
  Flags: runhidden waituntilterminated; RunOnceId: "FlauiMcpUnconfigureKeep"; Check: ShouldKeep

[Code]
var
  RemoveConfig: Boolean;

function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Param + ';', ';' + OrigPath + ';') = 0;
end;

procedure StopRunningInstance();
var
  ResultCode: Integer;
begin
  // Stop a running server so the locked exe can be replaced (round-2: update file-lock).
  Exec('taskkill.exe', '/F /IM {#ExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningInstance();
  Result := '';
end;

// --- Uninstall: ask whether to also remove user config/backups, and clean the PATH entry. ---

function InitializeUninstall(): Boolean;
begin
  // Default button is No (MB_DEFBUTTON2) so a /VERYSILENT uninstall keeps the user's config.
  RemoveConfig := MsgBox(
    'Also remove FlaUI.Mcp''s configuration and backups (the .flaui-mcp folder in your user profile)?' + #13#10 +
    'Choose No to keep your settings for a future reinstall.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

function ShouldPurge(): Boolean;
begin
  Result := RemoveConfig;
end;

function ShouldKeep(): Boolean;
begin
  Result := not RemoveConfig;
end;

procedure RemoveFromUserPath(const Dir: string);
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    exit;
  StringChangeEx(Path, ';' + Dir, '', True);
  StringChangeEx(Path, Dir + ';', '', True);
  StringChangeEx(Path, Dir, '', True);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
end;

// The CLI wrote any restore warnings here and is about to be deleted along with `flaui-mcp status`,
// the only thing that could have read them. We are the last actor standing, so we report them.
function StateDir(): string;
begin
  Result := ExpandConstant('{localappdata}\FlaUI.Mcp\state');
end;

procedure ShowUninstallWarnings();
var
  LogPath: string;
  Contents: AnsiString;
begin
  LogPath := StateDir() + '\uninstall-warnings.log';
  if not FileExists(LogPath) then
    exit;

  // A silent uninstall suppresses MsgBox and auto-answers the default (this script already relies
  // on that at InitializeUninstall). Deleting the log here would therefore destroy the warning
  // having shown it to nobody. Keep the evidence instead: there is no human to honor a purge
  // prompt for either, because nobody answered one.
  if UninstallSilent then
    exit;

  // If we cannot READ it we must not DELETE it: reaping here would destroy the evidence having
  // shown it to nobody, which is the very failure this procedure exists to prevent. Leave it and
  // point at it instead — a file the user can still open beats a file we silently ate.
  if not LoadStringFromFile(LogPath, Contents) then
  begin
    MsgBox('FlaUI.Mcp was removed, but some cleanup did not complete and the details could not be read.'
           + #13#10#13#10 + 'They were left for you at:' + #13#10 + LogPath, mbInformation, MB_OK);
    exit;
  end;

  MsgBox('FlaUI.Mcp was removed, but some cleanup did not complete:' + #13#10#13#10 +
         String(Contents), mbInformation, MB_OK);

  // Reaped ONLY on the path where a human has actually seen the contents — this honors the
  // "remove my configuration" request without silently swallowing the reason they may need.
  DeleteFile(LogPath);
  RemoveDir(StateDir());
  RemoveDir(ExpandConstant('{localappdata}\FlaUI.Mcp'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromUserPath(ExpandConstant('{app}'));
    ShowUninstallWarnings();
  end;
end;
