; Inno Setup script for FlaUI.Mcp. Build with: ISCC.exe installer\flaui-mcp.iss
; Expects the published single-file exe at: publish\flaui-mcp.exe (see release CI / Task 11).
#define AppName "FlaUI.Mcp"
#define AppVersion "0.1.0"
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
; Revert agent config (targeted) before files are removed.
Filename: "{app}\{#ExeName}"; Parameters: "uninstall --agent all"; \
  Flags: runhidden waituntilterminated; RunOnceId: "FlauiMcpUnconfigure"

[Code]
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
