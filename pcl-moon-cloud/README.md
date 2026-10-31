# PCL-MOON Cloud

Java 17+ / Spring Boot 3.5 + Gradle 8.14.4 的整合包目录服务，附中文网页管理台。独立于启动器，可部署到 Linux/Windows 云服务器。

## 启动

构建需要 JDK 17 或更高版本，运行需要 Java 17 或更高版本。固定使用 `--release 17`（class 61），在 JDK 21 上构建也不会生成仅支持 Java 21 的产物。复用已存在的 Gradle 8.14.4 可执行 `gradle test bootJar`；也提供标准 Wrapper。

Windows PowerShell：

```powershell
cd pcl-moon-cloud
.\gradlew.bat test bootJar
# 生成本机随机管理令牌。请保存到密码管理器；不要提交仓库。
$env:PCL_MOON_ADMIN_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$env:PCL_MOON_ADMIN_TOKEN # 复制到管理页登录框
java -Xms64m -Xmx256m -jar .\build\libs\pcl-moon-cloud-1.0.0.jar
```

Linux：

```sh
cd pcl-moon-cloud
sh gradlew test bootJar
export PCL_MOON_ADMIN_TOKEN="$(openssl rand -hex 32)"
printf '%s\n' "$PCL_MOON_ADMIN_TOKEN" # 保存后输入到管理网页
java -Xms64m -Xmx256m -jar build/libs/pcl-moon-cloud-1.0.0.jar
```

- 获取端口：`http://服务器:6664/index.json`（兼容 `/catalog.json`），只允许 GET/HEAD。
- 管理网页：`http://127.0.0.1:8081/`，默认只在服务器本机监听。
- 远程管理推荐 SSH 隧道：`ssh -L 8081:127.0.0.1:8081 用户@服务器`，然后打开本机 `http://127.0.0.1:8081/`。
- 未配置有效令牌会拒绝启动。令牌需 32–256 位随机字母/数字/下划线/连字符。网页仅在内存持有令牌，不使用 Cookie、浏览器存储或 URL 保存它；刷新网页需要重新登录。

## 网页操作

登录 → 新建整合包 → 填写名称/版本/下载地址等 → 保存。新建默认为草稿，勾选发布才会出现在公开目录。支持搜索、发布状态筛选、编辑、排序、删除确认、复制主/备用下载入口及公开目录地址。通过“公开地址”配置实际 HTTPS 域名；SSH 隧道或反向代理场景下自动推导的主机和端口未必是公网入口。

每条整合包包含：

| 字段 | 用途 |
| --- | --- |
| `id` | 稳定唯一标识，`[a-z0-9][a-z0-9_-]{0,63}`；创建后不可改 |
| `name`, `description`, `version`, `minecraft` | 名称、说明、整合包版本、MC 版本 |
| `coverUrl`, `icon`, `background` | 封面、图标、背景 HTTP(S) URL；公开目录中缺省图标/背景回退到封面 |
| `downloadUrl` | 主下载 URL（必填） |
| `backupUrls` | 最多 8 个有序备用下载 URL |
| `sha256`, `fileName` | 推荐填 SHA256；文件名只允许无路径的 `.zip` / `.mrpack` |
| `shareUrl`, `sharePath` | 可选网盘分享地址及内部路径说明 |
| `enabled`, `sortOrder` | 发布开关；排序值越小越靠前，相同时按 ID |

URL 必须是完整 HTTP(S) 地址，不得包含用户名、密码或片段。最多 100 条（与已有启动器解析上限兼容）。主/备用 URL 应指向同一内容和 SHA256。签名下载链接过期后需在管理台更新；不能把私密或短期签名链接当作永久地址。

本服务**托管目录与链接，不上传、存储或中转大 ZIP/MRPACK/图片**。将文件放在对象存储/CDN/Nginx 静态文件服务，网页里填写链接。断点续传由下载源支持 `Range`；本服务下载入口只发 302。客户端应根据下载失败自行按序尝试 `backupUrls`，服务不会主动探测镜像，也不会自动替客户端切换。

## 接口

获取端口无需令牌：

| 方法 | 路径 | 返回 |
| --- | --- | --- |
| GET/HEAD | `/index.json`、`/catalog.json` 或 `/api/v1/packs` | `{ "packs": [...] }`，仅已发布条目 |
| GET/HEAD | `/api/v1/packs/{id}` | 单个已发布条目 |
| GET/HEAD | `/api/v1/packs/{id}/download?mirror=0` | 302 到主地址；`mirror=1..8` 为备用地址 |
| GET/HEAD | `/healthz` | `{ "status": "ok" }` |

公开目录保留现有 `PackCatalog.Parse` 所需 `icon/background/downloadUrl/fileName/shareUrl/sharePath` 字段；旧启动器忽略扩展字段。**包下载的 `backupUrls` 与目录源备用不是同一功能，此改动不新增包下载镜像自动回退。**

启动器中打开「整合包 → 服务配置」，在预设目录源里每行填写一个 `index.json` 地址，第一项为主源，后续为备用（最多 8 个）。旧单个 `CatalogUrl` 自动迁移。网络错误、超时或目录格式错误会按序回退，不合并多个源；每个源最多等待 8 秒。全部失败保留本次运行中上次成功目录并显示错误。支持压缩、ETag/304；格式示例见 `../docs/catalog-sources.md`。

管理端口 API 必须带 `Authorization: Bearer <令牌>`，不启用跨域：

| 方法 | 路径 | 返回/行为 |
| --- | --- | --- |
| GET | `/api/admin/packs` | `{ "revision": 0, "packs": [...] }`，包含草稿；ETag 为 `"0"` |
| GET | `/api/admin/status` | 端口、总数、发布数量、目录版本 |
| POST | `/api/admin/packs` | 创建，返回完整新快照 |
| PUT | `/api/admin/packs/{id}` | 替换条目，返回完整新快照 |
| DELETE | `/api/admin/packs/{id}` | 删除，返回完整新快照 |

所有写操作必须带最新 GET 返回的 `If-Match: "revision"`；缺失为 428，旧版本为 412，重复 ID 为 409。错误格式 `{ "error": "说明" }`。网页遇到冲突刷新目录但保留编辑表单，需再次确认保存。POST/PUT 是完整条目提交，不是局部 PATCH。管理请求上限 128 KiB（同时限制 Content-Length 与 chunked）；字段长度及协议在服务端校验。

## 配置

| 环境变量 | 默认值 |
| --- | --- |
| `PCL_MOON_PUBLIC_PORT` | `6664` |
| `PCL_MOON_PUBLIC_BIND` | `0.0.0.0` |
| `PCL_MOON_ADMIN_PORT` | `8081` |
| `PCL_MOON_ADMIN_BIND` | `127.0.0.1` |
| `PCL_MOON_ADMIN_TOKEN` | 无，必填 |
| `PCL_MOON_DATA_DIR` | `./data`（相对工作目录） |

端口必须不同。应用通过实际接受请求的 socket 端口隔离路由；伪造 Host/X-Forwarded-Port 不会开放管理 API。公网端口不提供管理页面和管理接口，管理端口不提供公开目录。HTTP 由反向代理终止 TLS，不要把令牌通过公网明文 HTTP 发送。保持管理端口仅本机监听，或只在 VPN/受限 HTTPS 管理入口开放；公网防火墙不要放行 8081。

## 持久化与恢复

数据保存到 `PCL_MOON_DATA_DIR/catalog.json`。写入先生成完整内存快照及临时文件，文件 `force(true)` 后原子替换，成功后发布新快照。磁盘写失败保留旧内存数据并返回 503；文件损坏时拒绝启动，不静默清空。目录文件总大小上限为 8 MiB，超过则拒绝写入，确保已保存数据可在重启后读取。数据目录需支持原子重命名的本地文件系统。

目录被独占文件锁保护，**只支持单实例写入**，不适合多个实例共享 NFS。不要在运行时直接编辑文件。备份用复制 `catalog.json`（原子替换使单次复制看到完整版本）；恢复需先停服务、替换文件、再启动。不保证物理断电情况下父目录元数据的持久性，生产环境仍需磁盘/快照备份。管理台无历史版本功能。

## 网络与性能

- 公开目录/详情保存在不可变内存快照中，请求不读盘、不序列化、不压缩、不重新算散列。
- 更新时一次性生成紧凑 JSON、Gzip 和各自的强 ETag；支持 `Accept-Encoding` 的 q 值、`Vary: Accept-Encoding`、弱/列表条件 ETag、304、HEAD 和 Content-Length。
- 默认 `Cache-Control: public, max-age=60, stale-while-revalidate=300`，可接 CDN。若需要立刻看到更新，客户端发送 `Cache-Control: no-cache` 让 CDN 重新验证；否则中间缓存可能延迟 60 秒，允许 stale-while-revalidate 的缓存最多额外 300 秒。下架不是立即撤销已经下发的下载 URL。
- 下载仅 302 重定向，不双倍占用云服务器带宽、不把大文件装入 Java 堆；压缩只用于元数据和网页资源。
- 网页无 CDN/第三方 JS/字体依赖，静态文件也预生成 Gzip/ETag；管理 API 禁止缓存。
- Tomcat NIO，公共连接上限 2048、工作线程 64，管理连接上限 128、线程 16；连接超时 10 秒、Keep-Alive 15 秒。两个连接器隔离线程预算，但共享 JVM/进程，仍应通过网关限制请求速率和连接数。
- `-Xmx256m` 是小目录部署的起点，不是容量保证。未给出云机器/带宽前不承诺 QPS；上线后应按真实目录和并发压测。

## Linux 部署

`deploy/pcl-moon-cloud.service`、`deploy/pcl-moon.env.example`、`deploy/nginx.conf.example` 提供 systemd + HTTPS Nginx 示例。

1. 安装 Java 17 或更高版本，创建无登录 `pcl-moon` 用户；把 bootJar 复制为 `/opt/pcl-moon/pcl-moon-cloud.jar`。
2. 创建 `/var/lib/pcl-moon`，归属 pcl-moon；创建 `/etc/pcl-moon/pcl-moon.env`，填入随机令牌，权限 `600`。不要使用示例占位令牌。
3. 安装 systemd 单元，`systemctl daemon-reload && systemctl enable --now pcl-moon-cloud`。
4. 配置 Nginx 域名/证书，测试配置后重载。该示例只代理公共目录；管理仍通过 SSH 隧道。
5. 公网仅放行 443（必要时 80 用于证书/跳转）、受限 SSH；6664/8081 不直接开放。日志用 `journalctl -u pcl-moon-cloud`。

## 验证

```powershell
.\gradlew.bat test bootJar
node --check src/main/resources/admin/app.js
```

测试使用真实随机的两个 TCP 监听端口，覆盖访问隔离、令牌校验、CRUD/草稿隐藏、乐观并发、重启恢复、写入失败、损坏文件保护、输入校验、chunked 上限、Gzip/ETag/HEAD、备用下载跳转。浏览器联调运行 `node scripts/test-admin-ui.mjs`（Node 22+ 与本机 Chrome/Edge，可用 `PCL_MOON_BROWSER` 指定可执行文件），脚本自动启动隔离测试实例，结果/截图输出到 `build/ui-smoke/`，结束时关闭自己启动的进程，不访问实际下载源。

若本机系统盘空间不足，可用已有 Gradle 指定 `-g build/gradle-home`，并把 `JAVA_TOOL_OPTIONS=-Djava.io.tmpdir=<项目绝对路径>/build/tmp` 指到有空间的磁盘；不要删除其他工程缓存。
