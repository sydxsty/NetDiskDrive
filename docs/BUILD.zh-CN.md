# Windows 源码构建

## 准备

- Windows x64、Git。
- Rust stable，`x86_64-pc-windows-msvc` 工具链。
- Visual Studio Build Tools，勾选“使用 C++ 的桌面开发”和 Windows SDK。
- .NET 8 SDK；运行 GUI 还需要 .NET 8 Desktop Runtime、WebView2 Runtime 和 Visual C++ x64 Runtime。

从 Visual Studio 的开发者 PowerShell 打开仓库根目录：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

默认输出 `artifacts/OverlayDisk-v0.8.0/`。为避免夹带上次构建的文件，输出目录必须为空；保留旧目录，复建时指定新的目录：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -OutputDirectory C:\Builds\NetDiskDrive-public
```

`dotnet.exe` 和 `cargo.exe` 从 PATH 查找，Rust 也支持标准用户安装位置。可以用 `-DotNetPath`、`-CargoPath` 指定工具。构建缓存默认位于 `%LOCALAPPDATA%\OverlayDisk\build`。

## 脚本做什么

1. 下载固定版本 WinSpd MSI，核对 SHA-256 和 Authenticode 签名，提取 DLL；这一步不安装内核驱动。
2. 运行 Rust、百度协议模拟和云同步测试；然后编译 Rust DLL 与 Windows 应用。`-SkipTests` 只用于已经验证过相同源码的重复打包。
3. .NET 使用无 PDB 的发布配置，Rust 与 .NET 对项目和用户目录进行路径映射，避免二进制暴露构建机目录。
4. 复制公开文档、许可证和资源，生成文件 SHA-256 清单。不会打包账号状态、容器、测试记录或历史发布目录。

依赖下载需要联网：Rust crates、NuGet 和官方 WinSpd Release。默认功能测试不访问真实百度网盘账户。

## 最小功能验证

构建后执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-local.ps1
```

如指定自定义发布目录，为测试传同一个 `-ApplicationDirectory`。测试使用新的临时目录，包括启动、原生存储核心、日志、4/8/16 MiB 压缩同步、缓存，以及懒加载副本的准备/确认/取消/切换校验，不是吞吐 benchmark。

需要驱动和管理员权限的真实挂载验证单独选择：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-local.ps1 -IncludeMount
```

该选项只创建新的隔离测试容器，验证 NTFS、卸载重挂、懒加载最新快照切换和文件校验，不接受已有磁盘号作为目标。建议在测试电脑中运行。

只验证本轮副本功能时，可在构建目录运行 `dotnet OverlayDisk.dll --replica-smoke C:\Temp\ReplicaCheck`。管理员终端中的 `dotnet OverlayDisk.dll --replica-mount-smoke C:\Temp\ReplicaMountCheck` 另创建新 256 MiB 加密测试盘，检查只读/读写挂载、卸载后切换及文件内容；两条命令都要求新的空输出目录，不访问真实网盘账户。

GPT 恢复回归可单独运行 `dotnet OverlayDisk.dll --replica-gpt-smoke C:\Temp\ReplicaGptCheck`。它在新的空目录中创建 4 TiB 虚拟容量的稀疏测试容器，通过真实应用服务与原生接口检查分区表、恢复重试及快照更新；只写入少量测试数据，无需管理员权限或真实挂载，云对象来自内存模拟后端。

界面交互可以单独用 Node.js、Playwright 和 Microsoft Edge 验证。以下示例中的 Playwright 路径按本机安装位置填写，结果保存在指定临时目录：

```powershell
node app/web/tests/replica.browser.test.cjs C:\Temp\NetDiskDrive-replica-ui C:\Tools\node_modules\playwright
```

该测试加载实际界面代码，以模拟的磁盘服务检查挂载限制、解锁、版本确认、覆盖警告、取消和缓存状态；阻止外网请求，不接触真实磁盘或百度账户。它证明界面交互符合约定，不能代替存储原子性、真实挂载或云端协议检查。

## 项目结构

| 目录 | 内容 |
| --- | --- |
| `engine/` | Rust 块存储、CoW、快照、页认证、缓存与原生接口 |
| `app/` | .NET Windows 服务、WinSpd 接入、WebView2 管理界面 |
| `cloud/OverlayDisk.Cloud.Contracts` | 独立后端协议与 zstd 对象传输 |
| `cloud/OverlayDisk.Cloud.Sync` | 版本发布、恢复、回执及增量同步 |
| `cloud/OverlayDisk.Cloud.Baidu` | 百度私有网页接口、限流与凭据隔离 |
| `app/ThirdParty/WinSpd` | 上游 C# 绑定、修改说明与许可 |

仓库只保留当前存储核心及其接口、测试和格式说明。旧版存储实现、原生接口和专用示例已移除；当前程序不读取或迁移旧格式磁盘。

WinSpd - Windows Storage Proxy Driver, Copyright (C) Bill Zissimopoulos — https://github.com/winfsp/winspd
