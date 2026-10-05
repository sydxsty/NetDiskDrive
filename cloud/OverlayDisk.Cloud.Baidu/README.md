# 百度私有网页后端

这是 .NET 8 `ICloudObjectStore` 的独立实现，不依赖磁盘引擎、桌面界面或管理员权限。只接收本应用 WebView2 登录窗口交出的 cookie，不读取外部浏览器数据。账号密码不进入本库。

## 接口与会话

`new BaiduClient(BaiduCookieSession session, HttpMessageHandler? handler = null, BaiduClientOptions? options = null)`。

- `ValidateAsync` 向会员用户信息接口验证当前账户并返回不含凭据的 `CloudAccountInfo`；成功结果默认复用 5 分钟。`AccountValidationTtl=TimeSpan.Zero` 可关闭此复用。
- `InitializeWebSessionAsync` 验证账户，再访问 `disk/home`、`api/loginStatus`、`api/gettemplatevariable`、`pcloud/user/getinfo` 完成网页会话初始化，缓存正文中的 `bdstoken`。变更操作自动调用它。
- `ExportSession` 供 CloudHost 保存服务端更新的 cookie。保留 name/value/domain/path/secure/httpOnly/expires。发送时遵守域、路径和有效期；同名 cookie 优先使用匹配的具体域和路径。
- `WindowsSessionVault.SaveAsync(path, session)`、`LoadAsync(path)`、`Delete(path)` 使用当前 Windows 用户 DPAPI。只把密文写入文件，临时文件同样仅有密文，完成落盘后原子替换。其他 Windows 用户或机器不能直接解锁。调用者选择自己的应用数据目录。

凭据和签名下载 URL 不出现在异常、诊断事件或 DTO 的 `ToString()` 中；`Diagnostic` 只收到操作名称、错误码和 HTTP 状态。不要把 `ExportSession` 的序列化结果作为日志。库不会修改 WebView2 的持久配置，退出登录时还需由界面清除自己 WebView2 profile 的 cookie。

## 全局请求限流与 PCS 认证

所有客户端默认共用 `BaiduRequestScheduler.Shared`，默认每秒 2 个请求、同时 2 个请求。应用启动和设置保存时调用 `Shared.Configure(new BaiduRequestLimits(requestsPerSecond, maximumConcurrentRequests))`，现有客户端及已排队请求立即采用新设置；合法范围是每秒 0.1–20 次、并发 1–8。降低并发时让已开始的响应结束，直到活动数低于新上限才继续排队请求，不中断传输。`Snapshot()` 提供 `Limits`、`QueuedRequests`、`ActiveRequests`、`StartedRequests`，不包含路径或账户信息。`BaiduClientOptions.RequestScheduler` 可注入独立调度器供离线测试；生产应用使用默认共享实例，不能为每块磁盘创建独立额度。

调度器按先进先出、单调时钟的相邻请求最小间隔放行，空闲后不积累突发额度。会话初始化、目录查询、上传定位、每次分片、下载定位、实际下载、删除、轮询、重试和手动跟随的重定向全部经过同一发送入口。并发名额覆盖完整响应正文，不在收到响应头时释放；下载校验 EOF、关闭流、请求取消或客户端释放都会关闭正文并归还名额。排队时间不计入请求网络超时。这是进程内共享预算；应用的下载/上传由同一普通权限进程访问百度，没有提权 worker 绕过入口的第二套 HTTP 通道。界面 WebView2 本身的官方登录网页请求不经过此 HTTP 客户端。

PCS API 请求显式使用本应用 pan 会话的 `BDUSS` 及存在时的 `STOKEN`。不能仅依赖 CookieContainer 对节点域的自动匹配：host-only `pan.baidu.com` STOKEN 不会自动发送到 `d.pcs.baidu.com` 等上传节点。只向已经过 HTTPS 白名单校验的 PCS API 发送这对凭据，不发送 passport 域 STOKEN、整个浏览器 Cookie 集或 URL 凭据参数。内容下载的 baidupcs 域仍只获得 BDUSS。此修正依据下列固定上游源码的明确双凭证协议，能够修复“节点请求漏带 STOKEN”这一差异；现有只含状态码的 403 日志不能证明所有账号的 403 都由此造成。

HTTP 401/403 的响应仅有界读取最多 16 KiB，尝试提取已知的数字认证/验证错误码（-6、31045、132、9019）；原始正文不进入错误和日志。未知 403、HTML 拒绝页以及其他数字错误不会被盲目重试、切换节点或自动恢复旧 Cookie。确认的认证失效使本地认证提示失效并交给应用登录窗口处理；重新获得有效会话由 WebView2 完成，本库不能自行刷新已失效的 BDUSS。上传仍要求明确的成功回执；返回的分片 MD5 存在时核对，不接受错误状态或相互矛盾的对象信息。

## 分片正文长度修正（0.4.2）

真实账号验证确认：完整登录会话、预上传和上传定位均成功，但外层 multipart 总长度未知时，.NET 使用 `Transfer-Encoding: chunked`，当前 PCS 上传节点返回 HTTP 403 / `31211`。只设置分片子内容的 `Content-Length` 不够；`MultipartContent.TryComputeLength` 仍依赖 `StreamContent` 的实际可计算长度。

分片流现提供范围内的 Seek/Position/Length，底层继续使用同一份预校验缓冲或有界临时流。外层 multipart 因而得到精确总长度，不额外复制 4 MiB 载荷，也不更换节点或 User-Agent。使用同一保存会话和相同节点的定长诊断请求上传成功；正式实现再上传一个 4 MiB 测试文件并完整下载核对通过。未知 403 仍不作为可盲目重试的错误。

定向回归 `--filter fixed-length-multipart` 校验整个 multipart 的声明长度与实际序列化字节数一致，并核对重试保持同一文件内容和缓冲区。

## 单机写入下的本地缓存

独立库默认 `AssumeExclusiveWriter=false`。应用明确保证只有本机这套同步协调器修改管理目录时，才能启用此选项。`MetadataCacheDirectory` 指定持久缓存位置，内部在验证账户后按 provider/account 派生独立目录；不保存密码、Cookie 或签名下载链接。每个账户缓存有本地排他租约，防止两个实例同时维护互不知情的目录证明。没有配置目录时仅使用内存缓存。

缓存只把完整读取的父目录、或未受并发变更干扰的新建空目录视为完整索引。`HeadAsync` 可以据此证明路径存在或不存在；单次 404、半页列表、失败请求都不能产生“不存在”证明。显式 `ListAsync` 始终访问服务端，读者 pin 检查也因此保持实时。上传/删除使用追加日志增量更新索引：云变更前先持久写入带事务 ID 的 BEGIN，确认后追加包含变更项的 COMMIT；同目录并行变更使用独立事务 ID。重开仅在全部记录校验通过、序号连续、所有 BEGIN 均有 COMMIT 时恢复完整证明。进程在云请求中途被杀、回执丢失、并行失败或校验不符时，重开必须重新读取该父目录；已确认并落盘的回执无需等待完整 checkpoint，也能在进程被杀后恢复。目录删除会保守地使本账户目录索引全部失效，以免遗留子目录证明。

持久文件包含版本、provider/account、完整目录路径和 SHA-256 校验。每个目录由 `.cache` checkpoint 与 `.wal` 追加日志构成，二者绑定同一随机代数；日志记录有严格序号和独立 SHA-256 校验。损坏、截断、日志丢失、代数不符、错误账户或格式均使本地回执查询返回未命中，下一次确需 Head 时实时读取父目录。旧版单文件缓存不迁移，仅重新获取目录证明。`MaximumCachedMetadataEntries` 默认 8192，限制非活动目录索引的内存 LRU；淘汰不删除磁盘索引，重启和再次访问仍无需网络 LIST。活动变更会暂时保留所涉及的目录索引。单目录缓存最多 16384 项、文件最多 64 MiB；超过时仍正常列目录，但不缓存完整性证明。缓存写入故障不会变成远端对象存在/不存在的肯定结论。普通变更只追加小记录；日志记录数达到 `max(256, 当前目录项数)` 或日志达到 16 MiB，且没有活动变更时才重写完整 checkpoint。切换代数前先持久使旧 checkpoint 失效，再落盘新日志头并原子发布新 checkpoint，中间崩溃只能导致重新列目录，不能恢复旧的不存在证明。新完整列表或新建空目录也会建立 checkpoint。

`InvalidateCaches()` / `InvalidateCachesAsync()` 用于手动刷新，清除目录证明、网页令牌和账户/上传节点有效期；即使尚未加载缓存，也会在第一次选择已验证账户目录时使旧索引失效。请在没有传输任务时调用。认证失败也会失效缓存。上传节点按服务端 `expire` 与本地 `UploadEndpointTtl`（默认 5 分钟）的较短值复用，失败后重新定位。`GetRequestCounts()` 只返回固定操作标签的累计 HTTP 发送尝试数，不发逐请求日志。

## 对象语义

路径使用绝对 `/` 路径。目录列表逐页读取；达到页数上限、重复页面、缺失响应字段都抛错，不能变成“对象不存在”。`HeadAsync` 以父目录中的完整路径精确匹配。API 无事务、CAS 或已证明的原子重命名能力，调用者不能把它当作 S3。

`PutImmutableAsync` 从输入流当前位置读取声明的长度并验证调用者提供的 SHA-256。输入流归调用者所有。固定 4 MiB 分片经 `precreate → locateupload → superfile2 → create` 上传，显式发送 `rtype=0,is_revision=0`；分片回执含 MD5 时必须与本地一致；省略 MD5 时必须有明确的成功状态码。未知路径保留一次存在性判断，避免仅靠未单独证明的私有接口冲突语义；完整父目录缓存命中时该判断不需要网络。同一个客户端内相同路径的上传和删除串行化。客户端之间没有云端排他锁；同步层仍需遵守单台写入约定。

新上传以百度明确的 create 成功回执（JSON 中 errno/error_code 为 0）确认完成，不再为验证刚上传的对象而下载或重新列目录。回执中实际存在的 path/size/isdir 必须与请求一致，字段类型错误、错误状态、HTML 和没有成功状态的普通 HTTP 200 不会当成成功。可选字段缺失不触发下载；最终 MD5 仅作为提供方元数据保存，不比较其明文值，因为它可能是混淆后的表示。precreate 明确返回 return_type=2 的秒传成功采用同样的确认规则，不下载内容。已移除 VerifyNewUploadsByDownload 选项，调用方不能再意外开启每次上传后的完整读回。

只在未知的既有路径、路径冲突或网络中断/暂时服务错误造成的提交结果不确定时，保留同一路径下载并核对 SHA-256，以维持不可变对象冲突语义。明确的错误状态或矛盾 metadata 不会通过下载变成成功。单写者缓存已有同路径、同长度、同 SHA-256 的成功回执时直接复用。CloudObjectInfo.ReusedExisting 区分缓存复用、秒传/找回既有对象与新建上传。正常成功确认信任百度服务端，不声称已经独立读回验证远端数据；实际恢复和按需下载仍按源清单进行 SHA-256/认证校验。

不超过 16 MiB 对象压缩上界的 wire 内容使用下面的 prepared factory，单次调用持有一份独占对象缓冲，HTTP 分片与重试读取它的只读视图，不再额外复制整个分片或重算摘要。更大的清单最多 4 GiB，使用删除时关闭的临时文件和一份 4 MiB 读取缓冲限制内存；临时内容是调用者交付的对象字节，不含浏览器凭据。此临时文件不是额外历史版本。零字节上传当前不支持；OverlayDisk 的对象和提交清单均为非空。

## 预校验上传能力（0.4）

Contracts 的可选接口 `ICloudPreparedUploadStore` 不依赖 Baidu、磁盘引擎或 GUI：

```csharp
var descriptor = new ImmutableObjectDescriptor(path, length, authenticatedSha256);
var confirmed = await preparedStore.TryGetConfirmedReceiptAsync(descriptor, ct);
if (confirmed is null)
{
    // Only a miss opens the fixed source lease. The raw export need not hash again.
    using var prepared = await PreparedUpload.CreateAsync(descriptor, source, ct);
    confirmed = await preparedStore.PutPreparedAsync(prepared, ct);
}
```

`ImmutableObjectDescriptor(string Path, long Length, string Sha256)` 的摘要必须来自调用者已认证的封存清单；本库不会把任意输入摘要当作已认证清单。`TryGetConfirmedReceiptAsync` 只查独占写账户下的本地已确认回执，不读取源对象、不请求 HEAD/LIST 或下载；冷启动为选择账户范围可以做一次账户验证。未命中不表示远端不存在，后续上传仍使用既有目录证明和必要的错误恢复。不同长度/摘要是冲突。默认未启用 `AssumeExclusiveWriter` 时此能力返回未命中。

`PreparedUpload.CreateAsync(descriptor, Stream, ct)` 只接收 1 字节至 16 MiB + 128 KiB 的有限 wire 对象，要求声明长度后立即 EOF。factory 一次读取输入、核对完整 SHA-256 并计算 MD5，单分片整体/分片 MD5 复用同一个结果；8/16 MiB 对象同一读取过程中计算完整 MD5 和每个 4 MiB 分片的 MD5。截断、多余字节、摘要不符或取消不会产生 prepared 对象；输入流由调用者释放。私有构造函数保证调用方不能自行制造“已经验证”的实例，底层数组不对外暴露，也不保留调用方可修改的内存。

`PreparedUpload` 提供只读的 `Descriptor`、`Length`、`Sha256`、`Md5`、`PartMd5` 及不可获取底层数组的 `OpenRead()`。释放 owner 后不能新建 reader，已在途 reader 保留稳定字节到关闭，防止调用方释放对象改变正在上传或重试的内容。`PutPreparedAsync` 使用同一稳定内容，不再重复 SHA；回执中存在的分片 MD5 和对象身份仍核对，正常创建/秒传成功直接确认，只有不确定结果及未知既有对象才进行完整 SHA 读回。通用 `PutImmutableAsync` 仍自行验证任意输入，不能靠 expectedSHA 或布尔标记跳过检查；其小对象分支也要求声明长度后 EOF。同步协调器可以因此移除自己的重复 SHA，但 seal 首次认证、descriptor 来源认证与恢复时内容校验仍属于各自边界。

下载使用已登录 UID、BDUSS 派生的 locatedownload 签名，签名链接只在本库内部使用。默认完整对象 GET。Range 必须返回 HTTP 206 且 Content-Range 起止和总长度完全一致，HTTP 200 不能冒充 Range 成功。流由调用者释放，读到 EOF 时检查短流和多余字节。请求/流读取超时、取消及网络中断向上传播；中断后同步层应重新读取并校验整个对象。

若调用者已有经过认证的不可变对象描述，可使用 Contracts 可选接口 `ICloudKnownObjectReader.OpenReadKnownAsync(ImmutableObjectDescriptor descriptor, CancellationToken)`。此接口支持 1 字节至 `PreparedUpload.MaximumLength` 的完整 wire 对象，先验证路径、长度和 SHA 字段形状，再直接 locate-download→GET，完全不请求 Head/LIST，也不依赖目录缓存。已验证会话每次读取为 2 个请求；冷会话额外验证一次账户。返回流继续检查成功状态、完整响应及精确长度，读取到 EOF 才完成短流/多余字节检查。调用者必须按 descriptor 的 SHA-256 校验内容；本接口不会把字段形状检查当成内容认证，也不会据此生成已确认上传回执。无此可选能力的后端继续使用通用 OpenReadAsync，由上层保留相同的摘要校验。当前已知对象接口不提供 Range。

`ICloudBatchDeleteStore.DeleteManyAsync` 每批至多 64 条路径，使用现有 filelist 数组协议发送一次 delete 并等待任务，然后每个受影响父目录只做一次实时 LIST 确认全部目标消失。`DeleteAsync` 复用此流水线。`errno=0` 本身不是完成证明；失败/丢失响应后也只有重新读取目录证明确实全部不存在，才可幂等成功。不会把通用错误码 12 当成“已删除”。上层只应在新快照对象闭包上传、提交和核验完毕后删除旧对象；百度单文件历史不能替代对象集合的一致提交。

模拟服务的精确调用计数（均为小于或等于 4 MiB 的对象、完整成功回执）：冷建一层目录 7 次；已就绪会话首次上传 4 次（预创建、定位、分片、创建），同会话后续新对象 3 次，已验证同内容重复 Put 为 0 次。重启后复用三个对象共 1 次账户验证、0 次 LIST；重启后新增一个对象合计 9 次（账户 1、网页会话 4、上传 4），0 次 LIST。已知 65 个对象分 64+1 两批删除，共 8 次（delete 2、模拟任务轮询 4、确认 LIST 2）。实际页数、服务端任务耗时、失效和错误恢复会增加调用；这些是功能断言，不是性能测量。

HTTP handler 可注入。默认自动重定向关闭，手工仅跟随 GET 的百度/PCS 域名白名单，登录跳转触发重新登录。定位接口或重定向若返回白名单内的 HTTP 默认端口地址，会保留转义后的路径和签名参数、仅升级为 HTTPS；带 userinfo、非默认端口或外部域的地址拒绝使用。实际网络请求始终为 HTTPS，不回退到明文 HTTP。暂不处理验证码绕过、会员限速绕过、外部账号登录、官方 OAuth access_token 或网盘多账号同时写入。

## 协议来源与验收边界

实现独立编写，只核对请求字段和状态机，没有复制参考项目的日志、持久化代码或 GPL/AGPL 实现。

- [BaiduPCS-Rust 固定提交 427c5c38：client.rs](https://github.com/komorebiCarry/BaiduPCS-Rust/blob/427c5c38d8589b91c3a5fee09e5c9c246ad7c27e/backend/src/netdisk/client.rs)：web warmup、列表、上传、文件管理和任务查询。仓库 LICENSE 为 Apache-2.0，但 README 另有商业使用限制文字，因此这里只把接口行为用作协议参考，不引入源码或依赖。
- [同提交 PCS 双凭证](https://github.com/komorebiCarry/BaiduPCS-Rust/blob/427c5c38d8589b91c3a5fee09e5c9c246ad7c27e/backend/src/netdisk/client.rs#L935-L944)、[上传分片](https://github.com/komorebiCarry/BaiduPCS-Rust/blob/427c5c38d8589b91c3a5fee09e5c9c246ad7c27e/backend/src/netdisk/client.rs#L1698-L1727)：显式 pan 服务 BDUSS+STOKEN，superfile2/multipart 与上传 MD5；新一轮只读核查时仓库 HEAD 仍是此提交。
- [BaiduPCS-Go 固定提交 1b913181：prepare.go](https://github.com/qjfoidnh/BaiduPCS-Go/blob/1b9131817aaf8ca7dee24bc00e33ebc4c7a5cc73/baidupcs/prepare.go#L630-L657)：交叉核对 superfile2、uploadid、partseq 和非成功 HTTP 状态检查，没有引入其实现。
- [同提交 cookie_login.rs](https://github.com/komorebiCarry/BaiduPCS-Rust/blob/427c5c38d8589b91c3a5fee09e5c9c246ad7c27e/backend/src/auth/cookie_login.rs)：账户字段。
- [BaiduPCS-Py 固定提交 e81e9b65：pcs.py](https://github.com/PeterDing/BaiduPCS-Py/blob/e81e9b65c4b35fc8f7f2993a81e25e0bc24608db/baidupcs_py/baidupcs/pcs.py)：MIT 项目，交叉核对 locatedownload 签名及 native User-Agent。
- [百度开放平台文档](https://pan.baidu.com/union/doc/)：用于核对分片/创建字段；本库使用网页 cookie 而非官方 OAuth，不能据此承诺私有接口兼容性或账号权益。
- [官方创建文件字段](https://pan.baidu.com/union/doc/基础网盘服务/上传/创建文件.md)：说明 `rtype=0` 冲突失败、`is_revision=1` 会忽略命名策略，以及 create 回执的 path/size/md5/fs_id 字段。私有请求仍保留存在性判断与失败后的强校验。

运行 `dotnet run --project cloud/OverlayDisk.Cloud.Baidu.Tests -c Release`。35 项功能测试使用模拟 HTTP 服务，覆盖成功状态严格校验、分页、不可变上传、分片重试、MD5、输入 SHA-256、提交回执丢失、Range、截断、异步删除结果、域白名单、Windows DPAPI 假凭据持久化，以及缓存重启/淘汰/损坏/账户隔离/并行失败/真实子进程 kill、创建目录与子文件并发和精确请求计数。合法 JSON 即使带 `text/html` 也须通过状态/字段校验；真正 HTML 或既没有分片 MD5、也没有明确成功状态的响应仍失败。安全诊断和错误消息仅暴露固定操作标签及代码，不包含提供方原始正文、Cookie 或签名 URL。0.4 新增回执查询先于源访问、4 MiB 篡改/截断/多余字节拒绝、源内存变更与 owner 释放后的重试稳定性、不确定结果强校验、追加日志/批量 checkpoint、损坏/丢失/未完成日志，以及实际子进程被杀后已提交回执恢复测试。Windows Release 全部 35 项通过，0 警告；本轮未运行任何真实账号 API 测试或性能基准。

此前基线版本真实验证记录（2026-10-04）：`cloud-live-02` 已完成真实账号的对象上传、下载、校验和清理往返；最终 `app-cloud-worker-03` 已完成应用服务→提权 worker→NTFS→云上传→云导入→恢复盘挂载→文件哈希验证，以及正常退出和测试资源清理。预发布轮次的退出状态误报已修复；另一次准备阶段曾遇到 `code 1`，随后独立只读诊断及完整流程重测均成功，未复现该错误，因而没有把未知的 `code 1` 盲目改成自动重试错误。此处记录已有证据，不承诺接口永远兼容或所有账号均可用。私有网页接口变动、认证过期、账号风控或限速可能要求重新登录或更新后端。

403/全局限流修正后的离线回归为 41 项：在上述 35 项基础上加入 pan host-only STOKEN 节点上传、凭据隔离、未知 403 只发一次/已知认证码脱敏、虚拟时钟下的跨客户端 FIFO 频率、动态降速/降并发、完整下载正文持有额度、排队取消、在途取消、客户端释放，以及上传重试/GET 重定向统一计数。测试通过注入 HTTP handler 和虚拟时钟验证行为，不是吞吐测量；没有使用真实账号执行网络上传。旧有调用计数断言保持不变，限流不会凭空增加 API 调用。

已知对象读取能力将离线回归扩展到 44 项，增加精确 2/3 请求且 0 LIST、无效描述/取消在发送前拒绝，以及短流、多余字节、错误 Content-Length 和意外部分响应拒绝。

当前上传确认语义的定向测试入口：`dotnet run --project cloud/OverlayDisk.Cloud.Baidu.Tests -c Release -- --filter upload-ack`。只选择 11 项相关测试，覆盖 generic/prepared 上传的最终 MD5 混淆、32位不同值、MD5缺失、部分/纯成功回执均零下载；秒传零下载；矛盾字段、错误状态、无状态/HTML 拒绝；分片摘要检查；未知既有对象冲突与丢响应恢复；失败后缓存证明；4 MiB 定长 multipart 重试内容稳定。本轮不重跑完整套件，不使用真实账号。

新卷 canonical 对象大小支持 4/8/16 MiB。百度传输分片保持 4 MiB，预创建与合并传入**压缩后 wire** 的实际长度和完整分片 MD5 列表。不可压缩的 16 MiB 密文加 zstd/信封开销可能需要第 5 片，不能截掉最后一片。失败分片用同一 uploadid/partseq 和不可变缓冲做有界重试，已完成分片不因一次分片错误重新传输；耗尽后保留不确定结果恢复。每片外层 multipart 的 Content-Length 继续准确计算，普通成功合并/秒传不会下载载荷核验。

## zstd 对象传输与原始身份

当前同步层使用 `ICloudEncodedObjectStore`：先用 `CanonicalObjectDescriptor(path, 4/8/16MiB, canonicalSHA, "zstd-v1")` 查询 `TryGetEncodedReceiptAsync`；未命中才读取本地对象并创建 `PreparedObjectUpload`，然后 `PutEncodedAsync`。工厂先一次读取并核对原始 sealed 字节的 SHA，再按固定 codec 生成独占 wire 缓冲。wire 自己具有长度、SHA、整体及每 4 MiB 分片 MD5，HTTP 重试共用它。后台缓存的同一条 WAL 回执同时记录 canonical 身份和已确认 wire 身份；重启仍可在读源前命中。普通 wire SHA 回执不能自行充当 canonical 证明。账户范围、完整目录证明、日志损坏与并行变更失效规则均继续适用。

`.obj` 使用 128 字节信封和单个 zstd frame；JSON 不压缩。正常上传后只信明确成功回执与已有字段一致性，分片 MD5 出现时核对；不下载新对象。未知已存在路径、丢失合并回执仍可对 wire 读回 SHA 以排除不可变路径冲突。未成功验证的不确定结果不产生 canonical 回执。

下载使用 `ICloudBoundedObjectReader.OpenReadBoundedAsync`，根据 canonical 大小给定 wire 上界，直接 locate→GET，不查询 Head/LIST。流支持明确 Content-Length 或无长度的完整响应，都限制最大字节数；响应与全局并发配额在 EOF/释放/错误时释放。上层严格验证信封、单帧、content size、16 MiB window 上限、无 dictionary、无尾随内容，并解压至固定 canonical 大小后核对经认证的 canonical SHA。缺 codec/大小字段和旧 raw 格式明确拒绝，没有隐式兼容/迁移。

codec 使用固定 `ZstdSharp.Port 0.8.8`、level 3、content-size=1、checksum=0、nbWorkers=0；会改变 wire 字节的升级必须使用新 codec 标识。MIT 许可证和包中固定源码提交记录位于 `docs/ZSTD-LICENSE.txt`、`docs/ZSTD-PROVENANCE.md`。

本轮必要离线测试入口：`--filter object-transport`（6 组 codec 边界）、`--filter encoded-object`（4 组 provider 回执/分片/下载/恢复）、`--filter object-size`（3 组通用 4/8/16 MiB 分片接口）。这些只使用模拟 HTTP；不调用真实账户、不做吞吐基准。新的传输格式尚未作为本轮真实网盘测试结果宣称。
