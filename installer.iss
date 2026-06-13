; RuneshapePriceChecker Installer Script
; Requires Inno Setup 6+: https://jrsoftware.org/isinfo.php

#define MyAppName "RuneshapePriceChecker"
#define MyAppVersion GetVersionNumbersString("obj\Release\publish\RuneshapePriceChecker.exe")
#define MyAppPublisher "RuneshapePriceChecker"
#define MyAppURL "https://github.com/Barragek0/RuneshapePriceChecker"
#define MyAppExeName "RuneshapePriceChecker.exe"

[Setup]
AppId={{B4E5C7D2-8F1A-4A3E-9B6C-1D5E2F7A8C9B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=bin\Release
OutputBaseFilename=RuneshapePriceChecker-Installer
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayName={#MyAppName}
SetupIconFile=img\rune.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"
Name: "launchapp"; Description: "Launch {#MyAppName}"

[Files]
Source: "obj\Release\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "obj\Release\updater-publish\Update.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "obj\Release\publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "obj\Release\publish\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "ADDING_A_RESOLUTION.md"; DestDir: "{app}"; DestName: "ADDING_A_RESOLUTION.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon


[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp
Filename: "{app}\README.txt"; Description: "View README.txt"; Flags: nowait postinstall shellexec skipifsilent unchecked
Filename: "{app}\ADDING_A_RESOLUTION.txt"; Description: "View ADDING_A_RESOLUTION.txt"; Flags: nowait postinstall shellexec skipifsilent unchecked
