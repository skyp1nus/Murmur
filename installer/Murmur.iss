; Murmur installer (Inno Setup 6.3 or newer), per user: no administrator rights, nothing outside the user profile.
; D-036 in docs/DECISIONS.md. Build it with scripts/build-installer.ps1, which publishes Murmur.Desktop (Native AOT,
; win-x64, with the ASR worker Murmur.Transcription.exe and its native libraries next to it) and then calls ISCC with
; the defines below. To compile by hand: ISCC /DPublishDir=<publish folder> /DAppVersion=0.4.0 installer\Murmur.iss
;
; This file is UTF-8 with a BOM on purpose: the Ukrainian texts below are not ASCII and Inno reads a file without a
; BOM as ANSI.

#if Ver < EncodeVer(6, 3, 0)
  #error Murmur.iss needs Inno Setup 6.3 or newer (ArchitecturesAllowed=x64compatible)
#endif

; Defines passed by scripts/build-installer.ps1 (/D...). The defaults let `ISCC installer\Murmur.iss` work as is; keep
; AppVersion equal to <Version> in Directory.Build.props (a test checks it).
#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
; Numeric a.b.c.d form of AppVersion (AppVersion may carry a prerelease suffix such as 0.2.0-rc1).
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.4.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
; Windows 10 2004, the same as $(MurmurWindowsMinVersion) in Directory.Build.props.
#ifndef MinWindowsVersion
  #define MinWindowsVersion "10.0.19041"
#endif
; Files of the publish folder that are not needed at run time (see scripts/build-installer.ps1 for the reasons).
#ifndef ExcludeMasks
  #define ExcludeMasks "*.pdb,*.dbg,*.exp,*.ilk,*.lib"
#endif

[Setup]
; AppId identifies the product for upgrades and for the uninstaller. It was generated once. NEVER CHANGE IT: a new
; value makes the next installer install side by side with the old version instead of upgrading it.
; The doubled "{{" is Inno's escape for a literal "{".
AppId={{F0C06BC8-90EC-4025-A8D8-4AEFCA30E839}
AppName=Murmur
AppVersion={#AppVersion}
AppPublisher=Vladyslav Yeromenko
AppPublisherURL=https://github.com/skyp1nus/Murmur
AppSupportURL=https://github.com/skyp1nus/Murmur/issues
VersionInfoVersion={#AppVersionNumeric}
VersionInfoCompany=Vladyslav Yeromenko
VersionInfoProductName=Murmur
VersionInfoDescription=Murmur Setup

; Per user, no UAC prompt. PrivilegesRequiredOverridesAllowed is deliberately absent: there is no "all users" choice.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Murmur
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion={#MinWindowsVersion}

; The language is taken from the OS UI language (Ukrainian or English, English when neither matches): no dialog.
ShowLanguageDialog=no
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

OutputBaseFilename=Murmur-Setup-{#AppVersion}
SetupIconFile=murmur.ico
UninstallDisplayIcon={app}\Murmur.exe
UninstallDisplayName=Murmur

; Upgrade over an older version: same AppId, same folder (UsePreviousAppDir). Murmur has no single-instance mutex, so
; AppMutex is not set; CloseApplications (the default, spelled out here) makes Setup find the running Murmur.exe and
; Murmur.Transcription.exe through the Restart Manager and close them before the files are replaced. A recording that
; is still running is therefore stopped by an upgrade: finish it first.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole publish folder, minus the debug leftovers. Models and llama.cpp are not installed: the app downloads them
; into %LOCALAPPDATA%\Murmur (D-031).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "{#ExcludeMasks}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Murmur"; Filename: "{app}\Murmur.exe"
Name: "{autodesktop}\Murmur"; Filename: "{app}\Murmur.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Murmur.exe"; Description: "{cm:LaunchProgram,Murmur}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
english.DeleteUserDataPrompt=Also delete your recordings, database, settings and downloaded models?%n%nFolder: %1%n%nThis cannot be undone. Choose No to keep them (for example if you plan to reinstall Murmur). A recordings folder that you chose outside this folder is never deleted.
ukrainian.DeleteUserDataPrompt=Також видалити ваші записи, базу даних, налаштування й завантажені моделі?%n%nТека: %1%n%nЦе неможливо скасувати. Оберіть «Ні», щоб залишити їх (наприклад, якщо ви збираєтеся перевстановити Murmur). Теку для записів, яку ви обрали поза цією текою, не буде видалено ніколи.

[Code]
// Uninstall removes the program files and the shortcuts (Inno does that from its install log). The user data in
// %LOCALAPPDATA%\Murmur (recordings, database, settings.json, models: can be many GB) is kept unless the user says
// otherwise. The question is never asked in a silent uninstall (/SILENT, /VERYSILENT) and, with /SUPPRESSMSGBOXES, the
// default answer No applies: a scripted uninstall never deletes user data.
// A recordings folder chosen in the settings (recordingsDirectory in settings.json) outside this folder is never
// touched: only the folder below is deleted.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    DataDir := ExpandConstant('{localappdata}\Murmur');
    if DirExists(DataDir) and
       (SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteUserDataPrompt'), [DataDir]),
                           mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
