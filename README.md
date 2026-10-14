# 格林姆迦尔的妙妙工具箱 · 社区版

作者：嘟啦嘿。

本目录是社区版桌面界面的公开源码，版本 **0.1.8**，仅构建社区版。桌面应用使用 C#、WPF 和 .NET 10，支持 Windows 10 / 11 x64。AI 对话与本地知识库接口仍属预览功能，后续可能调整。

## 编译前补齐两个依赖

本仓库**不包含存档解析实现、存档解析 DLL、加密资料与图像资源包，也不提供它们的明文替代文件**。请先安装同一版本的社区版，再从安装目录复制以下两个文件。不要复制个人 UserData、存档或密钥。

| 社区版安装目录中的文件 | 本源码目录中的位置 |
| --- | --- |
| `Grimmcair.SaveParser.dll` | `Dependencies/Grimmcair.SaveParser.dll` |
| `Data/resources.gcpak` | `Data/resources.gcpak` |

可以手动复制，也可以在 PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/copy-installed-dependencies.ps1 -InstallDirectory 'C:\你的社区版安装目录'
```

两个文件需来自同一版社区安装包。它们已列入 `.gitignore`，请勿提交到 Git。存档解析 DLL 在程序运行目录根部；`Dependencies` 是源码编译时的引用位置。资料包在编译时嵌入应用程序集，保留 `Data/resources.gcpak` 是为了方便从安装版补齐构建依赖。

## 构建

安装微软官方 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，确保 `dotnet --version` 可用。已安装的运行时或本软件安装包不能代替 SDK。普通桌面构建会通过 NuGet 获取 Markdig 1.4.0；无需 Python、地图研究目录或维护者的 `.build` 环境。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-community.ps1
```

输出位于 `artifacts/desktop/`，主程序为 `GrimmcairToolBox.exe`。这默认是依赖 .NET 10 Windows Desktop Runtime 的本地构建；若希望输出包含运行时，请使用 `-SelfContained`。脚本不创建安装程序，也不启动应用。

独立 MCP 入口复用同一个工程，无桌面登录或窗口：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-community.ps1 -Target Mcp
```

输出位于 `artifacts/mcp/`，通过 stdio 运行 `Grimmcair.KnowledgeMcp.exe`。资源包为必需依赖；MCP 不调用存档解析 DLL，不读取用户存档。

### 离线环境

先在有网络的机器上准备 .NET 10 SDK 和 [Markdig 1.4.0 NuGet 包](https://www.nuget.org/packages/Markdig/1.4.0)，把 `.nupkg` 放入本地包源文件夹；两个非开源依赖仍从同版社区安装目录复制。然后执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-community.ps1 -PackageSource 'D:\nuget-offline'
```

此参数只使用所指定的包源。普通构建还需要 SDK 随附的 Windows 桌面引用组件；离线自包含发布还需要对应 .NET 运行时包，未准备时请先使用默认构建。不要将包缓存、SDK 或生成产物上传到仓库。

## 数据与升级

社区安装版将个人数据保存在当前 Windows 用户的 `%LOCALAPPDATA%\Grimmcair\Community`；专用版使用独立目录。没有安装标识的源码构建按便携版方式使用程序旁的 `UserData`。迁入旧便携版记录请使用桌面的导入功能，不要把个人目录加入源码仓库。Windows 加密保存的凭据通常不能直接迁移给其他用户或机器。

## 许可与范围

Copyright (c) 2026 嘟啦嘿。公开源码采用 [MIT 许可证](LICENSE)。该许可仅适用于本仓库提供的源码，不适用于另行取得的存档解析 DLL 或资源包。

存档解析 DLL 和加密资料/图像资源包不在本源码许可的授权范围内；请按其随附说明使用，不因本仓库代码公开而获得再分发或提取其中资源的授权。项目标志和名称保留原权利声明；第三方地图、图标和玩家资料的权利属于相应权利人。

Markdig 的独立许可见 [ThirdPartyNotices/Markdig.LICENSE.txt](ThirdPartyNotices/Markdig.LICENSE.txt)。本目录不包含任何个人存档、聊天记录、账号凭据、维护交接、地图研究原始材料或原仓库 Git 历史。文件清单及 SHA-256 见 `SOURCE-MANIFEST.json`。
