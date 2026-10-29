#ifndef AppVersion
  #define AppVersion "1.4.5"
#endif
#define NumericVersion AppVersion
#if Pos("-", AppVersion) > 0
  #undef NumericVersion
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#endif
#ifndef PayloadDirectory
  #define PayloadDirectory AddBackslash(SourcePath) + "..\artifacts\Win10ray-" + AppVersion
#endif
#ifndef BuildOutput
  #define BuildOutput AddBackslash(SourcePath) + "..\artifacts"
#endif

[Setup]
#ifdef TestBuild
AppId=Win10ray-Updater-Integration-Test
Uninstallable=no
CreateUninstallRegKey=no
UsePreviousAppDir=no
#else
AppId={{4FBAF886-CFEB-4ADE-8F01-611553E36F5A}
#endif
AppName=Win10ray
AppVersion={#AppVersion}
AppPublisher=zephrx0
AppPublisherURL=https://github.com/zephrx0/Win10ray
AppSupportURL=https://github.com/zephrx0/Win10ray/issues
AppUpdatesURL=https://github.com/zephrx0/Win10ray/releases
DefaultDirName={autopf}\Win10ray
UninstallDisplayIcon={app}\Win10ray.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
SetupIconFile=..\Assets\Win10ray.ico
OutputDir={#BuildOutput}
OutputBaseFilename=Win10ray-Setup-{#AppVersion}
Compression=lzma2/max
LZMADictionarySize=131072
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=1
SolidCompression=yes
WizardStyle=modern
VersionInfoDescription=Win10ray Setup
VersionInfoProductName=Win10ray
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PayloadDirectory}\Win10ray.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\updater.exe"; DestDir: "{app}"; Flags: ignoreversion
#ifndef TestBuild
Source: "{#PayloadDirectory}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\docs\BUILD.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "{#PayloadDirectory}\docs\JSON.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "{#PayloadDirectory}\docs\licenses\QRCoder.txt"; DestDir: "{app}\docs\licenses"; Flags: ignoreversion
Source: "{#PayloadDirectory}\Assets\Routing\LICENSE"; DestDir: "{app}\Assets\Routing"; Flags: ignoreversion
Source: "{#PayloadDirectory}\xray\xray.exe"; DestDir: "{app}\xray"; Flags: ignoreversion
Source: "{#PayloadDirectory}\xray\geoip.dat"; DestDir: "{app}\xray"; Flags: ignoreversion
Source: "{#PayloadDirectory}\xray\geosite.dat"; DestDir: "{app}\xray"; Flags: ignoreversion
Source: "{#PayloadDirectory}\xray\LICENSE"; DestDir: "{app}\xray"; Flags: ignoreversion
Source: "{#PayloadDirectory}\xray\VERSION.txt"; DestDir: "{app}\xray"; Flags: ignoreversion
Source: "{#PayloadDirectory}\tun\sing-box.exe"; DestDir: "{app}\tun"; Flags: ignoreversion
Source: "{#PayloadDirectory}\tun\wintun.dll"; DestDir: "{app}\tun"; Flags: ignoreversion
Source: "{#PayloadDirectory}\tun\libcronet.dll"; DestDir: "{app}\tun"; Flags: ignoreversion
Source: "{#PayloadDirectory}\tun\LICENSE"; DestDir: "{app}\tun"; Flags: ignoreversion
Source: "{#PayloadDirectory}\tun\COPYING"; DestDir: "{app}\tun"; Flags: ignoreversion
Source: "{#PayloadDirectory}\tun\LICENSE-Wintun"; DestDir: "{app}\tun"; Flags: ignoreversion
Source: "{#PayloadDirectory}\tun\VERSION.txt"; DestDir: "{app}\tun"; Flags: ignoreversion
Source: "{#PayloadDirectory}\service\Win10ray.TunService.exe"; DestDir: "{app}\service"; Flags: ignoreversion
Source: "{#PayloadDirectory}\Assets\Flags\SOURCE.txt"; DestDir: "{app}\Assets\Flags"; Flags: ignoreversion
Source: "{#PayloadDirectory}\docs\images\connection.png"; DestDir: "{app}\docs\images"; Flags: ignoreversion
#endif

#ifndef TestBuild
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{autoprograms}\Win10ray"; Filename: "{app}\Win10ray.exe"
Name: "{autodesktop}\Win10ray"; Filename: "{app}\Win10ray.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Win10ray.exe"; Description: "{cm:LaunchProgram,Win10ray}"; Flags: nowait postinstall skipifsilent runasoriginaluser
#endif

#ifdef TestFailBeforeInstall
[Code]
function InitializeSetup(): Boolean;
begin
  Result := False;
end;
#endif
