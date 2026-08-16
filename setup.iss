; GoPray installer.
;
; The payload is a single framework-dependent executable, so the one thing Setup must guarantee
; besides copying it is that the .NET 8 Desktop Runtime is present — see the [Code] section, which
; detects it and, only when missing, downloads and installs it silently after the files are laid
; down. Requires Inno Setup 6.1 or newer for the built-in download page.

#define MyAppName "GoPray"
#define MyAppVersion "0.11.0"
#define MyAppPublisher "KiraiEEE"
#define MyAppURL "https://github.com/IdleBuilt/GoPray-PC"
#define MyAppExeName "GoPray.exe"
#define MyPublishDir "bin\Release\net8.0-windows\win-x64\publish"

; Microsoft's permalink always points at the current 8.0 patch, so the installer never goes stale.
#define DotNetUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{A3F7B2C1-9D4E-4F8A-B6C2-1E3D5A8F0C9E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=Copyright (C) 2026 {#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\Setup
OutputBaseFilename=GoPraySetup-{#MyAppVersion}

; ── Compression ───────────────────────────────────────────────────────────────
; Maximum ratio, deliberately at the cost of build time. Block threads are pinned to 1 because
; splitting the stream into parallel blocks is what *costs* ratio — with a single ~8MB payload
; there is nothing to gain from the parallelism and a measurable amount to lose.
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=1
LZMANumFastBytes=273
LZMAUseSeparateProcess=yes
InternalCompressLevel=ultra64

; ── Platform ──────────────────────────────────────────────────────────────────
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; ── Wizard ────────────────────────────────────────────────────────────────────
WizardStyle=modern
WizardSizePercent=100
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableFinishedPage=no
SetupIconFile=app.ico
WizardImageFile=installer\wizard-164.bmp,installer\wizard-328.bmp,installer\wizard-410.bmp
WizardSmallImageFile=installer\small-55.bmp,installer\small-110.bmp,installer\small-138.bmp
WizardImageStretch=yes
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage

; ── Install behaviour ─────────────────────────────────────────────────────────
; Per-machine by default, but a user without admin rights can still install into their own
; profile rather than being turned away.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; GoPray holds this while running; naming it lets Setup ask politely before the forced close.
AppMutex=Local\GoPray.SingleInstance
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
RestartIfNeededByRun=no
SetupMutex=GoPraySetupMutex,Global\GoPraySetupMutex

UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
SetupLogging=yes

; The only per-user area touched is the uninstall-time cleanup of the Run entry, which is
; deliberately best-effort; everything the checkbox actually controls goes through HKA.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; The app itself ships Arabic, so Setup does too rather than making an Arabic speaker read an
; English wizard to install it.
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[CustomMessages]
english.WidgetComment=Compact prayer times widget
english.StartupTask=Start {#MyAppName} when I sign in to Windows
english.StartMenuTask=Create a Start menu shortcut
english.DotNetTitle=.NET 8 Desktop Runtime
english.DotNetSubtitle=GoPray needs this Microsoft component to run.
english.DotNetDownloading=Downloading the .NET 8 Desktop Runtime...
english.DotNetInstalling=Installing the .NET 8 Desktop Runtime. This can take a few minutes...
english.DotNetFailed=GoPray needs the .NET 8 Desktop Runtime, and Setup could not install it automatically (no internet connection?).%n%nInstall it from https://dotnet.microsoft.com/download/dotnet/8.0 and run Setup again.
english.RemoveData=Remove GoPray settings and data?%n%nLocation: %1

arabic.WidgetComment=أداة مواقيت الصلاة المصغّرة
arabic.StartupTask=تشغيل {#MyAppName} عند تسجيل الدخول إلى ويندوز
arabic.StartMenuTask=إنشاء اختصار في قائمة ابدأ
arabic.DotNetTitle=‏.NET 8 Desktop Runtime
arabic.DotNetSubtitle=يحتاج GoPray إلى هذا المكوّن من مايكروسوفت للعمل.
arabic.DotNetDownloading=جارٍ تنزيل ‎.NET 8 Desktop Runtime...
arabic.DotNetInstalling=جارٍ تثبيت ‎.NET 8 Desktop Runtime. قد يستغرق هذا بضع دقائق...
arabic.DotNetFailed=يحتاج GoPray إلى ‎.NET 8 Desktop Runtime، ولم يتمكّن المُثبِّت من تثبيته تلقائيًا (لا يوجد اتصال بالإنترنت؟).%n%nثبّته من https://dotnet.microsoft.com/download/dotnet/8.0 ثم أعد تشغيل المُثبِّت.
arabic.RemoveData=هل تريد إزالة إعدادات GoPray وبياناته؟%n%nالموقع: %1

[Tasks]
; All three are on by default, which is what someone installing a desktop widget expects.
Name: "desktopicon";  Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "{cm:StartMenuTask}";     GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon";  Description: "{cm:StartupTask}"

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Comment: "{cm:WidgetComment}"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    Comment: "{cm:WidgetComment}"; Tasks: desktopicon

[Registry]
; Setup records the checkbox; the app writes the actual Run entry on first launch.
;
; It cannot be done here: an elevated install runs as the administrator, so HKCU at this moment is
; the administrator's profile, not the profile of whoever will be signing in. HKA resolves to HKLM
; for a per-machine install and HKCU for a per-user one, and GoPray reads both on first run — so
; the checkbox lands on the right user either way, and the in-app switch owns it from then on.
Root: HKA; Subkey: "Software\{#MyAppPublisher}\{#MyAppName}"; ValueType: dword; \
    ValueName: "StartWithWindows"; ValueData: 1; Flags: uninsdeletekey; Tasks: startupicon
Root: HKA; Subkey: "Software\{#MyAppPublisher}\{#MyAppName}"; ValueType: dword; \
    ValueName: "StartWithWindows"; ValueData: 0; Flags: uninsdeletekey; Tasks: not startupicon

; Cleanup only — "dontcreatekey" means nothing is written at install time. Best effort: it removes
; the Run entry at uninstall so Windows stops trying to launch a deleted executable every sign-in.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "{#MyAppName}"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;
  RuntimeNeeded: Boolean;

// ── .NET 8 Desktop Runtime detection ─────────────────────────────────────────
// Two independent checks, because neither alone is reliable: the registry key is the one the
// runtime installer itself writes, but it is absent for some xcopy/CI deployments; the directory
// scan catches those. Only 8.x counts — a framework-dependent net8.0 app does not roll forward
// across a major version, so a machine with only .NET 9 still needs this.

function RuntimeInRegistry: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetValueNames(HKEY_LOCAL_MACHINE,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    Exit;

  for I := 0 to GetArrayLength(Names) - 1 do
    if Copy(Names[I], 1, 2) = '8.' then
    begin
      Result := True;
      Exit;
    end;
end;

function RuntimeInFolder: Boolean;
var
  FindRec: TFindRec;
  Bases: array[0..1] of String;
  I: Integer;
begin
  Result := False;
  Bases[0] := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  Bases[1] := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');

  for I := 0 to 1 do
    if FindFirst(Bases[I] + '\8.*', FindRec) then
    begin
      FindClose(FindRec);
      Result := True;
      Exit;
    end;
end;

function IsDesktopRuntimeInstalled: Boolean;
begin
  Result := RuntimeInRegistry or RuntimeInFolder;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    ExpandConstant('{cm:DotNetTitle}'),
    ExpandConstant('{cm:DotNetSubtitle}'),
    @OnDownloadProgress);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  // Decided once, up front, so the wizard can skip the download step entirely on machines
  // that already have the runtime.
  RuntimeNeeded := not IsDesktopRuntimeInstalled;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or (not RuntimeNeeded) then Exit;

  // Downloaded before the files are copied, so a machine with no connection fails while
  // nothing has been written yet rather than half-installed.
  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetUrl}', 'windowsdesktop-runtime-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      SuppressibleMsgBox(ExpandConstant('{cm:DotNetFailed}'), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Installer: String;
begin
  Result := '';
  if not RuntimeNeeded then Exit;

  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  if not FileExists(Installer) then
  begin
    Result := ExpandConstant('{cm:DotNetFailed}');
    Exit;
  end;

  WizardForm.StatusLabel.Caption := ExpandConstant('{cm:DotNetInstalling}');
  if not Exec(Installer, '/quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := ExpandConstant('{cm:DotNetFailed}');
    Exit;
  end;

  // 0 = installed, 3010 = installed but a restart is pending, 1638 = a newer build is already
  // there. All three mean the runtime is present and GoPray will run.
  if (ResultCode = 3010) then
    NeedsRestart := True
  else if (ResultCode <> 0) and (ResultCode <> 1638) then
    Result := ExpandConstant('{cm:DotNetFailed}');
end;

procedure InitializeUninstallProgressForm;
begin
  // Nothing to do; present so the uninstall UI is created before the prompt below.
end;

function InitializeUninstall: Boolean;
var
  DataDir: String;
  LegacyDir: String;
begin
  Result := True;

  // Roaming, not Local: the app writes to Environment.SpecialFolder.ApplicationData. Pointed
  // at the local app data folder this prompt never found anything, so uninstalling always left
  // the settings, the cached timetable and the error log behind.
  DataDir := ExpandConstant('{userappdata}\GoPray');
  LegacyDir := ExpandConstant('{userappdata}\PrayerTimes');

  if DirExists(DataDir) or DirExists(LegacyDir) then
    if SuppressibleMsgBox(FmtMessage(ExpandConstant('{cm:RemoveData}'), [DataDir]),
         mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      if DirExists(DataDir) then DelTree(DataDir, True, True, True);
      if DirExists(LegacyDir) then DelTree(LegacyDir, True, True, True);
    end;
end;
