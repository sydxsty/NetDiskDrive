# NetDiskDrive 界面演示

以下截图使用 **0.7.0 的实际 Web UI**，由隔离的模拟宿主提供示例数据。账户、盘符、路径、容量、块状态、日志和传输字节数均为演示数据；未连接真实云账户，也未操作真实磁盘。它们不代表实测云流量、压缩率或性能，不包含 CrystalDiskMark 测试结果。

每张图均带“模拟数据，非实测”标记；点击图片可查看原图。程序文件名仍为 `OverlayDisk.exe`。

## 磁盘与本地缓存

查看盘符、加密和挂载状态，直接打开或安全卸载磁盘。启用缓存上限后，界面会区分本地占用与虚拟容量，并显示尚需按需下载的块数。

[![磁盘列表：挂载状态、云同步和本地缓存上限](images/demo-disks.png)](images/demo-disks.png)

## 块状态

按本地物理位置查看对象状态，并按状态或内容类型筛选。待同步总数包含尚未缓存在本地的对象；物理网格只显示本地块。拖动图例调整颜色优先级，不改变块号顺序。

[![块状态：增量上传、未缓存对象和物理块网格](images/demo-blocks.png)](images/demo-blocks.png)

## 从云端导入

默认创建独立副本并按需加载：先接入索引，访问文件时再下载所需对象。取消“按需加载文件内容”可先完整下载。恢复原硬盘身份是另一项明确选择，要求确认没有其他本地副本或并发写入者。

[![云端导入：独立副本、保存位置与按需加载选项](images/demo-cloud-import.png)](images/demo-cloud-import.png)

## 请求限制与预取

设置同步间隔、同时传输对象数、全局请求限额，以及顺序或自适应预取。对象大小在创建磁盘时选择 4、8 或 16 MiB，预取数量按该磁盘的对象大小计算。

[![设置：同步间隔、网盘请求限制和自适应预取](images/demo-settings.png)](images/demo-settings.png)

## 同步任务日志

逐条查看上传确认、对象复用和版本准备记录。日志分别显示原对象大小与压缩后上传字节数，支持筛选磁盘、只看错误和复制对象 ID。下图数值仅用于说明字段含义，不能据此推算实际压缩收益。

[![同步日志：上传确认、对象复用与压缩后字节数](images/demo-tasks.png)](images/demo-tasks.png)

WinSpd - Windows Storage Proxy Driver, Copyright (C) Bill Zissimopoulos — https://github.com/winfsp/winspd
