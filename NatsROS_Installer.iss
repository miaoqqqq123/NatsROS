; NatsROS 工业控制系统 - 自动化部署脚本
#define MyAppName "NatsROS 工业控制平台"
#define MyAppPublisher "Hexiv Automation"
#define MyAppExeName "NatsROS.Dashboard.exe"
#define MyAppLauncher "NatsROS.Launcher.exe"

; 【核心魔法】：自动读取刚编译好的 EXE 的真实版本号！
; 注意路径：iss 脚本和 Deploy_Build 在同级目录
#define MyAppVersion GetFileVersion("Deploy_Build\Bin\" + MyAppExeName)

[Setup]
; 基础配置
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName=C:\NatsROS
DefaultGroupName={#MyAppName}
OutputDir=.\InstallerOutput
OutputBaseFilename=NatsROS_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
; 强制要求管理员权限，因为我们要注册 Windows 服务和修改注册表！
PrivilegesRequired=admin

; ==========================================
; 【关键修改 1】：开启语言选择弹窗，并将默认语言设为中文
; ==========================================
ShowLanguageDialog=yes
UsePreviousLanguage=no

[Languages]
; 英文包 (自带的 Default.isl 就是英文)
Name: "english"; MessagesFile: "compiler:Default.isl"
; 中文包 (注意：必须确保 Inno Setup 的 Languages 目录下有 ChineseSimplified.isl)
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; 将整个 Deploy_Build 目录下的所有文件原封不动地拍进 C:\NatsROS
Source: "Deploy_Build\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 创建桌面快捷方式 (可以使用 {cm:...} 宏实现快捷方式名称随语言变化)
Name: "{commondesktop}\NatsROS 监控大屏"; Filename: "{app}\Bin\{#MyAppExeName}"; IconFilename: "{app}\Bin\{#MyAppExeName}"
Name: "{commondesktop}\NatsROS 一键启动器"; Filename: "{app}\Bin\{#MyAppLauncher}"; IconFilename: "{app}\Bin\{#MyAppLauncher}"

[Registry]
; ==========================================
; 【黑魔法】：注册表劫持，实现文件后缀关联
; 让 Windows 认识 .natsros 文件，并指定用 Launcher 打开它！
; ==========================================
Root: HKCR; Subkey: ".natsros"; ValueType: string; ValueName: ""; ValueData: "NatsROS.ProjectFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "NatsROS.ProjectFile"; ValueType: string; ValueName: ""; ValueData: "NatsROS 机器工程包"; Flags: uninsdeletekey
Root: HKCR; Subkey: "NatsROS.ProjectFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Bin\{#MyAppLauncher},0"
Root: HKCR; Subkey: "NatsROS.ProjectFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Bin\{#MyAppLauncher}"" ""%1"""
; ==========================================
; 【核心魔法】：注册系统级环境变量 NATSROS_HOME
; ==========================================
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: string; ValueName: "NATSROS_HOME"; ValueData: "{app}"; Flags: preservestringtype uninsdeletevalue

[Run]
; ==========================================
; 【开机基建】：利用 Windows sc 命令，注册系统底层服务
; ==========================================
; 1. 注册 NATS 消息总线为开机自启服务 (带 -js 参数开启历史流)
Filename: "{sys}\sc.exe"; Parameters: "create nats-server binPath= ""\""{app}\Bin\nats-server.exe\"" -js"" DisplayName= ""NATS Message Bus"" start= auto"; Flags: runhidden

; 安装完后立刻启动这两个服务！
Filename: "{sys}\sc.exe"; Parameters: "start nats-server"; Flags: runhidden

[UninstallRun]
; ==========================================
; 卸载时的干净收尾：停止并删除服务 (添加 RunOnceId 防止重复执行)
; ==========================================
Filename: "{sys}\sc.exe"; Parameters: "stop nats-server"; Flags: runhidden; RunOnceId: "StopNatsService"
Filename: "{sys}\sc.exe"; Parameters: "delete nats-server"; Flags: runhidden; RunOnceId: "DeleteNatsService"