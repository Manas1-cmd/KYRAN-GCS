; ============================================
; SQK GCS — Inno Setup Installer Script
; ТОО «Стандарт Қапал Құрылыс»
; ============================================
; Сборка:
; 1) dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
; 2) Скачать Inno Setup: https://jrsoftware.org/isdl.php
; 3) Открыть этот файл в Inno Setup Compiler → Compile
; 4) После сборки вычислить SHA-256:
;    certutil -hashfile output\SQK_GCS_Setup_v1.0.exe SHA256 > SQK_GCS_v1.0_SHA256.txt
; ============================================

#define MyAppName "SQK GCS"
#define MyAppVersion "1.0"
#define MyAppPublisher "ТОО «Стандарт Қапал Құрылыс»"
#define MyAppExeName "SQK GCS.exe"

; Путь к папке publish (относительно этого .iss файла)
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{B8A7D3E1-4F2C-4A8B-9E6D-1C3F5A7B9D2E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\installer\output
OutputBaseFilename=SQK_GCS_Setup_v1.0
SetupIconFile=..\app_icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
LicenseFile=LICENSE.txt

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Все файлы из publish — рекурсивно
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Папка Elevation (SRTM данные) если есть
Source: "{#PublishDir}\Elevation\*"; DestDir: "{app}\Elevation"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent