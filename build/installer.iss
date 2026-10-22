; BetterScene（Windows 视觉改善）安装版脚本（Inno Setup 6）
; 由 build/build-release.ps1 调用：
;   ISCC /DAppVersion=x.y.z /DSourceDir=<publish 目录> /DOutputDir=<输出目录>
;        /DVariant=merged|split /DBootstrapRuntime=0|1 installer.iss
;
; 两种变体：
;   merged（合并版）：运行时随包携带，开箱即用。
;   split（分离版）：框架依赖，安装时检测并引导安装 .NET Desktop Runtime 与 Windows App SDK Runtime。

#define AppName "Windows 视觉改善"
#define AppExeName "BetterScene.exe"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
; merged / split
#ifndef Variant
  #define Variant "merged"
#endif
; 1 时安装前引导安装运行时（仅 split 版）
#ifndef BootstrapRuntime
  #define BootstrapRuntime "0"
#endif

[Setup]
AppId={{7C3E9A21-5B48-4F16-9D72-8E1A4C6B0F35}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DSUK
DefaultDirName={autopf}\BetterScene
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 允许免管理员的 per-user 安装到 %LocalAppData%，无需 UAC。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=BetterScene-{#AppVersion}-x64-{#Variant}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; 安装/升级时自动关闭正在运行的实例，避免文件占用导致复制失败。
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文语言包不在 Inno 默认发行版内，由构建脚本下载到 build\languages；
; 若缺失则自动跳过，仅用英文。
#ifdef ChineseISL
Name: "chinesesimplified"; MessagesFile: "{#ChineseISL}"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

; 用户数据位于 %LocalAppData%\BetterScene（设置、日志、控制项缓存、哨兵文件），
; 不在安装记录内，卸载时保留，避免误删。

[Code]
const
  // 运行时引导下载地址（官方 aka.ms 短链，始终指向最新受支持版本）。
  DotNetDesktopUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  WindowsAppSdkUrl = 'https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe';

// 检测 .NET Desktop Runtime：共享框架目录里是否存在 Microsoft.WindowsDesktop.App 主版本 10。
function HasDotNetDesktopRuntime(): Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(Root + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          if Copy(FindRec.Name, 1, 3) = '10.' then
            Result := True;
      until (not FindNext(FindRec)) or Result;
    finally
      FindClose(FindRec);
    end;
  end;
end;

// 检测 Windows App SDK Runtime：x64 应用需要 x64 框架包。
function HasWindowsAppSdkRuntime(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM,
       'SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages',
       Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Pos('Microsoft.WindowsAppRuntime.', Names[I]) = 1) and (Pos('_x64_', Names[I]) > 0) then
        Result := True;
end;

// 下载并静默安装一个运行时；失败返回 False 并记录原因。
function DownloadAndRun(Url, FileName, Params, DisplayName: String; var ErrorMsg: String): Boolean;
var
  TempFile: String;
  ResultCode: Integer;
begin
  Result := False;
  try
    DownloadTemporaryFile(Url, FileName, '', nil);
  except
    ErrorMsg := '下载 ' + DisplayName + ' 失败：' + GetExceptionMessage;
    Exit;
  end;

  TempFile := ExpandConstant('{tmp}\') + FileName;
  if not FileExists(TempFile) then
  begin
    ErrorMsg := '下载 ' + DisplayName + ' 后未找到文件。';
    Exit;
  end;

  if not Exec(TempFile, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrorMsg := '运行 ' + DisplayName + ' 安装程序失败。';
    Exit;
  end;
  if (ResultCode <> 0) and (ResultCode <> 1638) then // 1638 = 已安装更高版本
  begin
    ErrorMsg := DisplayName + ' 安装未成功（退出码 ' + IntToStr(ResultCode) + '）。';
    Exit;
  end;
  Result := True;
end;

// split 版：缺哪个运行时装哪个。合并版（BootstrapRuntime=0）不需要，直接跳过。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrMsg: String;
begin
  Result := '';

#if BootstrapRuntime == "1"
  if not HasDotNetDesktopRuntime() then
  begin
    if not DownloadAndRun(DotNetDesktopUrl, 'windowsdesktop-runtime.exe', '/install /quiet /norestart',
        '.NET Desktop Runtime 10', ErrMsg) then
    begin
      Result := ErrMsg;
      Exit;
    end;
  end;

  if not HasWindowsAppSdkRuntime() then
  begin
    if not DownloadAndRun(WindowsAppSdkUrl, 'windowsappruntimeinstall.exe', '--quiet',
        'Windows App SDK Runtime', ErrMsg) then
    begin
      Result := ErrMsg;
      Exit;
    end;
  end;
#endif
end;
