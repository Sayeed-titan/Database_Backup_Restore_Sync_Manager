; PgBackupManager installer script (Inno Setup 6)
;
; Build steps:
;   Just run  .\release.ps1  (or release.cmd) from the repo root — it tests, bumps the
;   version, publishes and runs ISCC into installer\dist\<version>\.
;   Manual: dotnet publish ... -p:PublishProfile=win-x64, then ISCC installer\PgBackupManager.iss
;
; Produces a single self-contained setup.exe under installer\dist\ — the
; target machine needs nothing pre-installed (.NET is bundled). PostgreSQL
; client tools (pg_dump/pg_restore/psql) are NOT bundled by design — the
; recipient points Settings -> PG Bin Folder Override at their own install
; after first launch. See before-install.txt, shown on the welcome page.

#define MyAppName "PgBackupManager"
; release.ps1 passes /DMyAppVersion=x.y.z (the csproj <Version> is the source of truth);
; this default only applies when compiling the script by hand.
#ifndef MyAppVersion
  #define MyAppVersion "3.0.0"
#endif
#define MyAppPublisher "Mediklaud"
#define MyAppExeName "PgBackupManager.UI.exe"
#define PublishDir "..\PgBackupManager.UI\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{53DD6FC1-9A07-4BA7-A7A0-6EB90B066B64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=PgBackupManager-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\PgBackupManager.UI\Assets\app.ico
InfoBeforeFile=before-install.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
