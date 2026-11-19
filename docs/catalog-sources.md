# PCL-MOON 整合包预设目录源

## 配置入口

启动器的「整合包」网页 → 右上角「服务配置」→ **整合包预设目录源 · index.json**。

每行一个 HTTP(S) 地址：

```text
https://packs.example.com/index.json
https://backup.example.com/index.json
http://127.0.0.1:8080/index.json
```

 第一项是主源，其余按顺序备用，最多 8 项；同一地址重复填写会去重。留空表示不使用自定义目录源，改用下方的 Gitee 默认源。这里填写的是目录 JSON，不是 ZIP/MRPACK 下载地址、网盘分享页，也不是 Packwiz 的更新索引或 `modrinth.index.json`。

网络异常、HTTP 错误、单源超过 8 秒、目录 JSON 解析/验证失败都会尝试下一项；第一次成功就停止，**不合并多个目录**。有效的空 `packs` 数组是成功，不继续尝试备用。全部失败保留本次运行中上次成功的条目并显示错误；缓存不跨启动持久化。设置页显示“上次成功源”，可确认是否用了备用。取消操作会停止所有后续请求。

每个源支持 Gzip/Deflate/Brotli 自动解压、ETag/304 复用及重新验证缓存，解压后目录上限 4 MiB。按列表顺序刷新，主源恢复后优先使用主源。不要把每次访问内容不同的服务作为互相备用源。

 ## 默认源（未配置时）

 目录源留空时，启动器不把 MCL 当成「没有目录」，而是使用 Gitee Release 上的默认源：

 ```text
 https://gitee.com/canyue_1_0/mcl-index/releases/download/default/index.json
 ```

 服务配置里的「使用 Gitee 默认源」按钮把这个地址填进输入框，等价于留空；用户保存过任意目录源后，默认源不再参与请求。本地 `docs/pack-catalog/index.json` 仍是仓库内的样例，不再作为未配置时的内置获取地址。

设置存于启动器旁的 `PCL/launcher-library.json`：

```json
{
  "CatalogUrl": "https://packs.example.com/index.json",
  "QuarkServiceUrl": "",
  "CatalogUrls": [
    "https://packs.example.com/index.json",
    "https://backup.example.com/index.json"
  ]
}
```

旧配置只有 `CatalogUrl` 时会自动作为单项迁移；新配置以 `CatalogUrls` 为准（显式 `[]` 表示清除），同时保留 `CatalogUrl` 首项兼容旧版。建议通过网页操作，不在运行时直接编辑配置文件。

## index.json 格式

```json
{
  "packs": [
    {
      "id": "moon-survival",
      "name": "月光生存",
      "description": "整合包简介",
      "version": "1.0.0",
      "minecraft": "1.21.1",
      "icon": "https://cdn.example.com/icon.webp",
      "background": "https://cdn.example.com/cover.webp",
      "downloadUrl": "https://cdn.example.com/moon-survival.mrpack",
      "fileName": "moon-survival.mrpack",
      "sha256": "",
      "shareUrl": "",
      "sharePath": ""
    }
  ]
}
```

最多 100 条；名称、版本不可空；ID 需匹配 `[a-z0-9][a-z0-9_-]{0,63}` 且不可重复；下载文件名必须是不含路径的 `.zip` / `.mrpack`；SHA256 可空或为 64 位十六进制值。图片/下载/分享 URL 可以是完整 HTTP(S) 地址，也可相对于该目录 JSON 的最终地址（支持重定向后相对路径）。禁止 URL 内嵌用户名/密码，目录源本身不得含片段。

文件扩展名推荐 `index.json`，但旧 `/catalog.json` URL 仍可使用；判断的是 JSON 内容格式，不是扩展名。服务部署后用 `http://服务器:8080/index.json`，云端后台「公开访问」会生成对应 URL。

目录源备用与每个整合包的 `backupUrls` 是不同功能：前者在启动器内自动回退获取目录；后者仍是云服务公开的包下载备用信息，不在此改动中新增下载镜像切换。

## 验证

- `.NET` 测试：`dotnet test PCL.Core.Test/PCL.Core.Test.csproj --filter FullyQualifiedName~PackCatalogClientTest`。
- 云服务（Rust）：在 `pcl-moon-cloud` 内执行 `cargo test && cargo build --release`。
- 网页桥接测试：`node scripts/Test-CatalogSources.mjs`（本机 Edge/Chrome + Node 22+，可设置 `PCL_MOON_BROWSER`）。
