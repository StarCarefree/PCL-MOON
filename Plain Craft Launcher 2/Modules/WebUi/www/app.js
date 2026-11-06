"use strict";
"use strict";
/* Browse bridge. Host owns the native shell. This page never draws it and never
   posts projection/navigate/window/action messages.
   Uplink: {type:'launcher', operation, ...params}, {type:'native', page},
   {type:'mcmod', operation, ...} and {type:'platform', operation, source, requestId, ...}.
   Platform list is page 1..400 with the debounced committed query, not an IME partial.
   Detail and download use item.projectId, never the prefixed card id.
   Real HandleAsync ops used here: select(id), refresh, primary, installQuark,
   cancel, configure(catalogUrls, catalogUrl, quarkServiceUrl), authorizeQuark, checkUpdates, import.
   primary = download if not installed, Packwiz update if updateAvailable,
   launch only from the more-menu when installed and no update is pending.
   Do not send install/launch/update/login/directory — those names are not the
   browse contract (login/directory exist in C# but navigation goes through native).
   Downlink: bootstrap, browse {launcher}, error {message}, mcmod*, platformList,
   platformDetail, platformError. A legacy page snapshot is accepted only to read
   launcher; its control tree is ignored. Every platform reply must echo requestId. */

const SRE_ID = "starrail-harpy-express";

 // Shipped default MCL catalog source. Kept in sync with PackCatalogSources.DefaultUrls in PCL.Core.
 const DEFAULT_CATALOG_URLS = [
   "https://gitee.com/canyue_1_0/mcl-index/releases/download/default/index.json"
 ];
const OFFLINE_SRE = {
  id: SRE_ID,
  name: "StarRailHarpyExpress",
  description: "SRE 哈比列车 · 内置测试整合包。连接启动器后可查看实时信息并下载。",
  version: "",
  minecraft: "",
  installed: false,
  updateAvailable: false
};
const COPY = {
  zh: {
    connection: { offline: "静态预览 · 未连接本地启动器", connecting: "正在连接启动器", live: "已连接本地启动器", lost: "连接失效" },
    search: "搜索", placeholder: "名称、作者、版本或关键词", refresh: "刷新资源", services: "服务配置",
    recommend: "MCL 整合包", recommendNote: "远端 index.json 目录与内置测试包", catalog: "探索整合包",
    test: "SRE", catalogBadge: "目录整合包", offlineState: "离线 · 安装状态未知",
    notInstalled: "未安装", installed: "已安装", update: "可更新", missing: "无下载来源信息",
    download: "下载整合包", updateAction: "更新整合包", gotoGames: "返回我的游戏", busy: "任务进行中",
    cancel: "取消", more: "更多", versions: "查看版本",
    check: "检查更新", quark: "通过夸克下载", quarkAuth: "授权夸克网盘", importPack: "导入整合包", launch: "启动已安装的整合包",
    versionSettings: "版本设置", tasks: "下载任务", account: "打开账号",
    clear: "清除搜索", config: "配置目录地址", save: "保存配置", close: "关闭服务配置",
    count: n => `${n} 个条目`,
    bundled: "随包运行时", system: "系统运行时", preview: "静态预览",
    selected: (name, status) => `已选择 ${name}，${status}`,
    disconnected: "请从启动器打开此页。当前没有连接，业务按钮已禁用。",
    sent: "已发送到启动器…",
    ignored: "启动器没有开始该任务。若原生对话框正打开，请先关闭后再试。",
    selectMiss: "启动器未切换到所选条目，已取消后续操作。",
    noSnapshot: "启动器没有提供浏览数据。",
    connectTimeout: "浏览页没有连上启动器。请关闭后重新打开浏览。",
    badSnapshot: "浏览数据无法显示。",
    gameRunning: "游戏运行中。下载、更新和夸克安装已禁用，请先在原生界面关闭游戏。",
    locked: "任务进行中，条目选择已暂时锁定。",
    catalogMissing: "尚未配置额外预设源。你仍可浏览平台资源，或配置自己的目录。",
    catalogReady: "",
    emptyHost: "启动器没有返回整合包条目。",
    emptySearch: "没有匹配的整合包。",
    recommendOffline: "连接启动器后浏览更多预设。",
    recommendNone: "暂无其他预设，可在下方浏览资源。",
    recommendSearch: "没有匹配的本地预设。",
    hintOffline: "连接启动器后可下载和导入整合包。",
    hintLive: "下载与游戏管理由本地启动器完成。",
    hintQuark: "可使用夸克下载或导入已下载的整合包。",
    launchTitle: "仅在已安装且没有待更新时调用启动器 primary。有更新时请使用主按钮更新。",
    urlInvalid: "请输入不含账号密码的 HTTP 或 HTTPS 地址。",
    urlRemote: "该地址使用未加密 HTTP，且不是本机地址。启动器会照常保存，请确认信任来源。",
    urlLocal: "本机 HTTP 地址可以保存。",
    urlHttps: "HTTPS 地址可以保存。",
    urlEmpty: "留空会清除已保存的地址。夸克留空则使用官方 API。",
    quarkAuthOn: "已授权。通过夸克下载时只会转存目标整合包，不会转存整个分享。",
    quarkAuthOff: "未授权。匿名直链会被拒绝；授权后才转存这一个文件并下载。",
    catalogListInvalid: "每行一个完整 HTTP(S) 目录地址，最多 8 个，每个不超过 2048 字符；不能含账号密码或片段。",
    catalogUsing: "上次成功源：", catalogNoSource: "尚未从目录源成功获取预设。",
     catalogDefaultUse: "使用 Gitee 默认源",
     catalogDefaultOff: url => `已配置目录源；清空后回到 Gitee 默认源：${url}`,
     catalogDefaultOn: url => `未配置目录源，启动器改用 Gitee 默认源：${url}`,
     configOffline: "静态预览不能保存。连接本地启动器后再配置。",
     configWait: "配置已发送，等待启动器确认。",
     configOk: "配置已由启动器确认。",
     configFail: "保存未确认：",
     phaseOffline: "未连接 · 下载未发送",
     phaseWait: "等待启动器提供浏览数据",
     phaseReady: "尚未安装 · 下载会交给本地启动器",
     phaseInstalled: "已安装 · 开始游戏在原生界面",
     phaseUpdate: "发现可更新版本",
     servicesTitle: "服务配置",
     pageTitle: "资源目录",
     eyebrow: "PCL-MOON · DISCOVER"
   },
   en: {
     connection: { offline: "Static preview · launcher not connected", connecting: "Connecting to launcher", live: "Launcher connected", lost: "Connection lost" },
     search: "Search", placeholder: "Name, version, or description", refresh: "Refresh", services: "Services",
    recommend: "MCL modpacks", recommendNote: "Remote index.json and the built-in test pack", catalog: "Discover packs",
    test: "SRE", catalogBadge: "Catalog pack", offlineState: "Offline · install state unknown",
    notInstalled: "Not installed", installed: "Installed", update: "Update available", missing: "No source metadata",
    download: "Download pack", updateAction: "Update pack", gotoGames: "Back to my games", busy: "Task in progress",
    cancel: "Cancel", more: "More", versions: "Versions",
    check: "Check updates", quark: "Download via Quark", quarkAuth: "Authorize Quark Drive", importPack: "Import pack", launch: "Launch installed pack",
    versionSettings: "Version settings", tasks: "Download tasks", account: "Open account",
    clear: "Clear search", config: "Configure catalog", save: "Save", close: "Close services",
    count: n => `${n} ${n === 1 ? "entry" : "entries"}`,
    bundled: "Bundled runtime", system: "System runtime", preview: "Static preview",
    selected: (name, status) => `Selected ${name}, ${status}`,
    disconnected: "Open this page from the launcher. Business actions are disabled.",
    sent: "Sent to the launcher…",
    ignored: "The launcher did not start the task. Close any native dialog and try again.",
    selectMiss: "The launcher did not select that entry. The follow-up action was cancelled.",
    noSnapshot: "The launcher did not provide browse data.",
    connectTimeout: "Browse did not connect to the launcher. Close it and open Browse again.",
    badSnapshot: "Browse data could not be shown.",
    gameRunning: "The game is running. Download, update, and Quark install are disabled.",
    locked: "A task is running, so selection is locked.",
    catalogMissing: "No extra preset source configured. Browse platform resources below, or add your own catalog.",
    catalogReady: "",
    emptyHost: "The launcher returned no packs.",
    emptySearch: "No matching packs.",
    recommendOffline: "Offline preview only includes the test pack. No invented recommendations.",
    recommendNone: "No other catalog entries yet. Configure a catalog URL and refresh.",
    recommendSearch: "No recommended entries match the search.",
    hintOffline: "Offline static preview. The host is not connected, and business buttons stay disabled.",
    hintLive: "Game directory, start, and version list stay in the native UI. This page only browses packs and forwards download, update, Quark, and import.",
    hintQuark: "Quark download uses the selected entry and runs in the launcher. Failures are real host errors.",
    launchTitle: "Sends launcher primary only when installed and no update is pending.",
    urlInvalid: "Enter an HTTP or HTTPS URL without credentials.",
    urlRemote: "This is a non-local HTTP URL. The launcher will still save it if you trust the source.",
    urlLocal: "Local HTTP is accepted.",
    urlHttps: "HTTPS is accepted.",
    urlEmpty: "Empty clears the saved URL. An empty Quark URL uses the official API.",
    quarkAuthOn: "Authorized. Quark download saves only the selected pack, not the whole share.",
    quarkAuthOff: "Not authorized. Anonymous links are rejected; authorize to save and download that one file.",
    catalogListInvalid: "One absolute HTTP(S) catalog URL per line; up to 8 URLs, 2048 characters each, without credentials or fragments.",
    catalogUsing: "Last successful source: ", catalogNoSource: "No successful catalog fetch yet.",
     catalogDefaultUse: "Use the Gitee default",
     catalogDefaultOff: url => `Catalog configured. Clearing it returns to the Gitee default: ${url}`,
     catalogDefaultOn: url => `No catalog configured. The launcher uses the Gitee default: ${url}`,
    configOffline: "Static preview cannot save. Connect the launcher first.",
    configWait: "Configuration sent. Waiting for the launcher to confirm.",
    configOk: "The launcher confirmed the configuration.",
    configFail: "Save was not confirmed: ",
    phaseOffline: "Not connected · download was not sent",
    phaseWait: "Waiting for browse data",
    phaseReady: "Not installed · download is handled by the launcher",
    phaseInstalled: "Installed · start the game from the native UI",
    phaseUpdate: "An update is available",
    servicesTitle: "Services",
    pageTitle: "Resource catalog",
    eyebrow: "PCL-MOON · DISCOVER"
  }
};

const state = {
  presented: true,
  link: "offline",
  language: "zh",
  version: "",
  branch: "",
  runtimeVersion: "",
  bundledRuntime: false,
  launcher: null,
  confirmedSelected: "",
  optimisticSelected: "",
  queued: null,
  pending: null,
  localNote: "",
  hostError: "",
  connectDeadline: 0,
  configSent: null,
  configNote: "",
  lastSelectionKey: ""
};
// List caches are isolated by site filters + page, with requestId guarding retries
// and same-page races. Detail caches remain wiki-id scoped, never launcher state.
// Platform caches are keyed by source/query/version/loader/sort/page.
const COMMON_MC = ["1.21.1", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2"];
const SITE_LOADERS = ["Forge", "Fabric", "Quilt", "NeoForge", "LiteLoader", "Rift", "数据包", "行为包"];
const PLATFORM_LOADERS = ["Forge", "Fabric", "Quilt", "NeoForge"];
const PLATFORM_SORTS = ["relevance", "downloads", "updated", "newest"];
const MCMOD_SORTS = ["default", "createtime", "lastedittime"];
const SITE_CATEGORIES = ["科技", "魔法", "冒险", "建筑", "地图", "任务", "硬核", "休闲", "大型", "轻量", "剧情", "水槽", "空岛", "PvP", "国创"];
const CATEGORY_LABELS = {
  adventure: "冒险", challenging: "硬核", combat: "战斗", "combat-pvp": "战斗 / PvP", "combat/pvp": "战斗 / PvP",
  cursed: "诅咒", decoration: "装饰", economy: "经济", equipment: "装备", exploration: "探索",
  food: "食物", "game-mechanics": "游戏机制", "game mechanics": "游戏机制", library: "库", magic: "魔法",
  management: "管理", minigame: "小游戏", "mini-game": "小游戏", mobs: "生物", multiplayer: "多人",
  optimization: "优化", quests: "任务", social: "社交", storage: "存储", technology: "科技",
  transportation: "交通", utility: "实用", worldgen: "世界生成", "world-gen": "世界生成", "world generation": "世界生成",
  kitchen: "厨房", "kitchen-sink": "水槽包", "kitchen sink": "水槽包", lightweight: "轻量",
  "small / light": "轻量", "small-light": "轻量", "extra-large": "超大型", extensive: "大型",
  "no-added-progression": "无额外进度", "added-progression": "额外进度", "added progression": "额外进度",
  vanilla: "原版向", "vanilla+": "原版增强", "vanilla-plus": "原版增强", modded: "模组向",
  realistic: "写实", fantasy: "奇幻", "semi-realistic": "半写实", cartoon: "卡通",
  atmosphere: "氛围", bloom: "泛光", cartoonish: "卡通", colored: "彩色", "colored-lighting": "彩色光照",
  "path-tracing": "路径追踪", pbr: "PBR", "reflections": "反射", shadows: "阴影",
  themed: "主题", modern: "现代", medieval: "中世纪", steampunk: "蒸汽朋克",
  "photo-realistic": "照片级", photorealistic: "照片级", simplistic: "简约", traditional: "传统",
  fonts: "字体", gui: "界面", locale: "语言", models: "模型", "mod-support": "模组支持",
  "core-shaders": "核心着色器", tweaks: "微调", misc: "杂项",
  survival: "生存", creative: "创造", adventuremap: "冒险地图", parkour: "跑酷", puzzle: "解谜",
  pvp: "PvP", skyblock: "空岛", "modded-world": "模组世界",
  datapack: "数据包", mod: "模组", modpack: "整合包", plugin: "插件", resourcepack: "材质包", shader: "光影"
};
function categoryLabel(value) {
  const name = String(value ?? "").trim();
  if (!name) return "";
  const known = CATEGORY_LABELS[name.toLowerCase()];
  return known && known !== name ? `${known} · ${name}` : name;
}
const REQUEST_TIMEOUT = 45000;
const PLATFORM_TIMEOUT = 60000;
const SEARCH_DEBOUNCE = 350;
const PLATFORM_PAGE_MAX = 400;
const DISCOVERY_AUTO_MS = 6000;
const browse = { source: "mcmod", sources: new Set(["mcl", "curseforge", "mcmod"]), resourceType: "modpack", feature: 0, featureKey: "" };
const catalog = {
  include: true, page: 1, totalPages: 0, sequence: 0, retained: new Map(),
  pages: new Map(), listPending: new Map(), listErrors: new Map(),
  requests: new Map(), latest: new Map(),
  details: new Map(), detailPending: new Map(), detailErrors: new Map(),
  downloads: new Map(), viewedId: "", detailId: "", returnFocus: null
};
const quark = { shareId: "", url: "", name: "", wikiId: 0, presetId: "", path: "", entries: [], pending: false, error: "", note: "", truncated: false, sequence: 0, requestId: "", downloadRequestId: "", openingDownload: false, returnFocus: null, xyeUrl: "", xyeVersions: [], xyeTruncated: false };
const platform = {
  page: 1, totalPages: 0, query: "", sequence: 0, searchTimer: 0,
  pages: new Map(), retained: new Map(), listPending: new Map(), listErrors: new Map(),
  requests: new Map(), latest: new Map(), details: new Map(),
  detailPending: new Map(), detailErrors: new Map(), detailLatest: new Map(),
  downloads: new Map(), downloadLatest: new Map()
};
const platformPositions = { curseforge: { page: 1, totalPages: 0 }, modrinth: { page: 1, totalPages: 0 } };
const mcl = { requestedKey: "" };
const association = { active: null, timer: 0, pausedKey: "", error: "", sequence: 0 };
function sourceEnabled(source) { return browse.sources.has(source); }
function selectedPlatforms() { return ["curseforge", "modrinth"].filter(sourceEnabled); }
function primaryPlatformSource() { return selectedPlatforms().includes(browse.source) ? browse.source : selectedPlatforms()[0] || "curseforge"; }
function platformPosition(source = primaryPlatformSource()) { return platformPositions[source]; }
function resetPlatformPages() { for (const position of Object.values(platformPositions)) { position.page = 1; position.totalPages = 0; } }
Object.defineProperties(platform, {
  page: { get: () => platformPosition().page, set: value => { platformPosition().page = value; } },
  totalPages: { get: () => platformPosition().totalPages, set: value => { platformPosition().totalPages = value; } }
});
 const categoryState = { items: new Map(), pending: new Map(), errors: new Map(), requests: new Map(), sequence: 0 };
const translation = { page: false, text: new Map(), pending: new Map(), missing: new Set(), errors: new Map(), requests: new Map(), sequence: 0, autoDetail: "" };
const RESOURCE_TYPES = { modpack: "整合包", datapack: "数据包", world: "地图", shader: "光影包", resourcepack: "材质包", plugin: "服务器插件", server: "服务器" };
const MCLISTS = "https://www.mclists.cn/";
const servers = { items: [], error: "", loading: false, selected: null, includeMclists: false };
function resourceTypeOf(item) { return item?.resourceType || (/^mcmod-datapack-/.test(item?.id || "") ? "datapack" : "modpack"); }
function resourcePayload(type = browse.resourceType) { return type === "modpack" ? {} : { resourceType: type }; }
function resourceLabel(type = browse.resourceType) { return RESOURCE_TYPES[type] || "资源"; }
function supportsResource(source, type = browse.resourceType) { return type !== "server" && !!RESOURCE_TYPES[type] && (source === "curseforge" && type !== "plugin" || source === "modrinth" && type !== "world" || source === "mcmod" && ["modpack", "datapack"].includes(type)); }
function mcKey(item, type = resourceTypeOf(item)) { const id = typeof item === "number" ? item : wikiId(item); return type === "modpack" ? id : `${type}:${id}`; }
function selectResourceType(type) {
  if (!RESOURCE_TYPES[type] || browse.resourceType === type) return;
  clearTimeout(platform.searchTimer); platform.searchTimer = 0;
  if (ui["pack-detail"].open) ui["pack-detail"].close();
  browse.resourceType = type;
  catalog.viewedId = ""; catalog.page = 1; catalog.totalPages = 0;
  resetPlatformPages();
  ui["filter-loader"].value = ""; ui["filter-category"].value = "";
  if (!state.composing) platform.query = ui.search.value.trim();
  render(); ensureBrowse(); renderServers();
}
function renderResourceTypes() {
  for (const source of ["curseforge", "modrinth", "mcmod"]) {
    const input = ui[source === "mcmod" ? "include-mcmod" : `source-${source}`];
    const label = input.closest("label");
    label.hidden = !supportsResource(source);
  }
  const mclists = ui["source-mclists"]?.closest("label");
  if (mclists) mclists.hidden = browse.resourceType !== "server";
  for (const type of Object.keys(RESOURCE_TYPES)) ui[`type-${type}`].setAttribute("aria-selected", String(type === browse.resourceType));
  const serverPage = browse.resourceType === "server";
  document.querySelector("section.catalog").hidden = serverPage;
  ui.servers.hidden = !serverPage;
  if (serverPage) document.querySelector(".stage").hidden = true;
  ui.filters.hidden = serverPage;
  ui["filter-loader"].disabled = browse.resourceType !== "modpack";
  ui["filter-category"].disabled = categoryOptions().length === 0;
  setText("catalog-title", resourceLabel() + "列表");
  if (catalog.include && !supportsResource("mcmod")) {
    ui["mcmod-status"].hidden = false; ui["mcmod-status"].dataset.state = "unsupported";
    ui["mcmod-status"].setAttribute("aria-busy", "false");
    setText("mcmod-message", "MC百科没有此类资源的独立索引，请切换到 CurseForge 或 Modrinth。");
    ui["mcmod-retry"].hidden = true;
  }
}
const ui = Object.fromEntries([...document.querySelectorAll("[id]")].map(node => [node.id, node]));
const text = () => COPY[state.language] || COPY.en;

function connected() { return !!window.chrome?.webview; }
function post(message) {
  try {
    if (!connected()) throw new Error("missing");
    window.chrome.webview.postMessage(message);
    return true;
  } catch {
    if (state.link !== "offline") state.link = "lost";
    state.hostError = text().disconnected;
    render();
    return false;
  }
}
function postLauncher(operation, values = {}) {
  return post({ type: "launcher", operation, ...values });
}
function postNative(page) { return post({ type: "native", page }); }
function setText(id, value) {
  const node = ui[id];
  const next = String(value ?? "");
  if (node.textContent !== next) node.textContent = next;
}
function imageUrl(value) {
  if (typeof value !== "string") return "";
  if (/^data:image\/(?:png|jpe?g|gif|webp|bmp|avif|svg\+xml|x-icon|vnd\.microsoft\.icon)[;,]/i.test(value)) return value;
  try {
    const url = new URL(value);
    return ["https:", "http:"].includes(url.protocol) && !url.username && !url.password ? url.href : "";
  } catch { return ""; }
}
function setImage(node, value) {
  const source = imageUrl(value);
  if (node.dataset.source === source) return;
  node.dataset.source = source;
  node.hidden = !source;
  node.referrerPolicy = "no-referrer";
  node.onerror = () => { node.hidden = true; };
  if (source) node.src = source;
  else node.removeAttribute("src");
}
function classifyUrl(value) {
  if (!value) return { ok: true, empty: true };
  let url;
  try { url = new URL(value); } catch { return { ok: false }; }
  if (url.username || url.password) return { ok: false };
  if (url.protocol === "https:") return { ok: true, https: true };
  if (url.protocol !== "http:") return { ok: false };
  const host = url.hostname.replace(/^\[|\]$/g, "").toLowerCase();
  const local = host === "localhost" || host.endsWith(".localhost") || host === "::1" || host.startsWith("127.");
  return { ok: true, localHttp: local, remoteHttp: !local };
}
function packs() {
  if (browse.resourceType !== "modpack" || !sourceEnabled("mcl")) return [];
  if (state.link !== "live" || !state.launcher) return [];
  return (Array.isArray(state.launcher.presets) ? state.launcher.presets : [])
    .filter(item => item && typeof item.id === "string" && item.id.length > 0)
    .map(item => ({ ...item, source: "mcl", resourceType: "modpack" }));
}
function queryText() { return ui.search.value.trim().toLocaleLowerCase(); }
function values(value) {
  return (Array.isArray(value) ? value : value ? [value] : [])
    .map(part => typeof part === "string" || typeof part === "number" ? String(part) : part?.name || "")
    .filter(Boolean);
}
function mcVersions(item) { return values(item?.minecraftVersions?.length ? item.minecraftVersions : item?.minecraft); }
// A span is a compact summary, not a promise of support for every intermediate release.
function versionSummary(item) {
  const versions = [...new Set(mcVersions(item))];
  const releases = versions.filter(value => /^\d+\.\d+(?:\.\d+)?$/.test(value))
    .sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
  const other = versions.filter(value => !releases.includes(value));
  const range = releases.length > 1 ? `${releases[0]}–${releases.at(-1)}` : releases[0] || "";
  const preview = other.length > 2 ? `${other[0]} 等 ${other.length} 个预览/其他版本` : other.join(" / ");
  return [range, preview].filter(Boolean).join(" · ");
}
function versionTitle(item) { return [...new Set(mcVersions(item))].join(" / "); }
function loaders(item) { return values(item?.loaders?.length ? item.loaders : item?.loader); }
function authors(item) { return values(item?.authors?.length ? item.authors : item?.author); }
function displayVersion(item) { return typeof item?.version === "string" && item.version ? item.version : ""; }
function countOf(value) {
  const count = typeof value === "number" ? value : typeof value === "string" && /^\d+$/.test(value) ? Number(value) : NaN;
  return Number.isSafeInteger(count) && count > 0 ? count : 0;
}
function compactCount(count) {
  if (count >= 100000000) return `${(count / 100000000).toFixed(count >= 1000000000 ? 0 : 1)}亿`;
  if (count >= 10000) return `${(count / 10000).toFixed(count >= 100000 ? 0 : 1)}万`;
  return count.toLocaleString("zh-CN");
}
function statLabel(item) {
  const views = countOf(item?.views);
  if (isMcmod(item)) return views ? `${compactCount(views)} 浏览` : "";
  const downloads = countOf(item?.downloadCount);
  return downloads ? `${compactCount(downloads)} 下载` : "";
}
function matches(item, query) {
  return !query || [item.name, item.englishName, item.id, item.description, displayVersion(item), ...mcVersions(item), ...loaders(item), ...authors(item), ...values(item.categories), item.installedVersion, item.updateVersion]
    .some(part => String(part || "").toLocaleLowerCase().includes(query));
}
function matchesFilters(item) {
  // Platform and MC百科 already applied their own filters. Missing list metadata must still render.
  if (isPlatform(item)) return true;
  if (isMcmod(item)) return matches(item, queryText());
  const category = item?.source === "mcl" ? null : SITE_CATEGORIES[Number(ui["filter-category"].value) - 1];
  return matches(item, queryText()) && (!ui["filter-mc"].value || mcVersions(item).includes(ui["filter-mc"].value))
    && (!ui["filter-loader"].value || loaders(item).includes(ui["filter-loader"].value))
    && (!category || values(item.categories).includes(category));
}
function selectedId() {
  if (state.link !== "live") return SRE_ID;
  return state.optimisticSelected || state.confirmedSelected || "";
}
function selectedPack(list = packs()) {
  const id = selectedId();
  return list.find(item => item.id === id) || list[0] || null;
}
const DISCOVERY_LIMIT = 8;
function discoveryPool(items = catalogItems().filter(matchesFilters)) {
  const preferred = items.filter(item => item.source === browse.source && (isMcmod(item) || isPlatform(item)));
  const remote = preferred.length ? preferred : items.filter(item => isMcmod(item) || isPlatform(item));
  const pool = browse.source !== "mcl" && remote.length ? remote : items.filter(item => matchesFilters(item));
  return pool.slice(0, DISCOVERY_LIMIT);
}
function featuredPack(items = catalogItems().filter(matchesFilters)) {
  const pool = discoveryPool(items);
  if (!pool.length) return null;
  const key = pool.map(item => item.id).join("\n");
  if (browse.featureKey !== key) { browse.featureKey = key; browse.feature = 0; }
  browse.feature = Math.min(Math.max(0, browse.feature), pool.length - 1);
  return pool[browse.feature];
}
function isSre(item) { return item?.id === SRE_ID; }
function packArt(item, field) {
  const value = item?.[field];
  return typeof value === "string" && value ? value : "";
}
function statusLabel(item) {
  if (isMcmod(item)) return "MC百科";
  if (isPlatform(item)) return sourceName(item.source);
  const t = text();
  if (!item || item.offline || state.link !== "live") return t.offlineState;
  if (item.updateAvailable) return t.update;
  if (item.installed) return t.installed;
  return t.notInstalled;
}
function taskOf() { return state.launcher?.task || {}; }
function taskForScope(scope) { const task = taskOf(); return (task.scope || "library") === scope ? task : {}; }
function busy() { return state.link === "live" && !!taskOf().busy; }
function gameRunning() { return state.link === "live" && !!taskOf().gameRunning; }
function mutatingLocked() { return state.link !== "live" || busy() || !!state.pending || gameRunning(); }
function cleanUrls(value) {
  return Array.isArray(value) ? value.filter(url => typeof url === "string" && url.trim()).map(url => url.trim()) : [];
}
function catalogUrls(config = state.launcher?.config || {}) {
  return Array.isArray(config.catalogUrls) ? cleanUrls(config.catalogUrls)
    : config.catalogUrl ? [String(config.catalogUrl)] : [];
}
function catalogDefaultUrls(config = state.launcher?.config || {}) {
  const shipped = cleanUrls(config.catalogDefaultUrls);
  return shipped.length ? shipped : DEFAULT_CATALOG_URLS;
}
// The shipped default only applies when the host says so. A snapshot without
// catalogDefaultActive (static preview, test fixture) still means "not configured".
function catalogDefaultActive(config = state.launcher?.config || {}) {
  return config.catalogDefaultActive === true && !catalogUrls(config).length;
}
function effectiveCatalogUrls(config = state.launcher?.config || {}) {
  return catalogDefaultActive(config) ? catalogDefaultUrls(config) : catalogUrls(config);
}
function catalogUrl() { return effectiveCatalogUrls()[0] || ""; }
function renderCatalogDefault() {
  const defaults = catalogDefaultUrls(), t = text();
  ui["catalog-default"].textContent = t.catalogDefaultUse;
  setText("catalog-default-status", catalogDefaultActive() ? t.catalogDefaultOn(defaults[0]) : t.catalogDefaultOff(defaults[0]));
}
function parseCatalogUrls(value) {
  const urls = [];
  for (const line of value.split(/\r?\n/).map(url => url.trim()).filter(Boolean)) {
    if (line.length > 2048 || !/^https?:\/\//i.test(line) || !classifyUrl(line).ok || line.includes("#")) throw new Error(text().catalogListInvalid);
    const normalized = new URL(line).href;
    if (normalized.length > 2048) throw new Error(text().catalogListInvalid);
    if (!urls.includes(normalized)) urls.push(normalized);
    if (urls.length > 8) throw new Error(text().catalogListInvalid);
  }
  return urls;
}
function buttonLabel(node, label) {
  const svg = node.querySelector("svg");
  if (!svg) { node.textContent = label; return; }
  node.replaceChildren(svg, document.createTextNode(label));
}

function syncButtons(parent, items, className, activate) {
  const previous = new Map([...parent.querySelectorAll("[data-pack-id]")].map(node => [node.dataset.packId, node]));
  const keep = new Set();
  items.forEach((item, index) => {
    const id = String(item.id);
    keep.add(id);
    let node = previous.get(id);
    if (!node) {
      node = document.createElement("div");
      node.className = className;
      node.dataset.packId = id;
      node.classList.add("enter");
      const open = document.createElement("button");
      open.type = "button";
      open.className = className === "card" ? "card-open" : "rec-open";
      const art = document.createElement("span");
      art.className = className === "card" ? "card-art" : "thumb";
      art.setAttribute("aria-hidden", "true");
      const image = document.createElement("img");
      image.alt = "";
      image.hidden = true;
      art.append(image);
      const copy = document.createElement("span");
      copy.className = className === "card" ? "card-body" : "rec-copy";
      const title = document.createElement("strong");
      const detail = document.createElement("small");
      const stat = document.createElement("small");
      stat.className = "card-stat";
      const pill = document.createElement("span");
      pill.className = "pill";
      const summary = document.createElement("span");
      summary.className = "card-summary";
      if (className === "card") copy.append(title, detail, stat, summary, pill);
      // 继续发现列表不显示来源标签
      else copy.append(title, detail);
      open.append(art, copy);
      node.append(open);
      node._parts = { open, image, title, detail, stat, summary, pill };
      open.addEventListener("click", () => activate(node.dataset.packId));
    }
    const parts = node._parts;
    const featured = parent === ui["recommend-list"] && item.id === featuredPack()?.id;
    // 网格卡片不标记选中项，只保留悬停效果
    const selected = featured || (parent !== ui.cards && item.id === (catalog.viewedId || selectedId()));
    node.classList.toggle("selected", selected);
    node.classList.toggle("pinned", false);
    node.setAttribute("aria-pressed", String(selected));
    node.setAttribute("aria-current", featured ? "true" : "false");
    node.tabIndex = 0;
    parts.title.textContent = item.name || id;
    const version = [mcVersions(item).length ? `MC ${versionSummary(item)}` : "MC 版本未提供", loaders(item).join(" / "), displayVersion(item)].filter(Boolean).join(" · ");
    parts.detail.textContent = version;
    parts.detail.title = versionTitle(item);
    const stat = className === "card" ? statLabel(item) : "";
    if (parts.stat) {
      parts.stat.hidden = !stat;
      parts.stat.textContent = stat;
    }
    parts.summary.textContent = translatedText(item) || item.description || "暂无简介，点击查看详情。";
    parts.pill.textContent = isMcmod(item) ? "MC百科 · 查看详情" : isPlatform(item) ? `${sourceName(item.source)} · 查看详情` : `MCL · ${statusLabel(item)}`;
    parts.pill.className = `pill${item.updateAvailable ? " update" : item.installed ? " installed" : item.offline ? " offline" : ""}`;
    const label = `${item.name || id}，${statusLabel(item)}，查看详情`;
    node.setAttribute("aria-label", label);
    node.title = label;
    node.disabled = false;
    setImage(parts.image, packArt(item, "icon"));
    syncCardTranslate(node, item);
    if (parent.children[index] !== node) parent.insertBefore(node, parent.children[index] || null);
  });
  for (const [id, node] of previous) if (!keep.has(id)) node.remove();
   const buttons = [...parent.querySelectorAll("[data-pack-id]")];
   const activeInside = buttons.includes(document.activeElement);
   const preferred = (activeInside ? document.activeElement : buttons.find(node => node.dataset.packId === (catalog.viewedId || selectedId()))) || buttons[0];
   buttons.forEach(node => { node.tabIndex = node === preferred ? 0 : -1; });
 }
 
 function choose(id) {
   if (state.link !== "live" || busy()) return;
   if (!packs().some(item => item.id === id && !item.offline)) return;
   state.queued = null;
   state.localNote = "";
   state.noteUntil = 0;
   state.optimisticAt = Date.now();
   state.optimisticSelected = id;
   render();
   postLauncher("select", { id });
 }
 function enqueue(operation) {
   const pack = selectedPack();
   if (!pack || pack.offline || isMcmod(pack) || isPlatform(pack) || mutatingLocked()) return false;
   if (state.confirmedSelected !== pack.id) {
     state.queued = { id: pack.id, operation, previous: state.confirmedSelected, at: Date.now() };
     state.localNote = text().sent;
     state.optimisticAt = Date.now();
     state.optimisticSelected = pack.id;
     render();
     return postLauncher("select", { id: pack.id });
   }
   markPending(operation);
   render();
   return postLauncher(operation);
 }
 function markPending(operation) {
   state.pending = { op: operation, at: Date.now(), phase: taskOf().phase || "" };
   state.localNote = text().sent;
 }
function closeMenu(focusToggle = false) {
  ui["more-menu"].hidden = true;
  ui.more.setAttribute("aria-expanded", "false");
  if (focusToggle) ui.more.focus();
}
function openMenu() {
  if (ui.more.disabled) return;
  ui["more-menu"].hidden = false;
  ui.more.setAttribute("aria-expanded", "true");
  ui["more-menu"].querySelector("button:not(:disabled)")?.focus();
}
function openServices() {
  closeMenu();
  const config = state.launcher?.config || {};
  ui["catalog-url"].value = catalogUrls(config).join("\n");
  ui["quark-url"].value = config.quarkServiceUrl || "";
  ui["catalog-url"].setCustomValidity("");
  renderCatalogDefault();
  ui["quark-url"].setCustomValidity("");
  state.configNote = state.link === "live" ? text().urlEmpty : text().configOffline;
  updateUrlWarning();
  setText("config-status", state.configNote);
  ui["save-services"].disabled = state.link !== "live";
  ui.services.showModal();
  ui["catalog-url"].focus();
}
function updateUrlWarning() {
  const t = text();
  const notes = [];
  try { parseCatalogUrls(ui["catalog-url"].value); } catch (error) { notes.push(error.message); }
  for (const value of [...ui["catalog-url"].value.split(/\r?\n/).map(url => url.trim()).filter(Boolean), ui["quark-url"].value.trim()]) {
    const kind = classifyUrl(value);
    if (!kind.ok) notes.push(t.urlInvalid);
    else if (kind.remoteHttp) notes.push(t.urlRemote);
    else if (kind.localHttp) notes.push(t.urlLocal);
    else if (kind.https) notes.push(t.urlHttps);
  }
  setText("url-warning", [...new Set(notes)].join(" "));
}

function render() {
  const t = text();
  document.body.dataset.link = state.link;
  document.documentElement.lang = state.language === "zh" ? "zh-CN" : "en";
   document.title = "PCL-MOON";
  setText("connection-text", t.connection[state.link] || t.connection.offline);
  buttonLabel(ui.refresh, t.refresh);
  buttonLabel(ui["open-services"], t.services);
  ui.search.placeholder = t.placeholder;
  ui.search.labels[0] && (ui.search.labels[0].firstChild.textContent = t.search);
  setText("recommend-title", t.recommend);
  setText("recommend-note", t.recommendNote);
  setText("catalog-title", t.catalog);
  ui.more.textContent = t.more;
  ui.cancel.textContent = t.cancel;
  ui["goto-versions"].textContent = t.versions;
  ui["check-updates"].textContent = t.check;
  ui["install-quark"].textContent = t.quark;
  if (ui["authorize-quark"]) {
    ui["authorize-quark"].textContent = t.quarkAuth;
    ui["authorize-quark"].disabled = state.link !== "live" || busy();
  }
  ui["import-pack"].textContent = t.importPack;
  ui["launch-installed"].textContent = t.launch;
  ui["launch-installed"].title = t.launchTitle;
  ui["goto-version-settings"].textContent = t.versionSettings;
  ui["goto-tasks"].textContent = t.tasks;
  ui["goto-games"].textContent = t.gotoGames;
  ui["goto-account"].textContent = t.account;
  ui["clear-search"].textContent = t.clear;
   ui["empty-config"].textContent = t.config;
   ui["notice-config"].textContent = t.config;
  ui["save-services"].textContent = t.save;
  ui["close-services"].setAttribute("aria-label", t.close);
  setText("services-title", t.servicesTitle);
  const source = state.launcher?.config?.catalogSource;
  setText("catalog-source-status", source ? t.catalogUsing + source : t.catalogNoSource);
  renderCatalogDefault();
  setText("quark-auth-status", state.launcher?.config?.quarkAuthorized ? t.quarkAuthOn : t.quarkAuthOff);
  document.querySelector(".eyebrow").textContent = t.eyebrow;
  document.querySelector(".title-block h1").textContent = t.pageTitle;

  const list = packs();
  const query = queryText();
  const all = catalogItems();
  syncFilterOptions(all);
  const visible = all.filter(matchesFilters);
  const selected = featuredPack(visible);
  const pool = discoveryPool(visible);
  const detailFeatured = !!selected && (isMcmod(selected) || isPlatform(selected) || selected.id !== selectedPack(list)?.id);
  const others = pool;
  document.querySelector(".stage").hidden = !selected;
  document.querySelector(".discovery-title").hidden = !selected;
  setText("discovery-note", selected ? `${sourceName(selected.source)} · 当前来源与筛选中的条目` : "");
  setText("discovery-index", pool.length ? `${browse.feature + 1} / ${pool.length}` : "");
  ui["discovery-previous"].disabled = !pool.length || browse.feature <= 0;
  ui["discovery-next"].disabled = !pool.length || browse.feature >= pool.length - 1;
  ui["discovery-previous"].hidden = pool.length < 2;
  ui["discovery-next"].hidden = pool.length < 2;
  ui["discovery-index"].hidden = pool.length < 2;
  scheduleDiscovery();
  setText("recommend-title", "继续发现");
  setText("recommend-note", "来自当前所选来源");
  const task = { ...taskOf(), error: taskForScope("library").error, phase: taskForScope("library").phase };
  const isBusy = busy();
  const running = gameRunning();
  const live = state.link === "live";
  const lost = state.link === "lost";
  const nativeOk = connected() && state.link !== "offline" && state.link !== "lost";

  ui["pack-badge"].textContent = selected ? `${sourceName(selected.source)} · ${resourceLabel(resourceTypeOf(selected))}` : t.catalogBadge;
  ui["pack-badge"].classList.toggle("catalog", !!selected);
  setText("pack-state", selected ? statusLabel(selected) : t.offlineState);
  ui["pack-state"].className = `pill${selected?.updateAvailable ? " update" : selected?.installed ? " installed" : !live ? " offline" : ""}`;
  setText("pack-name", selected?.name || "StarRailHarpyExpress");
  setText("pack-description", selected?.description || (isSre(selected) ? OFFLINE_SRE.description : "暂无简介，点击下方卡片查看版本与作者资料。"));
  setText("meta-mc", versionSummary(selected) || "未提供");
  ui["meta-mc"].title = versionTitle(selected);
  setText("meta-ver", displayVersion(selected) || "未提供");
  ui["meta-ver"].previousElementSibling.textContent = resourceLabel() + "版本";
  setText("meta-installed", selected?.installedVersion || "—");
  setImage(ui["banner-image"], packArt(selected, "background") || packArt(selected, "icon"));
  ui.banner.classList.toggle("remote-feature", !!selected && (isMcmod(selected) || isPlatform(selected)));
  ui["meta-installed"].parentElement.hidden = !!selected && (isMcmod(selected) || isPlatform(selected));
  ui["goto-versions"].hidden = detailFeatured;
  ui["more-wrap"].hidden = detailFeatured;
  if (detailFeatured) closeMenu();
  ui.banner.setAttribute("aria-busy", String(isBusy || !!state.pending));

  const plan = !selected ? "disabled" : !live ? "disabled" : selected.updateAvailable ? "update" : !selected.installed ? "download" : "games";
  const primaryLabel = plan === "update" ? t.updateAction : plan === "games" ? t.gotoGames : isBusy ? t.busy : t.download;
  ui.primary.textContent = detailFeatured ? "查看详情" : isBusy && plan !== "games" ? t.busy : primaryLabel;
  ui.primary.disabled = detailFeatured ? false : !selected || selected.offline || !live || lost || ((plan === "download" || plan === "update") && (isBusy || !!state.pending || running));
  ui.primary.setAttribute("aria-label", `${ui.primary.textContent}：${selected?.name || SRE_ID}`);

  const percent = !task.indeterminate && typeof task.progress === "number" && Number.isFinite(task.progress)
    ? Math.max(0, Math.min(100, task.progress <= 1 ? task.progress * 100 : task.progress)) : null;
  ui["task-progress"].hidden = !(isBusy && percent !== null);
  if (percent !== null) ui["task-progress"].value = percent;
  ui["task-progress"].setAttribute("aria-label", t.busy);
   const phase = (isBusy && task.phase) || (detailFeatured ? `${sourceName(selected.source)} · 查看资源资料与可用下载来源` : state.localNote || task.phase || (isBusy ? t.sent : !live ? (state.link === "connecting" ? t.phaseWait : t.phaseOffline) : running ? t.gameRunning : selected?.updateAvailable ? t.phaseUpdate : selected?.installed ? t.phaseInstalled : t.phaseReady));
   setText("phase", percent !== null && isBusy ? `${phase} ${Math.round(percent)}%` : phase);
   setText("task-error", live ? task.error || "" : "");
   ui["task-error"].hidden = !ui["task-error"].textContent;
  ui.cancel.hidden = !(isBusy || state.pending);
  ui.cancel.disabled = !live || lost;
  ui["goto-versions"].disabled = !nativeOk;
  ui.more.disabled = state.link === "offline";
  ui.refresh.disabled = !live || isBusy || !!state.pending;
  ui["open-services"].disabled = state.link === "offline" || lost;
  ui["save-services"].disabled = !live;
  for (const [id, enabled] of [
    ["check-updates", live && !isBusy && !state.pending],
    ["install-quark", live && !selected?.offline && !mutatingLocked()],
    ["import-pack", live && !isBusy && !state.pending],
    ["launch-installed", live && !!selected?.installed && !selected.updateAvailable && !mutatingLocked()],
    ["goto-version-settings", nativeOk],
    ["goto-tasks", nativeOk],
    ["goto-games", nativeOk],
    ["goto-account", nativeOk]
  ]) ui[id].disabled = !enabled;

  const missingCatalog = live && sourceEnabled("mcl") && !catalogUrl();
   const notice = state.hostError ? "" : missingCatalog ? t.catalogMissing : live ? t.catalogReady : "";
   setText("notice-text", notice);
   ui.notice.hidden = !notice;
   ui["notice-config"].hidden = !missingCatalog;
   ui["notice-config"].disabled = !live;
  ui.error.hidden = !state.hostError;
  setText("error", state.hostError);
  const hint = !live ? t.hintOffline : isBusy ? t.hintLive : missingCatalog ? t.hintQuark : t.hintLive;
  setText("hint", detailFeatured ? "版本以跨度简写，不代表区间内所有版本均兼容；完整版本与下载方式请查看详情。" : running ? `${t.gameRunning} ${hint}` : isBusy ? `${t.locked} ${hint}` : hint);

  setText("recommend-empty", !others.length ? (!live ? t.recommendOffline : query ? t.recommendSearch : t.recommendNone) : "");
  ui["recommend-empty"].hidden = others.length > 0;
  syncButtons(ui["recommend-list"], others, "rec", openDetail);
   fitRecommend();
  syncButtons(ui.cards, visible, "card", openDetail);
  syncPageTranslate();
  if (translation.page) translateVisible();
  setText("result-count", t.count(visible.length));
  const noPacks = live && list.length === 0;
  const matching = visible;
  const filtersActive = !!(query || ui["filter-mc"].value || ui["filter-loader"].value || ui["filter-category"].value);
  ui.empty.hidden = matching.length > 0 || visible.length > 0 && !filtersActive;
  setText("empty-text", !browse.sources.size ? "尚未选择资源来源，请在上方勾选至少一个来源。" : state.link !== "live" ? "连接启动器后加载所选来源，或勾选 MCL 查看本地预设。" : filtersActive ? "当前页没有匹配的资源。可重置筛选或浏览下一页。" : "当前来源暂无可展示的资源。加载进度、来源限制或错误请查看上方状态。");
  ui["clear-search"].hidden = !query;
  ui["empty-config"].hidden = !(noPacks || missingCatalog) || !live;
  ui["empty-config"].disabled = !live;

  const selectionKey = `${selected?.id || ""}:${statusLabel(selected)}`;
  if (selectionKey !== state.lastSelectionKey) {
    state.lastSelectionKey = selectionKey;
    setText("selection-live", selected ? t.selected(selected.name || selected.id, statusLabel(selected)) : "");
  }
  const runtime = [state.version, state.branch, state.runtimeVersion ? `WebView2 ${state.runtimeVersion}` : "", state.runtimeVersion ? (state.bundledRuntime ? t.bundled : t.system) : ""].filter(Boolean);
  setText("runtime", runtime.join(" · ") || t.preview);
  setText("config-status", state.configNote);
  renderCatalogStatus();
  renderDetail();
  renderQuarkDownload();
}

function applyBoot(data) {
  state.language = String(data.language || "zh").toLowerCase().startsWith("zh") ? "zh" : "en";
  state.version = String(data.version || "");
  state.branch = String(data.branch || "");
  if (data.runtimeVersion) state.runtimeVersion = String(data.runtimeVersion);
  if (typeof data.bundledRuntime === "boolean") state.bundledRuntime = data.bundledRuntime;
  if (state.link === "offline") state.link = "connecting";
  if (state.link === "connecting" && !state.connectDeadline) state.connectDeadline = Date.now() + 8000;
  render();
}
function applySnapshot(data) {
  const launcher = data.launcher;
  if (!launcher || typeof launcher !== "object" || Array.isArray(launcher)) {
    state.link = "lost";
    state.hostError = state.hostError || text().noSnapshot;
    render();
    return;
  }
  state.link = "live";
  state.connectDeadline = 0;
  state.launcher = launcher;
  if (data.runtimeVersion) state.runtimeVersion = String(data.runtimeVersion);
  if (typeof data.bundledRuntime === "boolean") state.bundledRuntime = data.bundledRuntime;
  const selected = typeof launcher.selected === "string" ? launcher.selected : "";
   state.confirmedSelected = selected;
   if (state.optimisticSelected && (!selected || selected === state.optimisticSelected || Date.now() - (state.optimisticAt || 0) > 1500)) state.optimisticSelected = "";
   const task = launcher.task || {};
   if (state.queued) {
     if (selected === state.queued.id) {
       const operation = state.queued.operation;
       state.queued = null;
       markPending(operation);
       postLauncher(operation);
     } else if ((selected && selected !== state.queued.previous && Date.now() - state.queued.at > 400) || Date.now() - state.queued.at > 2500) {
       state.queued = null;
       state.localNote = text().selectMiss;
       state.noteUntil = Date.now() + 4000;
     }
   } else if (state.pending) {
     const moved = !!task.busy || !!task.error || (!!task.phase && task.phase !== state.pending.phase);
     if (moved) { state.pending = null; if (state.localNote === text().sent) state.localNote = ""; }
     else if (Date.now() - state.pending.at > 2500) {
       state.pending = null;
       state.localNote = text().ignored;
       state.noteUntil = Date.now() + 4000;
     }
   } else if (!task.busy && state.localNote === text().sent) state.localNote = "";
   if (state.localNote && state.noteUntil && Date.now() > state.noteUntil) { state.localNote = ""; state.noteUntil = 0; }
   if (task.error && state.hostError === task.error) state.hostError = "";
   else if (state.hostError && !task.error && !task.busy && Date.now() - (state.errorAt || 0) > 1200) state.hostError = "";
  const config = launcher.config || {};
  if (state.configSent && JSON.stringify(catalogUrls(config)) === JSON.stringify(state.configSent.catalogUrls) && config.quarkServiceUrl === state.configSent.quarkServiceUrl) {
    state.configNote = text().configOk;
    state.configSent = null;
  }
  render();
  ensureBrowse();
}
function showHostError(message) {
   state.pending = null;
   state.queued = null;
   state.localNote = "";
   state.errorAt = Date.now();
  state.hostError = String(message || text().badSnapshot);
  if (state.configSent) { state.configNote = text().configFail + state.hostError; state.configSent = null; }
  render();
}
function receive(data) {
  try {
    if (!data || typeof data !== "object") return;
    if (data.type === "bootstrap") applyBoot(data);
    else if (data.type === "browse" || data.type === "page") applySnapshot(data);
    else if (data.type === "error") showHostError(data.message);
    else if (data.type === "presentation") applyPresentation(data.visible);
    else if (data.type === "browsePlugins") openPluginBrowser(data.minecraft);
    else if (data.type === "serverList") receiveServers(data);
    else if (data.type === "platformCategories" || data.type === "mcmodCategories" || data.operation === "categories" && ["platformError", "mcmodError"].includes(data.type)) receiveCategories(data);
    else if (data.type === "xyeVersions" || data.type === "quarkVersions" || data.type === "quarkDownload" || (data.type === "error" && (data.operation === "quarkList" || data.operation === "quarkDownload")) || (data.type === "mcmodError" && (data.operation === "quarkList" || data.operation === "quarkDownload" || data.operation === "xyeResolve"))) receiveQuark(data);
    else if (["mcmodList", "mcmodDetail", "mcmodLinks", "mcmodError"].includes(data.type)) receiveMcmod(data);
    else if (["platformList", "platformDetail", "platformError", "platformTranslation"].includes(data.type)) receivePlatform(data);
  } catch { showHostError(text().badSnapshot); }
}
function moveFocus(parent, event, columns) {
  const buttons = [...parent.querySelectorAll("[data-pack-id]")];
  const index = buttons.indexOf(document.activeElement);
  if (index < 0) return;
  let next = index;
  if (event.key === "ArrowRight") next = index + 1;
  else if (event.key === "ArrowLeft") next = index - 1;
  else if (event.key === "ArrowDown") next = index + columns;
  else if (event.key === "ArrowUp") next = index - columns;
  else if (event.key === "Home") next = 0;
  else if (event.key === "End") next = buttons.length - 1;
  else return;
  event.preventDefault();
  const target = buttons[Math.max(0, Math.min(buttons.length - 1, next))];
  buttons.forEach(node => { node.tabIndex = node === target ? 0 : -1; });
  target?.focus();
}
// MC百科: the host performs HTTP; this document only renders plain data.
function isMcmod(item) { return item?.source !== "mcl" && (item?.source === "mcmod" || /^mcmod-(?:datapack-)?\d+$/.test(item?.id || "")); }
function wikiId(item) {
  const id = Number(item?.wikiId || String(item?.id || "").replace(/^mcmod-(?:datapack-)?/, ""));
  return Number.isSafeInteger(id) && id > 0 ? id : 0;
}
function normalizeMcmod(item) {
  const id = wikiId(item);
  const resourceType = resourceTypeOf(item);
  return item && typeof item === "object" && id && ["modpack", "datapack"].includes(resourceType) ? { ...item, id: resourceType === "modpack" ? `mcmod-${id}` : `mcmod-datapack-${id}`, wikiId: id, source: "mcmod", resourceType } : null;
}
function siteFilters() {
  const minecraft = ui["filter-mc"].value;
  const loader = browse.resourceType === "modpack" ? ui["filter-loader"].value : "";
  const category = Number(categoryFor("mcmod")) || 0;
  const sort = MCMOD_SORTS.includes(ui["filter-mcmod-sort"]?.value) ? ui["filter-mcmod-sort"].value : "default";
  return { ...resourcePayload(), ...(minecraft ? { minecraft } : {}), ...(loader ? { loader } : {}), ...(category ? { category } : {}), ...(sort !== "default" ? { sort } : {}) };
}
function filterKey() { return JSON.stringify(siteFilters()); }
function pageKey(page = catalog.page, filters = filterKey()) { return JSON.stringify([filters, page]); }
function catalogItems() {
  const page = catalog.pages.get(pageKey());
  const mcmodItems = catalog.include && supportsResource("mcmod") ? (page?.items || catalog.retained.get(filterKey())?.items || []) : [];
  const encyclopedia = mcmodItems.map(item => ({ ...item, ...catalog.details.get(mcKey(item)) })).filter(matchesFilters);
  const publicItems = selectedPlatforms().flatMap(source => {
    if (!supportsResource(source)) return [];
    const cached = platform.pages.get(platformPageKey(platformPosition(source).page, source));
    const items = cached ? cached.items : platform.retained.get(platformFilterKey(source))?.items || [];
    return items.map(item => ({ ...item, ...platform.details.get(detailKey(item.source, item.projectId, resourceTypeOf(item))) }));
  });
  return ExplorerIdentity.merge(packs(), encyclopedia, publicItems);
}
function syncFilterOptions(items) {
  const versions = [...new Set([...COMMON_MC, ...items.flatMap(mcVersions), ...[...catalog.details.values()].flatMap(mcVersions), ...[...platform.details.values()].flatMap(mcVersions), ui["filter-mc"].value].filter(Boolean))]
    .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
  for (const [id, options, label] of [
    ["filter-mc", versions.map(value => [value, value]), "全部版本"],
    ["filter-loader", activeLoaders().map(value => [value, value]), "全部加载器"],
    ["filter-category", categoryOptions(), "全部分类"]
  ]) {
    const node = ui[id];
    const selected = node.value;
    const key = JSON.stringify(options);
    if (node.dataset.options === key) continue;
    node.dataset.options = key;
    node.replaceChildren(...[["", label], ...options].map(([value, label]) => {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      return option;
    }));
    node.value = options.some(([value]) => value === selected) ? selected : "";
    if (id === "filter-category" && selected && node.value !== selected) {
      catalog.page = 1; resetPlatformPages();
      queueMicrotask(() => { render(); ensureBrowse(); });
    }
  }
}
function changeSiteFilters(event) {
  catalog.page = 1;
  catalog.totalPages = catalog.pages.get(pageKey())?.totalPages || catalog.retained.get(filterKey())?.totalPages || 0;
  if (isRemoteBrowse()) resetPlatformPages();
  // Never present a different filter's results while this request is pending.
  render();
  ensureBrowse();
}
function ensureMcmod() {
  const key = pageKey();
  if (catalog.include && supportsResource("mcmod") && state.link === "live" && !catalog.pages.has(key) && !catalog.listPending.has(key) && !catalog.listErrors.has(key)) requestPage(catalog.page);
}
function clearRequest(map, key) {
  clearTimeout(map.get(key));
  map.delete(key);
}
function requestPage(page, force = false) {
  if (!catalog.include || !supportsResource("mcmod") || state.link !== "live" || !connected() || !Number.isInteger(page) || page < 1 || page > 500) return;
  catalog.page = page;
  const filters = filterKey();
  const key = pageKey(page, filters);
  const cached = catalog.pages.get(key);
  if (cached && !force) {
    catalog.retained.set(filters, { ...cached, page });
    catalog.totalPages = cached.totalPages;
    render();
    return;
  }
  if (catalog.listPending.has(key) && !force) { render(); return; }
  clearRequest(catalog.listPending, key);
  const requestId = `mcmod-list-${++catalog.sequence}`;
  catalog.requests.set(requestId, { key, filters, page, resourceType: browse.resourceType });
  catalog.latest.set(key, requestId);
  catalog.listErrors.delete(key);
  catalog.listPending.set(key, setTimeout(() => {
    catalog.listPending.delete(key);
    catalog.listErrors.set(key, "请求超时，未收到 MC百科目录。请重试。");
    render();
  }, REQUEST_TIMEOUT));
  render();
  if (!post({ type: "mcmod", operation: "list", page, ...siteFilters(), requestId })) {
    clearRequest(catalog.listPending, key);
    catalog.listErrors.set(key, "连接已断开，请连接启动器后重试。");
    render();
  }
}
function requestDetail(item, force = false) {
  const wiki = wikiId(item), type = resourceTypeOf(item), id = mcKey(item);
  if (!id || !catalog.include || state.link !== "live" || !connected() || catalog.detailPending.has(id) || (!force && catalog.details.has(id))) return;
  catalog.detailErrors.delete(id);
  catalog.detailPending.set(id, setTimeout(() => {
    catalog.detailPending.delete(id);
    catalog.detailErrors.set(id, "详情请求超时，请重试。");
    renderDetail();
  }, REQUEST_TIMEOUT));
  renderDetail();
  if (!post({ type: "mcmod", operation: "detail", id: wiki, ...resourcePayload(type) })) {
    clearRequest(catalog.detailPending, id);
    catalog.detailErrors.set(id, "连接已断开，请连接启动器后重试。");
    renderDetail();
  }
}
function receiveMcmod(data) {
  if (data.type === "mcmodLinks") { finishAssociation(data); return; }
  if (data.type === "mcmodError" && data.operation === "links") { finishAssociation(data, String(data.message || "百科关联查询失败")); return; }
  if (data.type === "mcmodList") {
    const request = catalog.requests.get(data.requestId);
    if (!request || catalog.latest.get(request.key) !== data.requestId || Number(data.page) !== request.page || (data.resourceType || "modpack") !== request.resourceType) return;
    const { key, filters, page } = request;
    if (Array.isArray(data.categories) && data.categories.length) storeCategories("mcmod", request.resourceType, data.categories);
    if (!Array.isArray(data.items)) {
      receiveMcmod({ type: "mcmodError", operation: "list", page, requestId: data.requestId, message: "目录数据格式不正确，请重试。" });
      return;
    }
    clearRequest(catalog.listPending, key);
    catalog.listErrors.delete(key);
    const totalPages = Math.max(1, Math.min(500, Math.floor(Number(data.totalPages) || 1)));
    const unique = new Map(data.items.map(normalizeMcmod).filter(Boolean).map(item => [item.id, item]));
    const items = [...unique.values()];
    catalog.pages.set(key, { items, totalPages });
    catalog.requests.delete(data.requestId);
    if (filters === filterKey() && page === catalog.page) {
      catalog.retained.set(filters, { items, totalPages, page });
      catalog.totalPages = totalPages;
    }
    render();
  } else if (data.type === "mcmodDetail") {
    const item = normalizeMcmod(data.item);
    if (!item) return;
    const key = mcKey(item);
    clearRequest(catalog.detailPending, key);
    catalog.detailErrors.delete(key);
    catalog.details.set(key, item);
    render();
  } else if (data.type === "mcmodError") {
    const message = String(data.message || "MC百科请求失败，请重试。");
    // List identity must be echoed; uncorrelated or superseded replies are unsafe.
    if (data.operation === "list") {
      const request = catalog.requests.get(data.requestId);
      if (!request || catalog.latest.get(request.key) !== data.requestId || (data.page != null && Number(data.page) !== request.page)) return;
      clearRequest(catalog.listPending, request.key);
      catalog.listErrors.set(request.key, message);
      catalog.requests.delete(data.requestId);
    } else if (data.operation === "detail") {
      const id = Number(data.id ?? (catalog.detailPending.size === 1 ? catalog.detailPending.keys().next().value : 0));
      if (!Number.isSafeInteger(id) || id < 1) return;
      const key = mcKey(id, data.resourceType || "modpack");
      clearRequest(catalog.detailPending, key);
      catalog.detailErrors.set(key, message);
    } else if (data.operation === "download") {
      const id = Number(data.id ?? (catalog.downloads.size === 1 ? catalog.downloads.keys().next().value : 0));
      if (!Number.isSafeInteger(id) || id < 1) return;
      catalog.downloads.set(mcKey(id, data.resourceType || "modpack"), { error: true, message });
      closeVersionWait();
    }
    render();
  }
}
function renderCatalogStatus() {
  const live = state.link === "live";
  const remote = isRemoteBrowse();
  for (const source of ["mcl", "curseforge", "modrinth"]) ui[`source-${source}`].checked = sourceEnabled(source);
  if (ui["source-mclists"]) ui["source-mclists"].checked = servers.includeMclists;
  ui["include-mcmod"].checked = catalog.include;
  ui["filter-category"].disabled = categoryOptions().length === 0;
  ui["filter-sort-label"].hidden = !remote;
  ui["filter-mcmod-sort-label"].hidden = !catalog.include || !supportsResource("mcmod");
  const names = [...browse.sources].map(sourceName).join(" + ");
  const key = pageKey();
  const pending = catalog.listPending.has(key);
  const error = catalog.listErrors.get(key);
  const page = catalog.pages.get(key);
  const previous = catalog.retained.get(filterKey());
  const retained = previous && (error || !page) ? ` 仍显示相同筛选的第 ${previous.page} 页结果。` : "";
  ui["mcmod-status"].hidden = !catalog.include;
  renderPagination();
  renderPlatformStatus();
  renderResourceTypes();
  renderMclExplorer(); renderAssociationStatus(); queueAssociationLookup(); renderSourceSummary(); renderCategoryHelp();
}
function isRemoteBrowse() { return selectedPlatforms().length > 0; }
function sourceName(source = primaryPlatformSource()) {
  return source === "mcl" ? "MCL" : source === "modrinth" ? "Modrinth" : source === "mcmod" ? "MC百科" : "CurseForge";
}
function activeLoaders() { return isRemoteBrowse() && !catalog.include ? PLATFORM_LOADERS : SITE_LOADERS; }
function projectIdOf(item) {
  if (!item || typeof item !== "object") return null;
  const id = String(item.id || "");
  const prefixed = id.match(/^(curseforge|modrinth)-([A-Za-z0-9]+)$/);
  const source = item.source || prefixed?.[1] || "";
  if (source !== "curseforge" && source !== "modrinth") return null;
  const projectId = item.projectId != null && item.projectId !== "" ? String(item.projectId) : (prefixed && prefixed[1] === source ? prefixed[2] : "");
  if (source === "curseforge" && !/^\d+$/.test(projectId)) return null;
  if (source === "modrinth" && !/^[A-Za-z0-9]{1,32}$/.test(projectId)) return null;
  if (prefixed && prefixed[1] === source && prefixed[2] !== projectId) return null;
  return { source, projectId };
}
function isPlatform(item) { return !!projectIdOf(item); }
function detailKey(source, projectId, type = browse.resourceType) { return type === "modpack" ? `${source}:${projectId}` : `${type}:${source}:${projectId}`; }
function normalizePlatform(item, expectedSource) {
  const identity = projectIdOf(item);
  if (!identity || (expectedSource && identity.source !== expectedSource)) return null;
  if (!RESOURCE_TYPES[resourceTypeOf(item)]) return null;
  return {
    id: `${identity.source}-${identity.projectId}`,
    projectId: identity.projectId,
    source: identity.source,
    resourceType: resourceTypeOf(item),
    slug: typeof item.slug === "string" ? item.slug : "",
    name: typeof item.name === "string" ? item.name : "",
    englishName: typeof item.englishName === "string" ? item.englishName : "",
    description: typeof item.description === "string" ? item.description : "",
    icon: typeof item.icon === "string" ? item.icon : "",
    website: typeof item.website === "string" ? item.website : "",
    minecraftVersions: Array.isArray(item.minecraftVersions) ? item.minecraftVersions.filter(value => typeof value === "string") : [],
    loaders: Array.isArray(item.loaders) ? item.loaders.filter(value => typeof value === "string") : [],
    authors: Array.isArray(item.authors) ? item.authors.filter(value => typeof value === "string") : [],
    categories: Array.isArray(item.categories) ? item.categories.filter(value => typeof value === "string") : [],
    version: typeof item.version === "string" ? item.version : "",
    downloadCount: countOf(item.downloadCount)
  };
}
function platformFilters(source = primaryPlatformSource()) {
  const minecraft = ui["filter-mc"].value || "";
  const loader = browse.resourceType === "modpack" && PLATFORM_LOADERS.includes(ui["filter-loader"].value) ? ui["filter-loader"].value : "";
  const sort = PLATFORM_SORTS.includes(ui["filter-sort"]?.value) ? ui["filter-sort"].value : "relevance";
  const category = categoryFor(source);
  return { source, ...resourcePayload(), query: platform.query, minecraft, loader, sort, ...(category ? { category } : {}) };
}
function platformFilterKey(source = primaryPlatformSource()) { return JSON.stringify(platformFilters(source)); }
function platformPageKey(page = platform.page, source = primaryPlatformSource()) { return JSON.stringify([platformFilterKey(source), page]); }
function selectBrowseSource(source, enabled) {
  if (!["mcl", "curseforge", "modrinth", "mcmod"].includes(source)) return;
  const next = typeof enabled === "boolean" ? enabled : !sourceEnabled(source);
  if (next === sourceEnabled(source)) return;
  clearTimeout(platform.searchTimer); platform.searchTimer = 0;
  if (next) { browse.sources.add(source); browse.source = source; } else browse.sources.delete(source);
  catalog.include = sourceEnabled("mcmod");
  ui["include-mcmod"].checked = catalog.include;
  if (!PLATFORM_LOADERS.includes(ui["filter-loader"].value) && !catalog.include) ui["filter-loader"].value = "";
  if (!state.composing) platform.query = ui.search.value.trim();
  if (ui["pack-detail"].open) ui["pack-detail"].close();
  catalog.viewedId = "";
  render(); ensureBrowse();
}
function ensureBrowse() {
  if (state.link !== "live") return;
  ensureCategories();
  for (const source of selectedPlatforms()) ensurePlatform(source);
  if (catalog.include) ensureMcmod();
  ensureMcl();
}
function ensurePlatform(source = primaryPlatformSource()) {
  if (!sourceEnabled(source) || !supportsResource(source)) return;
  const position = platformPosition(source), key = platformPageKey(position.page, source);
  const cached = platform.pages.get(key);
  if (cached) {
    if (position.totalPages !== cached.totalPages) { position.totalPages = cached.totalPages; render(); }
    return;
  }
  if (state.link === "live" && !platform.listPending.has(key) && !platform.listErrors.has(key)) requestPlatformPage(position.page, false, source);
}
function invalidatePlatformLists() {
  for (const key of [...platform.listPending.keys()]) clearRequest(platform.listPending, key);
  platform.pages.clear();
  platform.latest.clear();
  platform.listErrors.clear();
}
function schedulePlatformSearch() {
  clearTimeout(platform.searchTimer);
  platform.searchTimer = 0;
  if (!isRemoteBrowse() || state.composing) return;
  platform.searchTimer = setTimeout(() => {
    platform.searchTimer = 0;
    commitPlatformQuery(false);
  }, SEARCH_DEBOUNCE);
}
function commitPlatformQuery(force = false) {
  if (!isRemoteBrowse() || state.composing) return;
  const next = ui.search.value.trim();
  if (next !== platform.query) {
    platform.query = next; resetPlatformPages();
    for (const source of selectedPlatforms()) requestPlatformPage(1, true, source);
  } else if (force) {
    for (const source of selectedPlatforms()) requestPlatformPage(platformPosition(source).page, true, source);
  }
}
function requestPlatformPage(page, force = false, source = primaryPlatformSource()) {
  if (!sourceEnabled(source) || !supportsResource(source) || state.link !== "live" || !connected() || !Number.isInteger(page) || page < 1 || page > PLATFORM_PAGE_MAX) return;
  const position = platformPosition(source);
  position.page = page;
  const filters = platformFilters(source), filter = platformFilterKey(source), key = platformPageKey(page, source);
  const cached = platform.pages.get(key);
  if (cached && !force) {
    platform.retained.set(filter, { ...cached, page }); position.totalPages = cached.totalPages;
    render(); return;
  }
  if (platform.listPending.has(key) && !force) { render(); return; }
  clearRequest(platform.listPending, key);
  if (force) platform.pages.delete(key);
  const requestId = `platform-list-${++platform.sequence}`;
  platform.requests.set(requestId, { kind: "list", key, filters: filter, page, source, resourceType: browse.resourceType });
  platform.latest.set(key, requestId); platform.listErrors.delete(key);
  platform.listPending.set(key, setTimeout(() => {
    if (platform.latest.get(key) !== requestId) return;
    platform.listPending.delete(key); platform.listErrors.set(key, "请求超时，未收到平台目录。请重试。"); render();
  }, PLATFORM_TIMEOUT));
  render();
  if (!post({ type: "platform", operation: "list", ...filters, page, requestId })) {
    clearRequest(platform.listPending, key);
    platform.listErrors.set(key, "连接已断开，请连接启动器后重试。"); render();
  }
}
function requestPlatformDetail(item, force = false) {
  const normalized = normalizePlatform(item);
  if (!normalized || state.link !== "live" || !connected()) return;
  const key = detailKey(normalized.source, normalized.projectId, resourceTypeOf(normalized));
  if (platform.detailPending.has(key) && !force) return;
  if (!force && platform.details.has(key)) return;
  clearRequest(platform.detailPending, key);
  const requestId = `platform-detail-${++platform.sequence}`;
  platform.detailLatest.set(key, requestId);
  platform.requests.set(requestId, { kind: "detail", source: normalized.source, projectId: normalized.projectId, resourceType: resourceTypeOf(normalized), key });
  platform.detailErrors.delete(key);
  platform.detailPending.set(key, setTimeout(() => {
    if (platform.detailLatest.get(key) !== requestId) return;
    platform.detailPending.delete(key);
    platform.detailErrors.set(key, "详情请求超时，请重试。");
    renderDetail();
  }, PLATFORM_TIMEOUT));
  renderDetail();
  if (!post({ type: "platform", operation: "detail", source: normalized.source, id: normalized.projectId, ...resourcePayload(resourceTypeOf(normalized)), requestId })) {
    clearRequest(platform.detailPending, key);
    platform.detailErrors.set(key, "连接已断开，请连接启动器后重试。");
    renderDetail();
  }
}
function downloadPlatform() {
  const item = normalizePlatform(findDetailItem());
  if (!item || state.link !== "live") return;
  const key = detailKey(item.source, item.projectId, resourceTypeOf(item));
  const requestId = `platform-download-${++platform.sequence}`;
  platform.downloadLatest.set(key, requestId);
  platform.requests.set(requestId, { kind: "download", source: item.source, projectId: item.projectId, resourceType: resourceTypeOf(item), key });
  if (post({ type: "platform", operation: "download", source: item.source, id: item.projectId, ...resourcePayload(resourceTypeOf(item)), requestId })) {
    platform.downloads.set(key, { error: false, message: "已请求启动器打开版本列表，请在原生界面选择文件。", requestId });
    openVersionWait(item.name);
  } else {
    platform.downloads.set(key, { error: true, message: "无法连接启动器，请重新连接后重试。", requestId });
    closeVersionWait();
  }
  renderDetail();
}
function receivePlatform(data) {
  const request = platform.requests.get(data.requestId);
  if (request && (data.resourceType || "modpack") !== request.resourceType) return;
  if (data.type === "platformList") {
    if (!request || request.kind !== "list" || platform.latest.get(request.key) !== data.requestId) return;
    if (data.source !== request.source || Number(data.page) !== request.page) return;
    if (!Array.isArray(data.items)) {
      receivePlatform({ type: "platformError", source: request.source, resourceType: request.resourceType, operation: "list", page: request.page, requestId: data.requestId, message: "目录数据格式不正确，请重试。" });
      return;
    }
    clearRequest(platform.listPending, request.key);
    platform.listErrors.delete(request.key);
    const totalPages = Math.max(1, Math.min(PLATFORM_PAGE_MAX, Math.floor(Number(data.totalPages) || 1)));
    const items = [...new Map(data.items.map(item => normalizePlatform(item, request.source)).filter(item => item && resourceTypeOf(item) === request.resourceType).map(item => [item.id, item])).values()];
    platform.pages.set(request.key, { items, totalPages });

    if (request.filters === platformFilterKey(request.source) && request.page === platformPosition(request.source).page && sourceEnabled(request.source)) {
      platform.retained.set(request.filters, { items, totalPages, page: request.page });
      platformPosition(request.source).totalPages = totalPages;
    }
    render();
    return;
  }
  if (data.type === "platformDetail") {
    const item = normalizePlatform(data.item, data.source);
    if (!request || request.kind !== "detail" || !item) return;
    if (data.source !== request.source || String(data.id ?? "") !== request.projectId || item.projectId !== request.projectId || resourceTypeOf(item) !== request.resourceType) return;
    if (platform.detailLatest.get(request.key) !== data.requestId) return;
    clearRequest(platform.detailPending, request.key);
    platform.detailErrors.delete(request.key);
    platform.details.set(request.key, item);
    platform.requests.delete(data.requestId);
    render();
    return;
  }
  if (data.type === "platformTranslation") { receiveTranslation(data); return; }
  if (data.type !== "platformError" || !request || data.source !== request.source || (data.operation !== request.kind && !(data.operation === "translate" && request.kind === "translate"))) return;
  const message = String(data.message || "平台请求失败，请重试。");
  if (request.kind === "list") {
    if (platform.latest.get(request.key) !== data.requestId || (data.page != null && Number(data.page) !== request.page)) return;
    clearRequest(platform.listPending, request.key);
    platform.listErrors.set(request.key, message);
  } else if (request.kind === "detail") {
    if (platform.detailLatest.get(request.key) !== data.requestId || (data.id != null && String(data.id) !== request.projectId)) return;
    clearRequest(platform.detailPending, request.key);
    platform.detailErrors.set(request.key, message);
  } else if (request.kind === "translate") {
    receiveTranslation({ requestId: data.requestId, text: "", failed: true, message });
    return;
  } else if (request.kind === "download") {
    if (platform.downloadLatest.get(request.key) !== data.requestId || (data.id != null && String(data.id) !== request.projectId)) return;
    platform.downloads.set(request.key, { error: true, message, requestId: data.requestId });
    closeVersionWait();
  } else return;
  platform.requests.delete(data.requestId);
  render();
}

function translationKey(item) { const identity = projectIdOf(item); return identity ? `${identity.source}:${identity.projectId}` : ""; }
function translatedText(item) { return translation.text.get(translationKey(item)) || ""; }
function requestTranslation(item) {
  const identity = projectIdOf(item);
  const key = translationKey(item);
  if (!identity || !key || state.link !== "live" || translation.pending.has(key) || translation.text.has(key)) return;
  translation.missing.delete(key); translation.errors.delete(key);
  const requestId = `translate-${++translation.sequence}`;
  translation.requests.set(requestId, key);
  platform.requests.set(requestId, { kind: "translate", source: identity.source, projectId: identity.projectId, resourceType: resourceTypeOf(item), key });
  translation.pending.set(key, requestId);
  if (!post({ type: "platform", operation: "translate", source: identity.source, id: identity.projectId, ...resourcePayload(resourceTypeOf(item)), requestId })) {
    translation.pending.delete(key); translation.requests.delete(requestId); platform.requests.delete(requestId); translation.missing.add(key);
  }
}
function translateVisible(force = false) {
  for (const item of catalogItems()) {
    if (!isPlatform(item)) continue;
    if (!force && translation.missing.has(translationKey(item))) continue;
    requestTranslation(item);
  }
}
function syncCardTranslate(node, item) {
  const key = translationKey(item);
  let button = node.querySelector(".card-translate");
  if (!key) { button?.remove(); return; }
  if (!button) {
    button = document.createElement("button");
    button.type = "button"; button.className = "icon-button card-translate"; button.textContent = "译";
    button.addEventListener("click", event => {
      event.stopPropagation();
      const current = catalogItems().find(pack => pack.id === node.dataset.packId);
      if (!current) return;
      const currentKey = translationKey(current);
      if (translation.text.has(currentKey)) { translation.text.delete(currentKey); render(); return; }
      requestTranslation(current); render();
    });
    node.append(button);
  }
  const done = translation.text.has(key), busy = translation.pending.has(key), failed = translation.errors.get(key) || "";
  button.setAttribute("aria-pressed", String(done));
  button.setAttribute("aria-busy", String(busy));
  button.disabled = busy;
  button.title = done ? "恢复原文" : busy ? "正在翻译" : failed ? `翻译失败：${failed}` : "翻译这条简介";
}
function syncPageTranslate() {
  const button = ui["translate-page"];
  button.hidden = !catalogItems().some(isPlatform);
  button.setAttribute("aria-pressed", String(translation.page));
  button.title = translation.page ? "本页翻译已启用，再点关闭" : "翻译本页 CurseForge 与 Modrinth 简介";
}
function receiveTranslation(data) {
  const key = translation.requests.get(data.requestId);
  if (!key) return;
  translation.requests.delete(data.requestId);
  translation.pending.delete(key);
  const textValue = typeof data.text === "string" ? data.text.trim() : "";
  if (data.failed) translation.errors.set(key, String(data.message || "翻译失败"));
  else if (textValue) { translation.text.set(key, textValue); translation.errors.delete(key); }
  else { translation.missing.add(key); translation.errors.delete(key); }
  render();
  if (textValue && ui["pack-detail"].open && translationKey(findDetailItem()) === key) {
    ui["detail-introduction"].open = true;
    ui["detail-introduction"].scrollIntoView({ block: "nearest" });
  }
}
function pageableSources() {
  const sources = [];
  for (const source of ["curseforge", "modrinth"]) {
    if (sourceEnabled(source) && supportsResource(source)) sources.push(source);
  }
  if (catalog.include && supportsResource("mcmod")) sources.push("mcmod");
  return sources;
}
function sourcePageState(source) {
  if (source === "mcmod") return { page: catalog.page, totalPages: catalog.totalPages, max: 500 };
  const position = platformPosition(source);
  return { page: position.page, totalPages: position.totalPages, max: PLATFORM_PAGE_MAX };
}
function renderPagination() {
  const live = state.link === "live";
  const sources = pageableSources();
  ui.pagination.hidden = sources.length === 0;
  const parts = sources.map(source => {
    const pageState = sourcePageState(source);
    return `${sourceName(source)} · 第 ${pageState.page} 页${pageState.totalPages ? ` / ${pageState.totalPages}` : ""}`;
  });
  setText("page-label", parts.join("  ·  ") || "尚未加载");
  ui["previous-page"].disabled = !live || !sources.some(source => sourcePageState(source).page > 1);
  ui["next-page"].disabled = !live || !sources.some(source => {
    const pageState = sourcePageState(source);
    return pageState.totalPages > 0 && pageState.page < pageState.totalPages && pageState.page < pageState.max;
  });
}
function renderPlatformStatus() {
  const live = state.link === "live";
  for (const source of ["curseforge", "modrinth"]) {
    const prefix = source === "curseforge" ? "platform" : "modrinth";
    const enabled = sourceEnabled(source), position = platformPosition(source), name = sourceName(source);
    ui[`${prefix}-status`].hidden = !enabled;
    if (!enabled) continue;
    if (!supportsResource(source)) {
      ui[`${prefix}-status`].dataset.state = "unsupported";
      ui[`${prefix}-status`].setAttribute("aria-busy", "false");
      setText(`${prefix}-message`, `${name} 当前没有此类型目录，请切换其他探索器。`);
      ui[`${prefix}-retry`].hidden = true;
      continue;
    }
    const key = platformPageKey(position.page, source), pending = platform.listPending.has(key);
    const error = platform.listErrors.get(key), page = platform.pages.get(key);
    const previous = platform.retained.get(platformFilterKey(source));
    const retained = previous && (error || !page) ? ` 仍显示相同筛选的第 ${previous.page} 页。` : "";
    ui[`${prefix}-status`].dataset.state = !live ? "offline" : pending ? "loading" : error ? "error" : "ready";
    ui[`${prefix}-status`].setAttribute("aria-busy", String(live && pending));
    setText(`${prefix}-message`, !live ? `连接启动器后加载 ${name}。` : pending ? `正在加载 ${name} 第 ${position.page} 页…${retained}` : error ? `${error}${retained}` : page ? `${name} · 当前页 ${page.items.length} 个${resourceLabel()}` : `等待加载 ${name}…`);
    ui[`${prefix}-retry`].hidden = !error || pending;
    ui[`${prefix}-retry`].disabled = !live;
  }
}
function findDetailItem() {
  if (!catalog.detailId) return null;
  if (catalog.detailBase?.source === "mcl") return packs().find(item => item.id === catalog.detailId) || catalog.detailBase;
  if (isMcmod({ id: catalog.detailId })) {
    const id = wikiId({ id: catalog.detailId });
    return { ...catalog.detailBase, ...catalog.details.get(mcKey(catalog.detailBase)) };
  }
  if (isPlatform(catalog.detailBase) || isPlatform({ id: catalog.detailId })) {
    const identity = projectIdOf(catalog.detailBase) || projectIdOf({ id: catalog.detailId });
    return identity ? { ...catalog.detailBase, ...platform.details.get(detailKey(identity.source, identity.projectId, resourceTypeOf(catalog.detailBase))) } : catalog.detailBase;
  }
  return packs().find(item => item.id === catalog.detailId) || catalog.detailBase;
}
function openDetail(id) {
  const item = catalogItems().find(pack => pack.id === id);
  if (!item) return;
  closeMenu();
  catalog.returnFocus = document.activeElement;
  catalog.detailId = id;
  catalog.viewedId = id;
  catalog.detailBase = item;
  ui["detail-introduction"].open = false;
  if (!isMcmod(item) && !isPlatform(item)) choose(id);
  render();
  if (!ui["pack-detail"].open) ui["pack-detail"].showModal();
  if (isPlatform(item) && translation.page && translation.autoDetail !== id) { translation.autoDetail = id; requestTranslation(item); }
  if (isMcmod(item)) requestDetail(item);
  if (isPlatform(item)) requestPlatformDetail(item);
}
function renderIntroduction(value) {
  const root = ui["detail-description"];
  const text = String(value ?? "");
  if (root.dataset.source === text) return;
  root.dataset.source = text;
  root.replaceChildren();
  if (!text.includes("\n") && !/[*_`#>\[!]/.test(text)) { root.textContent = text; return; }
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  let index = 0;
  while (index < lines.length) {
    while (index < lines.length && !lines[index].trim()) index++;
    if (index >= lines.length) break;
    const start = index;
    const list = /^\s*(?:[-*+]|\d+[.)])\s+/.test(lines[index]);
    index++;
    while (index < lines.length && (list ? (lines[index].trim() && /^\s*(?:[-*+]|\d+[.)])\s+/.test(lines[index])) || /^\s{2,}\S/.test(lines[index]) : lines[index].trim())) index++;
    appendBlock(root, lines.slice(start, index).join("\n").trim());
  }
  if (!root.childNodes.length) root.textContent = text;
}
function appendBlock(root, block) {
  if (!block) return;
  const heading = /^(#{1,4})\s+(.+)$/.exec(block);
  if (heading) { const node = document.createElement("h" + Math.min(heading[1].length, 4)); fillInline(node, heading[2]); root.append(node); return; }
  if (/^(-{3,}|\*{3,}|_{3,})$/.test(block)) { root.append(document.createElement("hr")); return; }
  if (block.startsWith(">")) { const quote = document.createElement("blockquote"); for (const part of block.replace(/^>\s?/gm, "").split(/\n{2,}/)) appendBlock(quote, part.trim()); root.append(quote); return; }
  if (/^```/.test(block)) { const node = document.createElement("pre"); const code = document.createElement("code"); code.textContent = block.replace(/^```[^\n]*\n?|```$/g, ""); node.append(code); root.append(node); return; }
  const lines = block.split("\n");
  const parsed = [];
  for (const line of lines) {
    const item = /^(\s*)(?:[-*+]|\d+[.)])\s+(.*)$/.exec(line);
    if (!item) { parsed.length = 0; break; }
    const depth = item[1].replace(/\t/g, "  ").length >= 2 ? 1 : 0;
    parsed.push({ depth, text: item[2] });
  }
  if (parsed.length) {
    const ordered = /^\s*\d+[.)]\s+/.test(lines.find(line => line.trim()));
    const list = document.createElement(ordered ? "ol" : "ul");
    let parent = null;
    for (const item of parsed) {
      const node = document.createElement("li");
      fillInline(node, item.text);
      if (item.depth && parent) {
        let nested = parent.querySelector(":scope > ul, :scope > ol");
        if (!nested) { nested = document.createElement(ordered ? "ol" : "ul"); parent.append(nested); }
        nested.append(node);
      } else { list.append(node); parent = node; }
    }
    root.append(list); return;
  }
  const paragraph = document.createElement("p");
  lines.forEach((line, index) => { if (index) paragraph.append(document.createElement("br")); fillInline(paragraph, line); });
  root.append(paragraph);
}
function fillInline(node, value) {
  const pattern = /\[!\[([^\]]*)\]\((https?:\/\/[^\s)]+)\)\]\((https?:\/\/[^\s)]+)\)|!\[([^\]]*)\]\((https?:\/\/[^\s)]+)\)|\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)|(\*\*|__)(.+?)\8|~~(.+?)~~|`([^`]+)`|\*([^*\n]+)\*|_([^_\n]+)_/g;
  let cursor = 0; let match;
  while ((match = pattern.exec(value))) {
    if (match.index > cursor) node.append(value.slice(cursor, match.index));
    if (match[3]) appendImageLink(node, match[1], match[2], match[3]);
    else if (match[5]) appendImage(node, match[4], match[5]);
    else if (match[7]) appendLink(node, match[6], match[7]);
    else if (match[9]) { const strong = document.createElement("strong"); fillInline(strong, match[9]); node.append(strong); }
    else if (match[10]) { const del = document.createElement("del"); fillInline(del, match[10]); node.append(del); }
    else if (match[11]) { const code = document.createElement("code"); code.textContent = match[11]; node.append(code); }
    else { const em = document.createElement("em"); fillInline(em, match[12] || match[13]); node.append(em); }
    cursor = match.index + match[0].length;
  }
  if (cursor < value.length) node.append(value.slice(cursor));
}
function appendImage(node, alt, raw) {
  const image = document.createElement("img");
  const source = imageUrl(raw);
  image.alt = alt || ""; image.referrerPolicy = "no-referrer";
  if (source) image.src = source; else image.alt = image.alt || raw;
  image.addEventListener("error", () => { image.replaceWith(document.createTextNode(image.alt || "")); });
  node.append(image);
  return image;
}
function appendImageLink(node, alt, raw, href) {
  const url = externalUrl(href);
  if (!url) { appendImage(node, alt, raw); return; }
  const link = document.createElement("a");
  link.href = url; link.rel = "noreferrer noopener"; link.target = "_blank";
  link.addEventListener("click", event => { event.preventDefault(); openExternal(url); });
  appendImage(link, alt, raw);
  node.append(link);
}
function appendLink(node, label, raw) {
  const href = externalUrl(raw);
  if (!href) { node.append(label); return; }
  const link = document.createElement("a");
  link.href = href; link.textContent = label; link.rel = "noreferrer noopener"; link.target = "_blank";
  link.addEventListener("click", event => { event.preventDefault(); openExternal(href); });
  node.append(link);
}


function externalUrl(value) {
  return typeof value === "string" && value && classifyUrl(value).ok ? value : "";
}
function projectUrls(item) {
  return [item?.curseForgeUrl, item?.modrinthUrl, item?.website, ...(Array.isArray(item?.links) ? item.links.map(link => link?.url) : [])];
}
function curseForgeUrl(item) {
  const paths = resourceTypeOf(item) === "datapack" ? ["data-packs", "datapacks"] : ["modpacks"];
  for (const raw of projectUrls(item)) {
    const value = externalUrl(raw);
    if (!value) continue;
    const url = new URL(value);
    const segments = url.pathname.split("/").filter(Boolean);
    if (url.protocol === "https:" && /^(?:www\.)?curseforge\.com$/i.test(url.hostname) && segments.length >= 3 && segments[0] === "minecraft" && paths.includes(segments[1])) return value;
  }
  return "";
}
function openExternal(value) {
  const url = externalUrl(value);
  if (state.link === "live" && url) post({ type: "openExternal", url });
}
function quarkShareId(value) {
  const url = externalUrl(value);
  if (!url) return "";
  let parsed;
  try { parsed = new URL(url); } catch { return ""; }
  if (parsed.protocol !== "https:" || parsed.hostname !== "pan.quark.cn" || parsed.hash) return "";
  const match = parsed.pathname.match(/^\/s\/([A-Za-z0-9]+)$/);
  if (!match) return "";
  if (parsed.search) {
    const pwd = parsed.searchParams.get("pwd");
    if ([...parsed.searchParams.keys()].length !== 1 || !pwd || !/^[A-Za-z0-9]+$/.test(pwd)) return "";
  }
  return match[1];
}
function quarkEntry(value) {
  if (!value || typeof value !== "object") return null;
  const name = typeof value.name === "string" ? value.name : "";
  const path = typeof value.path === "string" ? value.path : "";
  if (!name || !path || name.length > 255 || path.length > 4096 || name.includes("/") || name.includes("\\") || name === "." || name === "..") return null;
  if (path.split("/").some(part => !part || part === "." || part === "..")) return null;
  if (!path.endsWith(name)) return null;
  return { name, path, directory: value.directory === true, size: Number.isFinite(Number(value.size)) && Number(value.size) > 0 ? Number(value.size) : 0 };
}
function formatSize(size) {
  if (!size) return "";
  if (size >= 1048576) return `${(size / 1048576).toFixed(1)} MiB`;
  if (size >= 1024) return `${Math.round(size / 1024)} KiB`;
  return `${size} B`;
}
function xyeIdentify(value) {
  const url = externalUrl(value);
  if (!url) return "";
  let parsed;
  try { parsed = new URL(url); } catch { return ""; }
  if (parsed.protocol !== "https:" || parsed.username || parsed.password || parsed.search) return "";
  const host = parsed.hostname.toLowerCase();
  if (host !== "www.xyebbs.com" && host !== "xyebbs.com") return "";
  const match = parsed.pathname.match(/^\/res-id\/([A-Za-z0-9_-]{1,64})(?:\/(?:downloads|releases))?\/?$/);
  return match ? match[1] : "";
}
function xyeJump(value) {
  const identify = xyeIdentify(value);
  return identify ? `https://www.xyebbs.com/res-id/${identify}/downloads#resource-download-section` : "";
}
function plainField(value, limit) {
  if (typeof value !== "string") return "";
  const text = value.trim();
  if (!text || text.length > limit || /[\u0000-\u001f]/.test(text)) return "";
  return text;
}
function xyeVersion(value) {
  if (!value || typeof value !== "object") return null;
  const shareId = typeof value.shareId === "string" && /^[A-Za-z0-9]{1,64}$/.test(value.shareId) ? value.shareId : "";
  const label = plainField(value.label, 80);
  if (!shareId || !label) return null;
  return { shareId, label, minecraft: plainField(value.minecraft, 32), loader: plainField(value.loader, 32) };
}
function quarkMessage(operation, extra) {
  const message = { type: "mcmod", operation, id: quark.wikiId, shareId: quark.shareId, resourceType: "modpack", ...extra };
  if (quark.xyeUrl) message.xyeUrl = quark.xyeUrl;
  return message;
}
function presetLinks(item) {
  if (isMcmod(item) || isPlatform(item) || resourceTypeOf(item) !== "modpack") return [];
  const links = [];
  const download = externalUrl(item?.downloadUrl);
  if (download) links.push({ name: "Gitee 下载", url: download });
  const share = externalUrl(item?.shareUrl);
  if (share && quarkShareId(share)) links.push({ name: "夸克网盘", url: share });
  return links;
}
function openQuarkChoice(link, item, presetId) {
  const shareId = quarkShareId(link?.url);
  const url = externalUrl(link?.url);
  const wiki = presetId ? 1 : wikiId(item);
  if (!shareId || !url || !wiki || state.link !== "live" || resourceTypeOf(item) !== "modpack") return;
  quark.shareId = shareId;
  quark.url = url;
  quark.xyeUrl = "";
  quark.xyeVersions = [];
  quark.name = link.name || "夸克网盘";
  quark.presetId = presetId || "";
  quark.wikiId = presetId ? 0 : wiki;
  quark.returnFocus = document.activeElement;
  setText("quark-choice-eyebrow", "夸克网盘");
  setText("quark-choice-title", quark.name);
  setText("quark-choice-text", "跳转会打开分享网页。直接下载会进入下载页，由你选择版本；只会转存那一个 .mrpack 或 .zip，不会转存整个分享。");
  if (!ui["quark-choice"].open) ui["quark-choice"].showModal();
  ui["quark-direct"].focus();
}
function openXyeChoice(link, item) {
  quark.presetId = "";
  const source = externalUrl(link?.url);
  const jump = xyeJump(source);
  const wiki = wikiId(item);
  if (!jump || !wiki || state.link !== "live" || resourceTypeOf(item) !== "modpack") return;
  quark.shareId = "";
  quark.url = jump;
  quark.xyeUrl = source;
  quark.xyeVersions = [];
  quark.name = link.name || "XyeBBS";
  quark.wikiId = wiki;
  quark.returnFocus = document.activeElement;
  setText("quark-choice-eyebrow", "XyeBBS");
  setText("quark-choice-title", quark.name);
  setText("quark-choice-text", "跳转会打开 XyeBBS 下载页。直接下载会读取该页的夸克分享，再进入原来的夸克下载页；只会转存你点中的一个整合包。");
  if (!ui["quark-choice"].open) ui["quark-choice"].showModal();
  ui["quark-direct"].focus();
}
function resolveXye() {
  if (!quark.wikiId || !quark.xyeUrl || state.link !== "live") return;
  quark.openingDownload = true;
  if (ui["quark-choice"].open) ui["quark-choice"].close();
  quark.openingDownload = false;
  quark.shareId = "";
  quark.path = "";
  quark.entries = [];
  quark.xyeVersions = [];
  quark.error = "";
  quark.downloadRequestId = "";
  quark.note = "";
  quark.truncated = false;
  quark.xyeTruncated = false;
  if (!ui["quark-download"].open) ui["quark-download"].showModal();
  const requestId = `xye-${++quark.sequence}`;
  quark.requestId = requestId;
  quark.pending = true;
  renderQuarkDownload();
  if (!post({ type: "mcmod", operation: "xyeResolve", id: quark.wikiId, url: quark.xyeUrl, requestId, resourceType: "modpack" })) {
    quark.pending = false;
    quark.error = "无法连接启动器。";
    renderQuarkDownload();
  }
}
function pickXyeVersion(version) {
  if (!version || quark.pending) return;
  quark.shareId = version.shareId;
  quark.path = "";
  quark.entries = [];
  quark.error = "";
  quark.note = "";
  requestQuarkList("");
}
function openQuarkDownloadPage() {
  quark.openingDownload = true;
  if (ui["quark-choice"].open) ui["quark-choice"].close();
  quark.openingDownload = false;
  quark.path = "";
  quark.entries = [];
  quark.error = "";
  quark.downloadRequestId = "";
  quark.note = "";
  quark.truncated = false;
  if (!ui["quark-download"].open) ui["quark-download"].showModal();
  requestQuarkList("");
}
function quarkPost(operation, extra) {
  if (quark.presetId) return { type: "launcher", operation, id: quark.presetId, shareId: quark.shareId, ...extra };
  return quarkMessage(operation, extra);
}
function requestQuarkList(path) {
  quark.downloadRequestId = "";
  if ((!quark.wikiId && !quark.presetId) || !quark.shareId || state.link !== "live") return;
  const requestId = `quark-list-${++quark.sequence}`;
  quark.requestId = requestId;
  quark.path = path;
  quark.entries = [];
  quark.pending = true;
  quark.error = "";
  quark.note = "";
  renderQuarkDownload();
  if (!post(quarkPost("quarkList", { path, requestId }))) {
    quark.pending = false;
    quark.error = "无法连接启动器。";
    renderQuarkDownload();
  }
}
function downloadQuarkVersion(entry) {
  if (!entry || entry.directory || quark.pending || mutatingLocked()) return;
  if (!/\.(?:mrpack|zip)$/i.test(entry.name)) return;
  const requestId = `quark-download-${++quark.sequence}`;
  quark.downloadRequestId = requestId;
  quark.pending = true;
  quark.error = "";
  quark.note = "正在交给启动器处理这个版本…";
  renderQuarkDownload();
  if (!post(quarkPost("quarkDownload", { path: entry.path, requestId }))) {
    quark.pending = false;
    quark.error = "无法连接启动器。";
    renderQuarkDownload();
  }
}
function quarkParent(path) {
  const parts = String(path || "").split("/").filter(Boolean);
  parts.pop();
  return parts.join("/");
}
function receiveQuark(data) {
  if (data.type === "mcmodError") {
    if (data.id && Number(data.id) !== quark.wikiId) return;
    if (data.requestId && data.requestId !== quark.requestId && data.requestId !== quark.downloadRequestId) return;
    quark.pending = false;
    quark.error = String(data.message || "夸克目录读取失败");
    renderQuarkDownload();
    return;
  }
  if (data.type === "xyeVersions") {
    if (data.requestId !== quark.requestId) return;
    quark.pending = false;
    quark.error = "";
    quark.xyeTruncated = data.truncated === true;
    quark.xyeVersions = Array.isArray(data.versions) ? data.versions.map(xyeVersion).filter(Boolean) : [];
    if (!quark.xyeVersions.length) {
      quark.error = "这个 XyeBBS 页面没有可用的夸克分享，请改为跳转。";
      renderQuarkDownload();
      return;
    }
    if (quark.xyeVersions.length === 1) {
      pickXyeVersion(quark.xyeVersions[0]);
      return;
    }
    renderQuarkDownload();
    return;
  }
  if (data.type === "quarkVersions") {
    if (data.requestId !== quark.requestId || data.shareId !== quark.shareId) return;
    quark.pending = false;
    quark.error = "";
    quark.path = typeof data.path === "string" ? data.path : "";
    quark.truncated = data.truncated === true;
    quark.entries = Array.isArray(data.entries) ? data.entries.map(quarkEntry).filter(Boolean) : [];
    renderQuarkDownload();
    return;
  }
  if (data.type === "quarkDownload") {
    if (data.requestId && data.requestId !== quark.downloadRequestId) return;
    if (data.shareId && data.shareId !== quark.shareId) return;
    quark.pending = false;
    quark.note = data.accepted ? "已交给启动器处理这个版本。若弹出授权窗口，请在其中确认；取消则不会转存。" : "已取消，没有转存，也没有打开分享窗口。";
    renderQuarkDownload();
  }
}
function renderQuarkDownload() {
  const dialog = ui["quark-download"];
  if (!dialog) return;
  const choosing = !!(quark.xyeUrl && !quark.shareId);
  setText("quark-download-title", choosing ? "选择 XyeBBS 版本" : "选择版本");
  setText("quark-download-note", choosing ? "先选择 XyeBBS 上的一个版本。随后进入夸克下载页，只会转存你点中的一个整合包。" : "在这个下载页选择要安装的版本。只会转存你点中的一个整合包，不会转存整个分享。");
  setText("quark-download-path", choosing ? "XyeBBS 下载页" : (quark.path || "分享根目录"));
  const task = quark.pending && quark.downloadRequestId ? taskForScope("browseQuark") : {};
  const phase = typeof task.phase === "string" && task.phase && task.phase !== "就绪" ? task.phase : "";
  const pendingText = choosing ? "正在读取 XyeBBS 下载页…" : "正在读取分享目录…";
  const idle = choosing ? (quark.xyeTruncated ? "版本较多，只列出前若干个。其余请改为跳转。" : "选择一个 XyeBBS 版本，随后进入夸克下载页。") : (quark.truncated ? "目录较长，只显示前 200 项。进入子文件夹，或改为跳转。" : "选择一个版本。文件夹只用于进入，不会整夹转存。");
  const status = quark.error || phase || quark.note || (quark.pending ? pendingText : "") || idle;
  setText("quark-download-message", !choosing && quark.truncated && !quark.error && !quark.pending ? `${status} 只显示前 200 项。` : status);
  ui["quark-download-status"].dataset.state = quark.error ? "error" : quark.pending ? "loading" : "ready";
  ui["quark-up"].hidden = choosing || !quark.path;
  ui["quark-up"].disabled = quark.pending;
  ui["quark-download-retry"].hidden = !quark.error;
  ui["quark-download-retry"].disabled = quark.pending;
  ui["quark-download-jump"].disabled = state.link !== "live" || !(quarkShareId(quark.url) || xyeIdentify(quark.url));
  const list = ui["quark-versions"];
  if (choosing) {
    const versions = quark.xyeVersions;
    const versionKey = JSON.stringify(["xye", versions, quark.pending, quark.error]);
    if (list.dataset.key === versionKey) return;
    list.dataset.key = versionKey;
    if (!versions.length) {
      const empty = document.createElement("p");
      empty.className = "panel-description";
      empty.textContent = quark.pending ? pendingText : (quark.error || "没有可安装的夸克版本。");
      list.replaceChildren(empty);
      return;
    }
    list.replaceChildren(...versions.map(version => {
      const button = document.createElement("button");
      button.type = "button";
      const label = document.createElement("span");
      label.textContent = version.label;
      const meta = document.createElement("small");
      meta.textContent = [version.minecraft, version.loader].filter(Boolean).join(" · ") || "进入夸克下载";
      button.append(label, meta);
      button.disabled = quark.pending;
      button.addEventListener("click", () => pickXyeVersion(version));
      return button;
    }));
    return;
  }
  const visible = quark.entries.filter(entry => entry.directory || /\.(?:mrpack|zip)$/i.test(entry.name));
  const key = JSON.stringify([quark.path, visible, quark.pending, mutatingLocked()]);
  if (list.dataset.key === key) return;
  list.dataset.key = key;
  if (!visible.length) {
    const empty = document.createElement("p");
    empty.className = "panel-description";
    empty.textContent = quark.pending ? pendingText : "这个目录里没有可安装的整合包。可以返回上级，或改为跳转。";
    list.replaceChildren(empty);
    return;
  }
  list.replaceChildren(...visible.map(entry => {
    const button = document.createElement("button");
    button.type = "button";
    const label = document.createElement("span");
    label.textContent = entry.name;
    const meta = document.createElement("small");
    meta.textContent = entry.directory ? "进入" : (formatSize(entry.size) || "下载此版本");
    button.append(label, meta);
    button.disabled = quark.pending || (entry.directory ? false : mutatingLocked());
    button.addEventListener("click", () => entry.directory ? requestQuarkList(entry.path) : downloadQuarkVersion(entry));
    return button;
  }));
}
function renderDetail() {
  const item = findDetailItem();
  if (!item) return;
  const remote = isMcmod(item);
  const platformItem = isPlatform(item);
  const id = mcKey(item);
  const key = platformItem ? detailKey(item.source, item.projectId, resourceTypeOf(item)) : "";
  const loaded = remote ? catalog.details.has(id) : platformItem ? platform.details.has(key) : true;
  const pending = remote ? catalog.detailPending.has(id) : platformItem ? platform.detailPending.has(key) : false;
  const error = remote ? catalog.detailErrors.get(id) : platformItem ? platform.detailErrors.get(key) : "";
  const live = state.link === "live";
  const needsFetch = remote || platformItem;
  setText("detail-source", platformItem ? `${sourceName(item.source)} · ${resourceLabel(resourceTypeOf(item))}资料` : remote ? `MC百科 · ${resourceLabel(resourceTypeOf(item))}资料` : "启动器预设 · 整合包资料");
  setText("detail-name", item.name || item.id);
  setText("detail-english", item.englishName || "");
  const shownDescription = platformItem && translatedText(item) ? translatedText(item) : (item.description || "暂无简介。");
  const stat = statLabel(item);
  ui["detail-stat-wrap"].hidden = !stat;
  setText("detail-stat-label", isMcmod(item) ? "浏览量" : "下载量");
  setText("detail-stat", stat);

  renderIntroduction(shownDescription);
  ui["detail-translate"].hidden = !platformItem;
  const translateFailed = translation.errors.get(translationKey(item)) || "";
  ui["detail-translate"].disabled = platformItem && translation.pending.has(translationKey(item)) && !translatedText(item);
  ui["detail-translate"].setAttribute("aria-pressed", String(!!translatedText(item)));
  ui["detail-translate"].setAttribute("aria-busy", String(platformItem && translation.pending.has(translationKey(item))));
  ui["detail-translate"].title = translatedText(item) ? "恢复原文" : translateFailed ? `翻译失败：${translateFailed}，再点重试` : "翻译简介";
  setImage(ui["detail-icon"], packArt(item, "icon"));
  setText("detail-mc", versionSummary(item) || "未提供");
  ui["detail-mc"].title = versionTitle(item);
  ui["detail-versions-wrap"].hidden = new Set(mcVersions(item)).size < 2;
  setText("detail-versions", versionTitle(item));
  setText("detail-version", displayVersion(item) || "未提供");
  ui["detail-version"].previousElementSibling.textContent = resourceLabel(resourceTypeOf(item)) + "版本";
  ui["detail-introduction"].querySelector("summary").textContent = resourceLabel(resourceTypeOf(item)) + "介绍";
  setText("detail-loader", loaders(item).join(" / ") || "未提供");
  setText("detail-authors", authors(item).join("、") || "未提供");
  setText("detail-categories", values(item.categories).map(categoryLabel).join(" · ") || "未提供");
  ui["detail-status"].hidden = !needsFetch || (!!loaded && !pending && !error);
  ui["detail-status"].dataset.state = pending ? "loading" : error ? "error" : "offline";
  ui["detail-status"].setAttribute("aria-busy", String(!!pending));
  setText("detail-message", pending ? "正在获取完整资料与下载来源…" : error || (!live ? "连接启动器后可获取完整详情。" : "完整资料尚未加载。"));
  ui["detail-retry"].hidden = !needsFetch || pending || (!error && !!loaded);
  ui["detail-retry"].disabled = !live || (remote && !catalog.include);
  const cf = !!(remote && loaded && curseForgeUrl(item));
  const mrUrl = modrinthProjectUrl(item);
  const mr = !!((remote && loaded || platformItem) && mrUrl);
  ui["detail-download"].hidden = platformItem ? false : !(cf || (remote && loaded && !!mrUrl));
  ui["detail-download"].disabled = state.link !== "live";
  ui["detail-download"].title = state.link === "live" ? "打开启动器中的版本列表" : "连接启动器后才能选择版本";
  ui["detail-modrinth"].hidden = !mr;
  ui["detail-modrinth"].disabled = state.link !== "live";
  ui["detail-modrinth"].title = mr ? mrUrl : "";
  ui["detail-native"].hidden = remote || platformItem;
  ui["detail-native"].disabled = !live || busy() || !!item.offline;
  const website = externalUrl(item.website);
  ui["detail-website"].hidden = !website;
  ui["detail-website"].disabled = !live;
  ui["detail-website"].textContent = remote ? "在 MC百科查看 ↗" : "访问项目网站 ↗";
  ui["detail-import"].disabled = mutatingLocked();
  ui["detail-import"].hidden = resourceTypeOf(item) !== "modpack";
  const preset = presetLinks(item);
  const remoteLinks = remote && loaded && Array.isArray(item.links) ? item.links.filter(link => externalUrl(link?.url)) : [];
  const links = preset.length ? preset : remoteLinks;
  const quarkSource = resourceTypeOf(item) === "modpack" && links.some(link => quarkShareId(link.url));
  const xyeSource = resourceTypeOf(item) === "modpack" && links.some(link => xyeIdentify(link.url));
  const presetGuidance = quarkSource && preset.length > 1 ? "Gitee 下载会打开发布页。夸克网盘不会直接打开网页，点击后可选跳转，或进入下载页自己选择版本。" : quarkSource && preset.length ? "夸克网盘不会直接打开网页。点击后可选跳转，或进入下载页自己选择版本。直接下载只会转存选中的一个文件。" : preset.length ? "Gitee 下载会打开发布页。也可以导入已有归档。" : "下载、夸克安装与更新在启动器预设卡片中操作；也可导入已有归档。";
  setText("detail-guidance", platformItem ? (mr ? "在启动器中选择版本并下载，或跳转到 Modrinth。" : "在启动器中选择版本并下载，也可访问项目网站查看说明。") : !remote ? presetGuidance : !loaded ? "完整详情加载后显示实际下载来源。" : quarkSource && (cf || mr) ? `可进入 ${cf ? "CurseForge" : "Modrinth"} 文件列表${cf && mr ? "，或跳转到 Modrinth" : ""}，或从夸克网盘进入下载页自己选择版本。` : quarkSource ? "夸克网盘不会直接打开网页。点击后可选跳转，或进入下载页自己选择版本。直接下载只会转存选中的一个文件。" : cf && mr ? "在启动器的 CurseForge 文件列表中选择版本下载，或跳转到 Modrinth。" : cf || mr ? `在启动器的 ${cf ? "CurseForge" : "Modrinth"} 文件列表中选择实际版本下载。${mr && !cf ? "也可跳转到 Modrinth。" : ""}` : resourceTypeOf(item) === "datapack" ? "请从作者链接下载，按说明放入存档的 datapacks 目录。" : "暂无平台下载入口，请从作者链接手动下载后导入。");
  if (resourceTypeOf(item) === "plugin") setText("detail-guidance", "选择已安装的 Paper / Purpur 服务器后，沿用 PCL-MOON 文件列表选择版本，下载到该服务器的 plugins 目录。请先停止服务器，并确认插件来源与兼容版本。");
  if (xyeSource && resourceTypeOf(item) !== "plugin") setText("detail-guidance", `${ui["detail-guidance"].textContent} XyeBBS 点击后可选跳转，或读取下载页中的夸克分享再选择文件。`);
  const linkKey = JSON.stringify([links, live, resourceTypeOf(item)]);
  if (ui["detail-links"].dataset.key !== linkKey) {
    ui["detail-links"].dataset.key = linkKey;
    ui["detail-links"].replaceChildren(...links.map(link => {
      const xye = resourceTypeOf(item) === "modpack" ? xyeIdentify(link.url) : "";
      const shareId = !xye && resourceTypeOf(item) === "modpack" ? quarkShareId(link.url) : "";
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = shareId || xye ? (link.name || (xye ? "XyeBBS" : "夸克网盘")) : `${link.name || "作者提供的地址"} ↗`;
      button.title = shareId || xye ? "选择跳转或直接下载" : link.url;
      button.disabled = !live;
      button.addEventListener("click", () => xye ? openXyeChoice(link, item) : shareId ? openQuarkChoice(link, item, preset.length ? item.id : "") : openExternal(link.url));
      return button;
    }));
  }
  const download = platformItem ? platform.downloads.get(key) : remote ? catalog.downloads.get(id) : null;
  setText("download-status", download?.message || "");
  ui["download-retry"].hidden = !download?.error || (remote && !cf && !mr);
  ui["download-retry"].disabled = state.link !== "live";
}
function downloadMcmod() {
  const item = findDetailItem();
  const wiki = wikiId(item), id = mcKey(item);
  if (!item || !isMcmod(item) || !catalog.details.has(id) || !(curseForgeUrl(item) || modrinthProjectUrl(item)) || state.link !== "live") return;
  if (post({ type: "mcmod", operation: "download", id: wiki, ...resourcePayload(resourceTypeOf(item)) })) {
    // The contract has no success acknowledgement. Report only the hand-off,
    // never a completed download or invented launcher progress.
    catalog.downloads.set(id, { error: false, message: "已请求启动器打开版本列表，请在原生界面选择文件。" });
    openVersionWait(item.name);
    renderDetail();
  } else {
    catalog.downloads.set(id, { error: true, message: "无法连接启动器，请重新连接后重试。" });
    closeVersionWait();
    renderDetail();
  }
}
function downloadCurrent() {
  if (isPlatform(findDetailItem())) downloadPlatform();
  else downloadMcmod();
}

ui.filters.addEventListener("submit", event => event.preventDefault());
for (const id of ["filter-mc", "filter-loader", "filter-category", "filter-mcmod-sort"]) ui[id].addEventListener("change", changeSiteFilters);
ui["filter-sort"].addEventListener("change", () => {
  if (!isRemoteBrowse()) return;
  resetPlatformPages();
  render();
  ensureBrowse();
});
for (const source of ["mcl", "curseforge", "modrinth"]) ui[`source-${source}`].addEventListener("change", () => selectBrowseSource(source, ui[`source-${source}`].checked));
for (const type of Object.keys(RESOURCE_TYPES)) ui[`type-${type}`].addEventListener("click", () => selectResourceType(type));
ui["translate-page"].addEventListener("click", () => {
  translation.page = !translation.page;
  translation.autoDetail = "";
  if (translation.page) translateVisible(true);
  render();
});
ui["detail-translate"].addEventListener("click", () => {
  const item = findDetailItem();
  if (!isPlatform(item)) return;
  const key = translationKey(item);
  ui["detail-introduction"].open = true;
  if (translation.text.has(key)) { translation.text.delete(key); render(); return; }
  requestTranslation(item); render();
  ui["detail-introduction"].scrollIntoView({ block: "nearest" });
});
ui["reset-filters"].addEventListener("click", () => {
  ui.search.value = "";
  clearTimeout(platform.searchTimer);
  platform.searchTimer = 0;
  platform.query = "";
  ui["filter-sort"].value = "relevance";
  ui["filter-mcmod-sort"].value = "default";
  for (const id of ["filter-mc", "filter-loader", "filter-category"]) ui[id].value = "";
  changeSiteFilters();
});
ui["include-mcmod"].addEventListener("change", () => selectBrowseSource("mcmod", ui["include-mcmod"].checked));
function stepBrowsePages(delta) {
  if (catalog.include && supportsResource("mcmod")) {
    const next = catalog.page + delta;
    if (next >= 1 && next <= 500 && (delta < 0 || (catalog.totalPages > 0 && catalog.page < catalog.totalPages))) requestPage(next);
  }
  for (const source of ["curseforge", "modrinth"]) {
    if (!sourceEnabled(source) || !supportsResource(source)) continue;
    const position = platformPosition(source);
    const next = position.page + delta;
    if (next < 1 || next > PLATFORM_PAGE_MAX) continue;
    if (delta > 0 && (!position.totalPages || position.page >= position.totalPages)) continue;
    requestPlatformPage(next, false, source);
  }
}
ui["previous-page"].addEventListener("click", () => { if (!ui["previous-page"].disabled) stepBrowsePages(-1); });
ui["next-page"].addEventListener("click", () => { if (!ui["next-page"].disabled) stepBrowsePages(1); });
ui["mcmod-retry"].addEventListener("click", () => requestPage(catalog.page, true));
for (const source of ["curseforge", "modrinth"]) {
  const prefix = source === "curseforge" ? "platform" : "modrinth";
  ui[`${prefix}-retry`].addEventListener("click", () => requestPlatformPage(platformPosition(source).page, true, source));
}
ui["mcl-config"].addEventListener("click", openServices);
ui["mcl-refresh"].addEventListener("click", refreshMcl);
ui["association-retry"].addEventListener("click", () => { association.pausedKey = ""; association.error = ""; render(); });
ui["close-detail"].addEventListener("click", () => ui["pack-detail"].close());
ui["pack-detail"].addEventListener("cancel", event => {
  if (state.composing || event.isComposing || event.keyCode === 229) event.preventDefault();
});
ui["pack-detail"].addEventListener("close", () => {
  closeVersionWait();
  catalog.detailId = "";
  if (catalog.returnFocus?.isConnected) catalog.returnFocus.focus();
  else ui.search.focus();
});
ui["pack-detail"].addEventListener("click", event => {
  if (event.target === ui["pack-detail"]) ui["pack-detail"].close();
});
ui["detail-retry"].addEventListener("click", () => {
  const item = findDetailItem();
  if (isPlatform(item)) requestPlatformDetail(item, true);
  else requestDetail(item, true);
});
ui["detail-website"].addEventListener("click", () => openExternal(findDetailItem()?.website));
ui["detail-download"].addEventListener("click", downloadCurrent);
ui["version-wait"].addEventListener("cancel", event => {
  if (state.composing || event.isComposing || event.keyCode === 229) {
    event.preventDefault();
    return;
  }
  event.preventDefault();
  closeVersionWait();
  if (ui["pack-detail"].open) ui["pack-detail"].close();
});
ui["detail-modrinth"].addEventListener("click", () => openExternal(modrinthProjectUrl(findDetailItem())));
ui["download-retry"].addEventListener("click", downloadCurrent);
ui["close-quark-choice"].addEventListener("click", () => ui["quark-choice"].close());
ui["quark-choice-cancel"].addEventListener("click", () => ui["quark-choice"].close());
ui["quark-jump"].addEventListener("click", () => { const url = quark.url; ui["quark-choice"].close(); openExternal(url); });
ui["quark-direct"].addEventListener("click", () => quark.xyeUrl ? resolveXye() : openQuarkDownloadPage());
ui["quark-choice"].addEventListener("cancel", event => {
  if (state.composing || event.isComposing || event.keyCode === 229) event.preventDefault();
});
ui["quark-choice"].addEventListener("close", () => {
  if (quark.openingDownload) return;
  if (!ui["quark-download"].open && quark.returnFocus?.isConnected) quark.returnFocus.focus();
});
ui["close-quark-download"].addEventListener("click", () => ui["quark-download"].close());
ui["quark-up"].addEventListener("click", () => { if (!ui["quark-up"].disabled) requestQuarkList(quarkParent(quark.path)); });
ui["quark-download-retry"].addEventListener("click", () => quark.xyeUrl && !quark.shareId ? resolveXye() : requestQuarkList(quark.path));
ui["quark-download-jump"].addEventListener("click", () => openExternal(quark.url));
ui["quark-download"].addEventListener("cancel", event => {
  if (state.composing || event.isComposing || event.keyCode === 229) event.preventDefault();
});
ui["quark-download"].addEventListener("close", () => { quark.requestId = ""; quark.downloadRequestId = ""; });
ui["detail-import"].addEventListener("click", () => { if (!mutatingLocked()) postLauncher("import"); });
ui["detail-native"].addEventListener("click", () => {
  const item = findDetailItem();
  if (!item || isMcmod(item) || isPlatform(item) || ui["detail-native"].disabled) return;
  choose(item.id);
  ui["pack-detail"].close();
  ui.primary.focus();
});

document.addEventListener("compositionstart", () => {
  state.composing = true;
  clearTimeout(platform.searchTimer);
  platform.searchTimer = 0;
});
document.addEventListener("compositionend", () => {
  state.composing = false;
  schedulePlatformSearch();
});
ui["search-form"].addEventListener("submit", event => {
  event.preventDefault();
  if (state.composing || event.isComposing) return;
  clearTimeout(platform.searchTimer);
  platform.searchTimer = 0;
  commitPlatformQuery(true);
});
ui.search.addEventListener("input", event => {
  render();
  if (state.composing || event.isComposing) return;
  schedulePlatformSearch();
});
ui.search.addEventListener("keydown", event => {
  if (event.key === "Escape" && !state.composing && !event.isComposing && event.keyCode !== 229 && ui.search.value) {
    event.preventDefault();
    ui.search.value = "";
    clearTimeout(platform.searchTimer);
    platform.searchTimer = 0;
    if (isRemoteBrowse() && platform.query) {
      platform.query = "";
      resetPlatformPages();
      for (const source of selectedPlatforms()) requestPlatformPage(1, true, source);
    } else render();
  }
});
ui.refresh.addEventListener("click", () => {
  if (ui.refresh.disabled) return;
  markPending("refresh");
  render();
  postLauncher("refresh");
  if (isRemoteBrowse()) {
    invalidatePlatformLists();
    for (const source of selectedPlatforms()) requestPlatformPage(platformPosition(source).page, true, source);
  }
  if (catalog.include) requestPage(catalog.page, true);
  association.pausedKey = ""; association.error = "";
});
ui["open-services"].addEventListener("click", openServices);
ui["notice-config"].addEventListener("click", openServices);
ui["empty-config"].addEventListener("click", openServices);
ui["clear-search"].addEventListener("click", () => {
  ui.search.value = "";
  ui.search.focus();
  clearTimeout(platform.searchTimer);
  platform.searchTimer = 0;
  if (isRemoteBrowse() && platform.query) {
    platform.query = "";
    resetPlatformPages();
    for (const source of selectedPlatforms()) requestPlatformPage(1, true, source);
  } else render();
});
ui.primary.addEventListener("click", () => {
  if (ui.primary.disabled) return;
  const featured = featuredPack();
  if (featured && (isMcmod(featured) || isPlatform(featured) || featured.id !== selectedPack()?.id)) { openDetail(featured.id); return; }
  const pack = selectedPack();
  if (!pack || pack.offline) return;
  if (pack.updateAvailable || !pack.installed) enqueue("primary");
  else postNative("games");
});
let discoveryTimer = 0;
let discoveryHold = 0;
function scheduleDiscovery() {
  clearTimeout(discoveryTimer);
  discoveryTimer = 0;
  if (!state.presented || document.hidden || discoveryPool().length < 2 || Date.now() < discoveryHold) return;
  discoveryTimer = setTimeout(() => {
    discoveryTimer = 0;
    const pool = discoveryPool();
    if (!state.presented || document.hidden || pool.length < 2 || ui["pack-detail"].open || Date.now() < discoveryHold) return;
    browse.feature = (browse.feature + 1) % pool.length;
    render();
  }, DISCOVERY_AUTO_MS);
}
function stepDiscovery(delta) {
  const pool = discoveryPool();
  const next = browse.feature + delta;
  if (!pool.length || next < 0 || next >= pool.length) return;
  browse.feature = next;
  discoveryHold = Date.now() + DISCOVERY_AUTO_MS;
  render();
}
ui["discovery-previous"].addEventListener("click", () => stepDiscovery(-1));
ui["discovery-next"].addEventListener("click", () => stepDiscovery(1));
ui.banner.addEventListener("pointerenter", () => { discoveryHold = Date.now() + DISCOVERY_AUTO_MS; scheduleDiscovery(); });
ui.banner.addEventListener("pointerleave", () => { discoveryHold = 0; scheduleDiscovery(); });
document.addEventListener("visibilitychange", scheduleDiscovery);
ui["recommend-list"].addEventListener("pointerover", event => {
  const row = event.target.closest(".rec");
  if (!row || row.contains(event.relatedTarget)) return;
  const pool = discoveryPool();
  const index = pool.findIndex(item => item.id === row.dataset.packId);
  if (index < 0 || index === browse.feature) return;
  browse.feature = index;
  discoveryHold = Date.now() + DISCOVERY_AUTO_MS;
  render();
});
ui["recommend-list"].addEventListener("wheel", event => {
  const list = ui["recommend-list"];
  if (list.scrollHeight <= list.clientHeight + 1 || !event.deltaY || event.deltaX) return;
  list.scrollTop += event.deltaY;
  event.preventDefault();
}, { passive: false });
ui.cancel.addEventListener("click", () => {
  if (ui.cancel.disabled) return;
  state.pending = null;
  state.queued = null;
  state.localNote = text().sent;
  render();
  postLauncher("cancel");
});
ui["goto-versions"].addEventListener("click", () => postNative("versions"));
ui.more.addEventListener("click", () => { if (ui["more-menu"].hidden) openMenu(); else closeMenu(); });
ui["more-menu"].addEventListener("keydown", event => {
  if (state.composing || event.isComposing || event.keyCode === 229) return;
  const items = [...ui["more-menu"].querySelectorAll("button")];
  const index = items.indexOf(document.activeElement);
  if (event.key === "Escape") { event.preventDefault(); closeMenu(true); return; }
  if (event.key !== "ArrowDown" && event.key !== "ArrowUp" && event.key !== "Home" && event.key !== "End") return;
  event.preventDefault();
  const next = event.key === "ArrowDown" ? index + 1 : event.key === "ArrowUp" ? index - 1 : event.key === "Home" ? 0 : items.length - 1;
  items[(next + items.length) % items.length]?.focus();
});
document.addEventListener("click", event => { if (!event.target.closest("#more-wrap")) closeMenu(); });
document.addEventListener("keydown", event => {
  if (state.composing || event.isComposing || event.keyCode === 229) {
    if (event.key === "Escape" && ui["pack-detail"].open) event.preventDefault();
    return;
  }
  if (event.key === "Escape" && !ui["more-menu"].hidden && !ui.services.open) { closeMenu(true); return; }
  if (event.key === "/" && document.activeElement === document.body) { event.preventDefault(); ui.search.focus(); }
  if (event.target.closest("#cards")) {
    const cols = getComputedStyle(ui.cards).gridTemplateColumns.split(" ").filter(Boolean).length || 1;
    moveFocus(ui.cards, event, cols);
  } else if (event.target.closest("#recommend-list")) moveFocus(ui["recommend-list"], event, 1);
});
for (const [id, page] of [["goto-version-settings", "versionSettings"], ["goto-tasks", "tasks"], ["goto-games", "games"], ["goto-account", "account"]]) {
  ui[id].addEventListener("click", () => { closeMenu(); postNative(page); });
}
 ui["check-updates"].addEventListener("click", () => { closeMenu(); if (!ui["check-updates"].disabled) { markPending("checkUpdates"); render(); postLauncher("checkUpdates"); } });
ui["install-quark"].addEventListener("click", () => { closeMenu(); enqueue("installQuark"); });
ui["authorize-quark"]?.addEventListener("click", () => {
  if (ui["authorize-quark"].disabled) return;
  markPending("authorizeQuark");
  render();
  postLauncher("authorizeQuark");
});
ui["sponsor-link"].addEventListener("click", event => {
  event.preventDefault();
  openExternal(ui["sponsor-link"].getAttribute("href"));
});
 ui["import-pack"].addEventListener("click", () => { closeMenu(); if (!ui["import-pack"].disabled) postLauncher("import"); });
ui["launch-installed"].addEventListener("click", () => { closeMenu(); enqueue("primary"); });
for (const id of ["close-services", "dismiss-services"]) ui[id].addEventListener("click", () => ui.services.close());
ui.services.addEventListener("close", () => ui["open-services"].focus());
for (const id of ["catalog-url", "quark-url"]) ui[id].addEventListener("input", () => { ui[id].setCustomValidity(""); updateUrlWarning(); });
ui["catalog-default"].addEventListener("click", () => {
  ui["catalog-url"].value = catalogDefaultUrls().join("\n");
  ui["catalog-url"].setCustomValidity("");
  updateUrlWarning();
});
ui["services-form"].addEventListener("submit", event => {
  event.preventDefault();
  if (ui["save-services"].disabled) return;
  const t = text();
  const node = ui["catalog-url"];
  let urls;
  try { urls = parseCatalogUrls(node.value); node.setCustomValidity(""); }
  catch (error) { node.setCustomValidity(error.message); node.reportValidity(); return; }
  if (!node.reportValidity()) return;
  const quark = ui["quark-url"].value.trim();
  ui["quark-url"].setCustomValidity(classifyUrl(quark).ok ? "" : t.urlInvalid);
  if (!ui["quark-url"].reportValidity()) return;
  const values = { catalogUrls: urls, catalogUrl: urls[0] || "", quarkServiceUrl: quark };
  state.configSent = values;
  state.configNote = t.configWait;
   markPending("configure");
  render();
  postLauncher("configure", values);
});

state.link = connected() ? "connecting" : "offline";
if (state.link === "connecting") state.connectDeadline = Date.now() + 8000;
render();
if (connected()) {
  window.chrome.webview.addEventListener("message", event => receive(event.data));
  post({ type: "bootstrap" });
}

function ensureMcl() {
  if (!sourceEnabled("mcl") || browse.resourceType !== "modpack" || state.link !== "live" || busy() || taskForScope("catalog").error) return;
  const urls = effectiveCatalogUrls(), key = JSON.stringify(urls);
  if (!urls.length || urls.includes(state.launcher?.config?.catalogSource) || mcl.requestedKey === key) return;
  mcl.requestedKey = key;
  postLauncher("refresh");
}
function refreshMcl() {
  if (state.link !== "live" || busy()) return;
  mcl.requestedKey = JSON.stringify(effectiveCatalogUrls());
  markPending("refresh"); render(); postLauncher("refresh");
}
function renderMclExplorer() {
  const shown = sourceEnabled("mcl") && browse.resourceType === "modpack";
  ui["mcl-status"].hidden = !shown;
  if (!shown) return;
  const urls = effectiveCatalogUrls(), source = state.launcher?.config?.catalogSource || "";
  const live = state.link === "live";
   setText("mcl-message", !live ? "MCL · 未连接启动器，目录状态未知" : source ? `MCL · ${packs().length} 个条目 · 上次成功目录：${source}` : urls.length ? (catalogDefaultActive() ? "MCL · 未配置目录源，正在使用 Gitee 默认源；内置测试包仍可用。" : "MCL · 已配置远端 index.json，等待启动器返回目录；内置测试包仍可用。") : "MCL · 请在目录配置中填写远端 index.json；当前仅有内置测试包。");
  ui["mcl-refresh"].disabled = !live || busy();
  ui["mcl-config"].disabled = !live;
}
function associationScope() { return JSON.stringify([filterKey(), catalog.page, selectedPlatforms()]); }
function associationCandidates() {
  if (!state.presented || !catalog.include || !isRemoteBrowse() || !supportsResource("mcmod") || state.link !== "live") return [];
  const items = catalogItems();
  if (!items.some(isPlatform)) return [];
  return items.filter(item => isMcmod(item) && !catalog.details.has(mcKey(item)) && ExplorerIdentity.keys(item).size === 0);
}
function renderAssociationStatus() {
  const shown = catalog.include && isRemoteBrowse() && supportsResource("mcmod");
  ui["association-status"].hidden = !shown;
  if (!shown) return;
  const paused = association.pausedKey === associationScope();
  setText("association-message", state.link !== "live" ? "连接启动器后可核对百科关联项目。" : paused ? `百科关联尚未核对完成：${association.error}；未确认的关联不会按名称猜测。` : association.active || associationCandidates().length ? "正在按限速核对百科项目链接，确认后移除对应的 CF / Modrinth 条目…" : "已按已确认的百科项目链接合并显示，各来源轮流排列。");
  ui["association-retry"].hidden = !paused;
  ui["association-retry"].disabled = state.link !== "live" || !!association.active;
}
function queueAssociationLookup() {
  if (association.active || association.timer || association.pausedKey === associationScope() || !associationCandidates().length) return;
  association.timer = setTimeout(() => {
    association.timer = 0;
    const item = associationCandidates()[0];
    if (!item || association.active || association.pausedKey === associationScope()) return;
    const requestId = `mcmod-links-${++association.sequence}`;
    const active = { key: mcKey(item), resourceType: resourceTypeOf(item), requestId, scope: associationScope(), timeout: 0 };
    association.active = active;
    active.timeout = setTimeout(() => finishAssociation(null, "请求超时，请稍后重试"), REQUEST_TIMEOUT);
    if (!post({ type: "mcmod", operation: "links", id: wikiId(item), ...resourcePayload(active.resourceType), requestId })) finishAssociation(null, "连接已断开");
    else { renderAssociationStatus(); renderSourceSummary(); }
  }, 500);
}
function finishAssociation(data, error = "") {
  const active = association.active;
  if (!active || data && data.requestId !== active.requestId) return;
  clearTimeout(active.timeout); association.active = null;
  const item = !error && data ? normalizeMcmod(data.item) : null;
  if (item && mcKey(item) === active.key && resourceTypeOf(item) === active.resourceType) catalog.details.set(active.key, item);
  else { association.pausedKey = active.scope; association.error = error || "返回的项目身份不符"; }
  render();
}

const browseSwitchSections = [".toolbar", "#notice", "#error", ".discover-controls", ".discovery-title", ".stage", ".catalog", ".foot"];
function playBrowseSwitch() {
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  const nodes = browseSwitchSections
    .map(selector => document.querySelector(selector))
    .filter(node => node && !node.hidden && node.getClientRects().length > 0);
  nodes.forEach((node, index) => {
    node.classList.remove("page-enter");
    node.style.setProperty("--enter-delay", `${index * 25}ms`);
  });
  void document.body.offsetWidth;
  for (const node of nodes) node.classList.add("page-enter");
}
function applyPresentation(visible) {
  if (typeof visible !== "boolean") return;
  state.presented = visible;
  if (!visible) {
    closeVersionWait();
    clearTimeout(association.timer); association.timer = 0;
    if (association.active) { clearTimeout(association.active.timeout); association.active = null; }
  } else if (state.link === "connecting") {
    state.connectDeadline = Date.now() + 8000;
    state.hostError = "";
    post({ type: "bootstrap" });
  }
  render();
  if (visible) {
    playBrowseSwitch();
    ensureBrowse();
  }
}
setInterval(() => {
  if (state.link !== "connecting" || !state.connectDeadline || Date.now() < state.connectDeadline) return;
  state.link = "lost";
  state.connectDeadline = 0;
  state.hostError = text().connectTimeout;
  render();
}, 1000);

function openVersionWait(name) {
  const label = typeof name === "string" && name ? name : "";
  setText("version-wait-title", "正在打开版本列表");
  setText("version-wait-text", "已请求启动器打开版本列表，请在原生界面选择文件。");
  setText("version-wait-note", label ? `正在解析「${label}」，完成后会自动跳转。` : "正在解析项目，完成后会自动跳转。");
  const dialog = ui["version-wait"];
  if (dialog && !dialog.open) dialog.showModal();
}
function closeVersionWait() {
  const dialog = ui["version-wait"];
  if (dialog?.open) dialog.close();
}

function modrinthProjectUrl(item) {
  const type = resourceTypeOf(item);
  for (const value of projectUrls(item)) {
    const key = ExplorerIdentity.projectLink(value, type);
    if (key && key.startsWith(`${type}:modrinth:`) && new URL(value).protocol === "https:") return value;
  }
  return "";
}

function categoryFor(source) {
  const value = ui["filter-category"].value;
  if (/^\d+$/.test(value)) return source === "mcmod" && browse.resourceType === "modpack" ? value : "";
  const split = value.indexOf(":");
  return split > 0 && value.slice(0, split) === source ? value.slice(split + 1) : "";
}
function categoryOptions() {
  const options = [];
  for (const source of ["curseforge", "modrinth", "mcmod"]) {
    if (!sourceEnabled(source) || !supportsResource(source)) continue;
    if (source === "mcmod" && browse.resourceType === "modpack") {
      options.push(...SITE_CATEGORIES.map((name, i) => [String(i + 1), name])); continue;
    }
    const items = categoryState.items.get(`${source}:${browse.resourceType}`) || [];
    options.push(...items.map(item => [`${source}:${item.id}`, `${sourceName(source)} · ${categoryLabel(item.name)}`]));
  }
  return options;
}
function ensureCategories(force = false) {
  if (state.link !== "live" || !state.presented) return;
  for (const source of ["curseforge", "modrinth", "mcmod"]) {
    if (!sourceEnabled(source) || !supportsResource(source) || source === "mcmod" && browse.resourceType === "modpack") continue;
    const key = `${source}:${browse.resourceType}`;
    if (categoryState.pending.has(key) || !force && (categoryState.items.has(key) || categoryState.errors.has(key))) continue;
    const requestId = `categories-${++categoryState.sequence}`;
    categoryState.requests.set(requestId, { source, resourceType: browse.resourceType, key });
    categoryState.errors.delete(key);
    categoryState.pending.set(key, setTimeout(() => {
      categoryState.pending.delete(key); categoryState.errors.set(key, "分类请求超时");
      categoryState.requests.delete(requestId); render();
    }, PLATFORM_TIMEOUT));
    if (!post({ type: source === "mcmod" ? "mcmod" : "platform", operation: "categories", source, ...resourcePayload(), requestId })) {
      clearRequest(categoryState.pending, key); categoryState.requests.delete(requestId);
      categoryState.errors.set(key, "分类请求未发送");
    }
  }
}
function storeCategories(source, resourceType, items) {
  const unique = new Map();
  for (const item of items) {
    const id = String(item?.id ?? ""), name = typeof item?.name === "string" ? item.name.trim() : "";
    if (!name || name.length > 200 || !/^[A-Za-z0-9_+.\-]{1,64}$/.test(id)) continue;
    unique.set(id, { id, name });
    if (unique.size >= 512) break;
  }
  categoryState.items.set(`${source}:${resourceType}`, [...unique.values()]);
}
function receiveCategories(data) {
  const request = categoryState.requests.get(data.requestId);
  if (!request || (data.resourceType || "modpack") !== request.resourceType || (data.source || "mcmod") !== request.source) return;
  clearRequest(categoryState.pending, request.key); categoryState.requests.delete(data.requestId);
  if (data.type.endsWith("Error") || !Array.isArray(data.items)) categoryState.errors.set(request.key, String(data.message || "分类数据格式不正确"));
  else { storeCategories(request.source, request.resourceType, data.items); categoryState.errors.delete(request.key); }
  render();
}
function renderCategoryHelp() {
  const sources = ["curseforge", "modrinth", "mcmod"].filter(s => sourceEnabled(s) && supportsResource(s));
  const loading = sources.filter(s => categoryState.pending.has(`${s}:${browse.resourceType}`));
  const errors = sources.filter(s => categoryState.errors.has(`${s}:${browse.resourceType}`));
  const selected = ui["filter-category"].selectedOptions[0];
  setText("category-help", errors.length ? `分类暂不可用：${errors.map(sourceName).join("、")}` : loading.length ? `正在加载 ${loading.map(sourceName).join("、")} 分类…` : ui["filter-category"].value ? `${selected?.textContent || "分类"}；仅筛选对应来源` : categoryOptions().length ? "分类标签按所选来源生成" : "当前来源暂无可用分类");
  ui["category-retry"].hidden = errors.length === 0;
  ui["category-retry"].disabled = state.link !== "live";
}
function renderSourceSummary() {
  const segments = [], loading = [], errors = [];
  for (const source of ["mcl", "curseforge", "modrinth", "mcmod"]) {
    if (!sourceEnabled(source) || source === "mcl" && browse.resourceType !== "modpack") continue;
    const name = sourceName(source);
    if (!supportsResource(source) && source !== "mcl") { segments.push(`${name}：此类型不可用`); continue; }
    if (state.link !== "live") { segments.push(`${name}：未连接`); continue; }
    if (source === "mcl") {
      segments.push(`MCL ${packs().length}`);
      if (!effectiveCatalogUrls().length) segments.push("MCL 目录未配置");
      if (taskForScope("catalog").busy) loading.push("MCL");
      if (taskForScope("catalog").error) errors.push(`MCL：${taskForScope("catalog").error}`);
      continue;
    }
    const key = source === "mcmod" ? pageKey() : platformPageKey(platformPosition(source).page, source);
    const pending = source === "mcmod" ? catalog.listPending.has(key) : platform.listPending.has(key);
    const error = source === "mcmod" ? catalog.listErrors.get(key) : platform.listErrors.get(key);
    const page = source === "mcmod" ? catalog.pages.get(key) : platform.pages.get(key);
    if (pending) loading.push(name);
    if (error) errors.push(`${name}：${error}`);
    if (page) segments.push(`${name} ${page.items.length}`);
    else if (!pending && !error) segments.push(`${name}：等待加载`);
  }
  const count = catalogItems().filter(matchesFilters).length;
  let message = `当前页 ${count} 个${resourceLabel()}（合并去重后）`;
  if (segments.length) message += ` · ${segments.join(" / ")}`;
  if (loading.length) message += ` · 加载中：${loading.join("、")}`;
  if (errors.length) message += ` · ${errors.join("；")}`;
  if (catalog.include && isRemoteBrowse() && supportsResource("mcmod")) message += ` · ${ui["association-message"].textContent}`;
  setText("sources-summary-message", message);
  ui["sources-summary"].hidden = browse.sources.size === 0;
  ui["sources-summary"].dataset.state = errors.length ? "error" : loading.length ? "loading" : "ready";
  ui["sources-summary"].setAttribute("aria-busy", String(loading.length > 0));
  const actions = ui["sources-summary-actions"];
  for (const id of ["mcl-refresh", "mcl-config", "platform-retry", "modrinth-retry", "mcmod-retry", "association-retry"]) {
    const node = ui[id];
    if (id.startsWith("mcl-")) node.hidden = !sourceEnabled("mcl") || browse.resourceType !== "modpack";
    const owner = { "platform-retry": "curseforge", "modrinth-retry": "modrinth", "mcmod-retry": "mcmod" }[id];
    if (owner && (!sourceEnabled(owner) || !supportsResource(owner))) node.hidden = true;
    if (id === "association-retry" && (!catalog.include || !isRemoteBrowse() || !supportsResource("mcmod"))) node.hidden = true;
    if (node.parentElement !== actions) actions.append(node);
  }
  for (const id of ["mcl-status", "platform-status", "modrinth-status", "mcmod-status", "association-status"]) ui[id].hidden = true;
}
ui["category-retry"].addEventListener("click", () => { ensureCategories(true); render(); });

function openPluginBrowser(minecraft) {
  selectResourceType("plugin");
  if (!isRemoteBrowse()) selectBrowseSource("modrinth", true);
  if (typeof minecraft === "string" && /^[0-9A-Za-z.\-_]+$/.test(minecraft)) {
    const select = ui["filter-mc"];
    if (![...select.options].some(option => option.value === minecraft)) {
      const option = document.createElement("option"); option.value = minecraft; option.textContent = minecraft; select.append(option);
    }
    select.value = minecraft; changeSiteFilters();
  }
  ui["type-plugin"].focus();
  ui["type-plugin"].focus();
}

function hostServers() {
  const list = Array.isArray(state.launcher?.servers) ? state.launcher.servers : [];
  return list.filter(item => item && typeof item.name === "string")
    .map(item => ({ ...item, source: item.source || "mcl", local: item.local === true, directory: item.directory === true || item.kind === "directory", packs: Array.isArray(item.packs) ? item.packs : [] }));
}
function isDirectory(item) {
  const host = String(item?.address || "").replace(/:\d+$/, "");
  return item?.directory === true || item?.kind === "directory" || /(?:^|\.)mclists\.cn$/i.test(host);
}
function visibleServers() {
  const query = queryText();
  const merged = new Map();
  for (const item of [...hostServers(), ...(servers.includeMclists ? servers.items : [])]) {
    if (isDirectory(item) || !item.address || query && ![item.name, item.address, item.minecraft, item.description].some(part => String(part || "").toLocaleLowerCase().includes(query))) continue;
    merged.set(`${item.source || "mcl"}:${item.address || item.id || item.name}`, item);
  }
  return [...merged.values()];
}
function loadServers() {
  if (!servers.includeMclists) { servers.loading = false; servers.error = ""; renderServers(); return; }
  servers.loading = true; servers.error = "";
  renderServers();
  if (!postLauncher("servers", {})) { servers.loading = false; servers.error = "连接启动器后加载 MCLists。"; renderServers(); }
}
function receiveServers(data) {
  servers.loading = false;
  servers.items = Array.isArray(data.items) ? data.items.filter(item => item && typeof item.name === "string").map(item => ({ ...item, source: "mclists", packs: item.packs || [] })) : [];
  servers.error = data.message || (servers.items.length ? "" : "MCLists 没有返回可展示的服务器。");
  renderServers();
}
function renderServers() {
  if (!ui.servers) return;
  const items = visibleServers();
  setText("servers-count", `${items.length} 个服务器`);
  setText("servers-message", !servers.includeMclists ? "MCLists 是资源来源，默认不作为服务器。勾选后只用来源站查找，仍不会被当成可进入服务器。" : servers.loading ? "正在读取 MCLists…" : servers.error || "来源：MCLists 与 MCL index.json");
  ui["servers-empty"].hidden = items.length > 0;
  ui["server-cards"].replaceChildren(...items.map(serverCard));
}
function serverCard(item) {
  const card = document.createElement("article");
  card.className = "card";
  const body = document.createElement("div");
  body.className = "card-body";
  if (item.local && typeof item.icon === "string" && item.icon.startsWith("data:image/")) {
    const icon = document.createElement("img");
    icon.alt = "";
    icon.width = 48;
    icon.height = 48;
    icon.referrerPolicy = "no-referrer";
    icon.src = item.icon;
    body.append(icon);
  }
  const title = document.createElement("strong");
  title.textContent = item.name;
  const summary = document.createElement("p");
  summary.className = "card-summary";
  summary.textContent = item.description || item.address || "未提供简介";
  const meta = document.createElement("small");
  meta.textContent = [item.local ? "本地" : item.source === "mclists" ? "MCLists" : "MCL", item.minecraft, item.vanilla ? "原版" : "", item.address].filter(Boolean).join(" · ");
  const join = document.createElement("button");
  join.type = "button";
  join.textContent = item.address ? "一键进入" : "查看来源";
  join.addEventListener("click", () => item.address ? openServerJoin(item) : post({ type: "openExternal", url: item.website || MCLISTS }));
  body.append(title, summary, meta, join);
  if (item.local) {
    const remove = document.createElement("button");
    remove.type = "button";
    remove.textContent = "删除";
    remove.addEventListener("click", () => postLauncher("removeLocalServer", { id: item.id || "" }));
    body.append(remove);
  }
  card.append(body);
  return card;
}
function instanceMatches(entry, minecraft) {
  if (!minecraft) return true;
  const exact = String(entry.minecraft || "").trim();
  if (exact) return exact.toLocaleLowerCase() === minecraft.toLocaleLowerCase();
  return false;
}
function openServerJoin(item) {
  servers.selected = item;
  setText("server-join-title", item.name);
  setText("server-join-note", `${item.address} · MC ${item.minecraft || "版本未知"}${item.vanilla ? " · 支持原版客户端" : ""} · 使用 PCL-MOON javaagent 进入；加载失败时游戏继续启动，不会因此崩溃`);
  const box = ui["server-choices"];
  box.replaceChildren();
  const instances = Array.isArray(state.launcher?.instances) ? state.launcher.instances : [];
  const idle = instances.filter(entry => entry.running && entry.session && entry.linkState !== "connected" && entry.linkState !== "connecting");
  const busy = instances.filter(entry => entry.running && (entry.linkState === "connected" || entry.linkState === "connecting"));
  appendJoinGroup(box, "已启动且未进服", idle.map(entry => ({
    title: entry.name,
    detail: `MC ${entry.minecraft || "版本未知"} · ${linkStateText(entry.linkState)}${entry.linkAddress ? " · " + entry.linkAddress : ""} · 直接进入，不重新启动`,
    action: "进入",
    run: () => joinServer(item, { session: entry.session })
  })));
  appendJoinGroup(box, "已在服务器中", busy.map(entry => ({
    title: entry.name,
    detail: `${linkStateText(entry.linkState)}${entry.linkAddress ? " · " + entry.linkAddress : ""} · 请先在游戏里退出当前服务器`,
    action: "不可用",
    disabled: true
  })));
  const matching = instances.filter(entry => !entry.running && instanceMatches(entry, item.minecraft));
  const packs = matching.filter(entry => entry.vanilla !== true);
  const vanilla = matching.filter(entry => entry.vanilla === true);
  const others = instances.filter(entry => !entry.running && !instanceMatches(entry, item.minecraft));
  appendJoinGroup(box, "已安装客户端整合包", packs.map(entry => ({
    title: entry.name, detail: `MC ${entry.minecraft || item.minecraft} · 启动时注入 javaagent`, action: "启动",
    run: () => joinServer(item, { instance: entry.name })
  })));
  appendJoinGroup(box, "原版客户端", [
    ...vanilla.map(entry => ({ title: entry.name, detail: `MC ${entry.minecraft} · 启动时注入 javaagent`, action: "启动", run: () => joinServer(item, { instance: entry.name }) })),
    ...(item.vanilla && item.minecraft ? [{ title: `原版 ${item.minecraft}`, detail: "没有原版实例时自动下载", action: "下载", run: () => joinServer(item, { choice: "vanilla" }) }] : [])
  ]);
  appendJoinGroup(box, "其他已有游戏", others.map(entry => ({
    title: entry.name, detail: `MC ${entry.minecraft || "版本未知"} · 与服务器版本不同，仍可启动进入`, action: "启动",
    run: () => joinServer(item, { instance: entry.name, mismatch: true })
  })));
  const seen = new Set();
  const offers = [...(item.packs || []), { id: "fabulously-optimized", name: "Fabulously Optimized", minecraft: item.minecraft, loader: "Fabric" }]
    .filter(offer => {
      const key = offer.id || offer.name;
      if (!key || seen.has(key) || (item.minecraft && offer.minecraft && offer.minecraft !== item.minecraft)) return false;
      seen.add(key);
      return true;
    });
  appendJoinGroup(box, "推荐整合包", offers.map(offer => ({
    title: offer.name, detail: [offer.loader, offer.minecraft].filter(Boolean).join(" · "), action: "下载",
    run: () => joinServer(item, { choice: `pack:${offer.id}`, packName: offer.name, loader: offer.loader || "" })
  })));
  if (!box.childElementCount) box.append(choiceButton("没有可直接进入的版本，打开来源", () => post({ type: "openExternal", url: item.website || MCLISTS })));
  box.append(choiceButton("返回到服务器列表", () => ui["server-join"].close()));
  if (!ui["server-join"].open) ui["server-join"].showModal();
  ui["server-join"].scrollTop = 0;
}
function appendJoinGroup(box, title, rows) {
  if (!rows.length) return;
  const group = document.createElement("section");
  group.className = "join-group";
  const heading = document.createElement("h3");
  heading.textContent = title;
  group.append(heading, ...rows.map(row => joinRow(row)));
  box.append(group);
}
function joinRow(row) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "join-row";
  const copy = document.createElement("span");
  const name = document.createElement("strong");
  name.textContent = row.title;
  const detail = document.createElement("small");
  detail.textContent = row.detail || "";
  copy.append(name, detail);
  const action = document.createElement("em");
  action.textContent = row.action;
  button.append(copy, action);
  if (row.disabled) button.disabled = true;
  else button.addEventListener("click", () => { ui["server-join"].close(); row.run(); });
  return button;
}
function choiceButton(label, action) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = label;
  button.addEventListener("click", () => { ui["server-join"].close(); action(); });
  return button;
}
function linkStateText(state) {
  return { waiting: "正在加载", ready: "在主菜单", connecting: "正在连接", connected: "已在服务器", disconnected: "未进服", unsupported: "javaagent 未适配，游戏继续运行", error: "上次进入失败，可重试" }[state] || "已启动";
}
function joinServer(item, extra) {
  postLauncher("joinServer", { id: item.id || "", address: item.address, minecraft: item.minecraft || "", vanilla: !!item.vanilla, ...extra });
}
ui["servers-refresh"]?.addEventListener("click", loadServers);
ui["servers-add"]?.addEventListener("click", () => { if (!ui["local-server"].open) ui["local-server"].showModal(); ui["local-server-name"].focus(); });
ui["local-server-cancel"]?.addEventListener("click", () => ui["local-server"].close());
ui["close-local-server"]?.addEventListener("click", () => ui["local-server"].close());
ui["local-server-form"]?.addEventListener("submit", event => {
  event.preventDefault();
  postLauncher("addLocalServer", { name: ui["local-server-name"].value.trim(), address: ui["local-server-address"].value.trim(), minecraft: ui["local-server-version"].value.trim() });
  ui["local-server-form"].reset();
  ui["local-server"].close();
});
ui["source-mclists"]?.addEventListener("change", () => { servers.includeMclists = ui["source-mclists"].checked; loadServers(); });
ui["close-server-join"]?.addEventListener("click", () => ui["server-join"].close());
ui["server-join-cancel"]?.addEventListener("click", () => ui["server-join"].close());
loadServers();
 function fitRecommend() {
   const stage = document.querySelector(".stage");
   const aside = stage?.querySelector(".recommend");
   const banner = ui.banner;
   if (!aside || !banner) return;
   if (stage.hidden || getComputedStyle(stage).gridTemplateColumns.split(" ").length < 2) {
     aside.style.maxHeight = "";
     return;
   }
   aside.style.maxHeight = `${Math.ceil(banner.getBoundingClientRect().height)}px`;
 }
 window.addEventListener("resize", fitRecommend);
 if (document.fonts?.ready) document.fonts.ready.then(fitRecommend);
