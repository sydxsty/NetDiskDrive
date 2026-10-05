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

默认输出 `artifacts/OverlayDisk-v0.7.0/`。为避免夹带上次构建的文件，输出目录必须为空；保留旧目录，复建时指定新的目录：

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

如指定自定义发布目录，为测试传同一个 `-ApplicationDirectory`。测试使用新的临时目录，包括启动、原生存储核心、日志、4/8/16 MiB 压缩同步及缓存校验，不是吞吐 benchmark。

需要驱动和管理员权限的真实挂载验证单独选择：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-local.ps1 -IncludeMount
```

该选项只创建新的隔离测试容器，验证 NTFS、卸载重挂和文件校验，不接受已有磁盘号作为目标。建议在测试电脑中运行。

## 项目结构

| 目录 | 内容 |
| --- | --- |
| `engine/` | Rust 块存储、CoW、快照、页认证、缓存与原生接口 |
| `app/` | .NET Windows 服务、WinSpd 接入、WebView2 管理界面 |
| `cloud/OverlayDisk.Cloud.Contracts` | 独立后端协议与 zstd 对象传输 |
| `cloud/OverlayDisk.Cloud.Sync` | 版本发布、恢复、回执及增量同步 |
| `cloud/OverlayDisk.Cloud.Baidu` | 百度私有网页接口、限流与凭据隔离 |
| `app/ThirdParty/WinSpd` | 上游 C# 绑定、修改说明与许可 |

当前用户入口只读取当前格式。仓库保留较早的研究实现及测试以供参考，这不表示当前程序兼容旧磁盘。

WinSpd - Windows Storage Proxy Driver, Copyright (C) Bill Zissimopoulos — https://github.com/winfsp/winspd
