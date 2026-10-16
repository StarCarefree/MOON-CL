"use strict";
/* Browse bridge. Host owns the native shell. This page never draws it and never
   posts projection/navigate/window/action messages.
   Uplink: {type:'launcher', operation, ...params} and {type:'native', page}.
   Real HandleAsync ops used here: select(id), refresh, primary, installQuark,
   cancel, configure(catalogUrls, catalogUrl, quarkServiceUrl), authorizeQuark, checkUpdates, import.
   primary = download if not installed, Packwiz update if updateAvailable,
   launch only from the more-menu when installed and no update is pending.
   Do not send install/launch/update/login/directory — those names are not the
   browse contract (login/directory exist in C# but navigation goes through native).
   Downlink: bootstrap, browse {launcher}, error {message}. A legacy page
   snapshot is accepted only to read launcher; its control tree is ignored. */

const SRE_ID = "starrail-harpy-express";
const SRE_COVER = "sre-cover.png";
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
    recommend: "本地预设", recommendNote: "来自启动器的整合包目录", catalog: "探索整合包",
    test: "SRE · 置顶测试包", catalogBadge: "目录整合包", offlineState: "离线 · 安装状态未知",
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
    badSnapshot: "浏览数据无法显示。",
    gameRunning: "游戏运行中。下载、更新和夸克安装已禁用，请先在原生界面关闭游戏。",
    locked: "任务进行中，条目选择已暂时锁定。",
    catalogMissing: "尚未配置额外预设源。你仍可浏览 MC百科，或配置自己的目录。",
    catalogReady: "",
    emptyHost: "启动器没有返回整合包条目。",
    emptySearch: "没有匹配的整合包。",
    recommendOffline: "连接启动器后浏览更多预设。",
    recommendNone: "暂无其他预设，可在下方浏览 MC百科。",
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
    pageTitle: "整合包目录",
    eyebrow: "MOONCL · DISCOVER"
  },
  en: {
    connection: { offline: "Static preview · launcher not connected", connecting: "Connecting to launcher", live: "Launcher connected", lost: "Connection lost" },
    search: "Search", placeholder: "Name, version, or description", refresh: "Refresh", services: "Services",
    recommend: "Launcher presets", recommendNote: "From your launcher catalog", catalog: "Discover packs",
    test: "SRE · Pinned test pack", catalogBadge: "Catalog pack", offlineState: "Offline · install state unknown",
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
    badSnapshot: "Browse data could not be shown.",
    gameRunning: "The game is running. Download, update, and Quark install are disabled.",
    locked: "A task is running, so selection is locked.",
    catalogMissing: "No extra preset source configured. Browse MC百科 below, or add your own catalog.",
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
    pageTitle: "Pack catalog",
    eyebrow: "MOONCL · DISCOVER"
  }
};

const state = {
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
  configSent: null,
  configNote: "",
  lastSelectionKey: ""
};
// List caches are isolated by site filters + page, with requestId guarding retries
// and same-page races. Detail caches remain wiki-id scoped, never launcher state.
const COMMON_MC = ["1.21.1", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2"];
const SITE_LOADERS = ["Forge", "Fabric", "Quilt", "NeoForge", "LiteLoader", "Rift", "数据包", "行为包"];
const SITE_CATEGORIES = ["科技", "魔法", "冒险", "建筑", "地图", "任务", "硬核", "休闲", "大型", "轻量", "剧情", "水槽", "空岛", "PvP", "国创"];
const REQUEST_TIMEOUT = 45000;
const catalog = {
  include: true, page: 1, totalPages: 0, sequence: 0, retained: new Map(),
  pages: new Map(), listPending: new Map(), listErrors: new Map(),
  requests: new Map(), latest: new Map(),
  details: new Map(), detailPending: new Map(), detailErrors: new Map(),
  downloads: new Map(), viewedId: "", detailId: "", returnFocus: null
};
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
  if (value === SRE_COVER) return value;
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
  if (state.link !== "live" || !state.launcher) return [{ ...OFFLINE_SRE, offline: true }];
  const list = (Array.isArray(state.launcher.presets) ? state.launcher.presets : [])
    .filter(item => item && typeof item.id === "string" && item.id.length > 0 && !isMcmod(item));
  // Keep the built-in identity visible even when a host snapshot omits it. Its
  // release/loader/author metadata is intentionally unknown, not guessed.
  const sre = list.find(isSre) || { ...OFFLINE_SRE, offline: true };
  return [sre, ...list.filter(item => !isSre(item))];
}
function queryText() { return ui.search.value.trim().toLocaleLowerCase(); }
function values(value) {
  return (Array.isArray(value) ? value : value ? [value] : [])
    .map(part => typeof part === "string" || typeof part === "number" ? String(part) : part?.name || "")
    .filter(Boolean);
}
function mcVersions(item) { return values(item?.minecraftVersions?.length ? item.minecraftVersions : item?.minecraft); }
function loaders(item) { return values(item?.loaders?.length ? item.loaders : item?.loader); }
function authors(item) { return values(item?.authors?.length ? item.authors : item?.author); }
function matches(item, query) {
  return !query || [item.name, item.englishName, item.id, item.description, item.version, ...mcVersions(item), ...loaders(item), ...authors(item), ...values(item.categories), item.installedVersion, item.updateVersion]
    .some(part => String(part || "").toLocaleLowerCase().includes(query));
}
function matchesFilters(item) {
  // The site already applied structured filters, even if list metadata is absent.
  if (isMcmod(item)) return matches(item, queryText());
  const category = SITE_CATEGORIES[Number(ui["filter-category"].value) - 1];
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
function isSre(item) { return item?.id === SRE_ID; }
function packArt(item, field) {
  const value = item?.[field];
  return typeof value === "string" && value ? value : isSre(item) ? SRE_COVER : "";
}
function statusLabel(item) {
  if (isMcmod(item)) return "MC百科";
  const t = text();
  if (!item || item.offline || state.link !== "live") return t.offlineState;
  if (item.updateAvailable) return t.update;
  if (item.installed) return t.installed;
  return t.notInstalled;
}
function taskOf() { return state.launcher?.task || {}; }
function busy() { return state.link === "live" && !!taskOf().busy; }
function gameRunning() { return state.link === "live" && !!taskOf().gameRunning; }
function mutatingLocked() { return state.link !== "live" || busy() || !!state.pending || gameRunning(); }
function catalogUrls(config = state.launcher?.config || {}) {
  return Array.isArray(config.catalogUrls) ? config.catalogUrls.filter(url => typeof url === "string" && url.trim()).map(url => url.trim())
    : config.catalogUrl ? [String(config.catalogUrl)] : [];
}
function catalogUrl() { return catalogUrls()[0] || ""; }
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
      node = document.createElement("button");
      node.type = "button";
      node.className = className;
      node.dataset.packId = id;
      node.classList.add("enter");
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
      const pill = document.createElement("span");
      pill.className = "pill";
      const summary = document.createElement("span");
      summary.className = "card-summary";
      if (className === "card") copy.append(title, detail, summary, pill);
      else copy.append(title, detail, pill);
      node.append(art, copy);
      node.parts = { image, title, detail, summary, pill };
      node.addEventListener("click", () => activate(node.dataset.packId));
    }
    const selected = item.id === (catalog.viewedId || selectedId());
    node.classList.toggle("selected", selected);
    node.classList.toggle("pinned", isSre(item));
    node.setAttribute("aria-pressed", String(selected));
    node.tabIndex = 0;
    node.parts.title.textContent = item.name || id;
    const version = [mcVersions(item).length ? `MC ${mcVersions(item).join(" / ")}` : "MC 版本未提供", loaders(item).join(" / "), item.version || ""].filter(Boolean).join(" · ");
    node.parts.detail.textContent = version;
    node.parts.summary.textContent = item.description || "暂无简介，点击查看详情。";
    node.parts.pill.textContent = isSre(item) ? text().test : isMcmod(item) ? "MC百科 · 查看详情" : statusLabel(item);
    node.parts.pill.className = `pill${item.updateAvailable ? " update" : item.installed ? " installed" : item.offline ? " offline" : ""}`;
    const label = `${item.name || id}，${isSre(item) ? text().test : statusLabel(item)}，查看详情`;
    node.setAttribute("aria-label", label);
    node.title = label;
    node.disabled = false;
    setImage(node.parts.image, packArt(item, "icon"));
    if (parent.children[index] !== node) parent.insertBefore(node, parent.children[index] || null);
  });
  for (const [id, node] of previous) if (!keep.has(id)) node.remove();
   const buttons = [...parent.querySelectorAll("button")];
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
   if (!pack || pack.offline || isMcmod(pack) || mutatingLocked()) return false;
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
   document.title = "MOONCL";
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
  setText("quark-auth-status", state.launcher?.config?.quarkAuthorized ? t.quarkAuthOn : t.quarkAuthOff);
  document.querySelector(".eyebrow").textContent = t.eyebrow;
  document.querySelector(".title-block h1").textContent = t.pageTitle;

  const list = packs();
  const query = queryText();
  const all = catalogItems();
  syncFilterOptions(all);
  const visible = all.filter(item => isSre(item) || matchesFilters(item));
  const selected = selectedPack(list);
  const others = list.filter(item => item.id !== selected?.id && matchesFilters(item)).slice(0, 3);
  const task = taskOf();
  const isBusy = busy();
  const running = gameRunning();
  const live = state.link === "live";
  const lost = state.link === "lost";
  const nativeOk = connected() && state.link !== "offline" && state.link !== "lost";

  ui["pack-badge"].textContent = selected && !isSre(selected) ? t.catalogBadge : t.test;
  ui["pack-badge"].classList.toggle("catalog", !!(selected && !isSre(selected)));
  setText("pack-state", selected ? statusLabel(selected) : t.offlineState);
  ui["pack-state"].className = `pill${selected?.updateAvailable ? " update" : selected?.installed ? " installed" : !live ? " offline" : ""}`;
  setText("pack-name", selected?.name || "StarRailHarpyExpress");
  setText("pack-description", selected?.description || (isSre(selected) ? OFFLINE_SRE.description : "暂无简介，点击下方卡片查看版本与作者资料。"));
  setText("meta-mc", mcVersions(selected).join(" / ") || "未提供");
  setText("meta-ver", selected?.version || "未提供");
  setText("meta-installed", selected?.installedVersion || "—");
  setImage(ui["banner-image"], packArt(selected, "background"));
  ui.banner.setAttribute("aria-busy", String(isBusy || !!state.pending));

  const plan = !selected ? "disabled" : !live ? "disabled" : selected.updateAvailable ? "update" : !selected.installed ? "download" : "games";
  const primaryLabel = plan === "update" ? t.updateAction : plan === "games" ? t.gotoGames : isBusy ? t.busy : t.download;
  ui.primary.textContent = isBusy && plan !== "games" ? t.busy : primaryLabel;
  ui.primary.disabled = !selected || selected.offline || !live || lost || ((plan === "download" || plan === "update") && (isBusy || !!state.pending || running));
  ui.primary.setAttribute("aria-label", `${ui.primary.textContent}：${selected?.name || SRE_ID}`);

  const percent = !task.indeterminate && typeof task.progress === "number" && Number.isFinite(task.progress)
    ? Math.max(0, Math.min(100, task.progress <= 1 ? task.progress * 100 : task.progress)) : null;
  ui["task-progress"].hidden = !(isBusy && percent !== null);
  if (percent !== null) ui["task-progress"].value = percent;
  ui["task-progress"].setAttribute("aria-label", t.busy);
   const phase = (isBusy && task.phase) || state.localNote || task.phase || (isBusy ? t.sent : !live ? (state.link === "connecting" ? t.phaseWait : t.phaseOffline) : running ? t.gameRunning : selected?.updateAvailable ? t.phaseUpdate : selected?.installed ? t.phaseInstalled : t.phaseReady);
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

  const missingCatalog = live && !catalogUrl();
   const notice = state.hostError ? "" : missingCatalog ? t.catalogMissing : live ? t.catalogReady : "";
   setText("notice-text", notice);
   ui.notice.hidden = !notice;
   ui["notice-config"].hidden = !missingCatalog;
   ui["notice-config"].disabled = !live;
  ui.error.hidden = !state.hostError;
  setText("error", state.hostError);
  const hint = !live ? t.hintOffline : isBusy ? t.hintLive : missingCatalog ? t.hintQuark : t.hintLive;
  setText("hint", running ? `${t.gameRunning} ${hint}` : isBusy ? `${t.locked} ${hint}` : hint);

  setText("recommend-empty", !others.length ? (!live ? t.recommendOffline : query ? t.recommendSearch : t.recommendNone) : "");
  ui["recommend-empty"].hidden = others.length > 0;
  syncButtons(ui["recommend-list"], others, "rec", openDetail);
  syncButtons(ui.cards, visible, "card", openDetail);
  setText("result-count", t.count(visible.length));
  const noPacks = live && list.length === 0;
  const matching = visible.filter(item => !isSre(item));
  const filtersActive = !!(query || ui["filter-mc"].value || ui["filter-loader"].value || ui["filter-category"].value);
  ui.empty.hidden = matching.length > 0 || !filtersActive;
  setText("empty-text", "当前页没有其他匹配的整合包。可重置筛选或浏览下一页。");
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
}

function applyBoot(data) {
  state.language = String(data.language || "zh").toLowerCase().startsWith("zh") ? "zh" : "en";
  state.version = String(data.version || "");
  state.branch = String(data.branch || "");
  if (data.runtimeVersion) state.runtimeVersion = String(data.runtimeVersion);
  if (typeof data.bundledRuntime === "boolean") state.bundledRuntime = data.bundledRuntime;
  if (state.link === "offline") state.link = "connecting";
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
  ensureMcmod();
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
    else if (["mcmodList", "mcmodDetail", "mcmodError"].includes(data.type)) receiveMcmod(data);
  } catch { showHostError(text().badSnapshot); }
}
function moveFocus(parent, event, columns) {
  const buttons = [...parent.querySelectorAll("button")];
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
function isMcmod(item) { return item?.source === "mcmod" || /^mcmod-\d+$/.test(item?.id || ""); }
function wikiId(item) {
  const id = Number(item?.wikiId || String(item?.id || "").replace(/^mcmod-/, ""));
  return Number.isSafeInteger(id) && id > 0 ? id : 0;
}
function normalizeMcmod(item) {
  const id = wikiId(item);
  return item && typeof item === "object" && id ? { ...item, id: `mcmod-${id}`, wikiId: id, source: "mcmod" } : null;
}
function siteFilters() {
  const minecraft = ui["filter-mc"].value;
  const loader = ui["filter-loader"].value;
  const category = Number(ui["filter-category"].value);
  return { ...(minecraft ? { minecraft } : {}), ...(loader ? { loader } : {}), ...(category ? { category } : {}) };
}
function filterKey() { return JSON.stringify(siteFilters()); }
function pageKey(page = catalog.page, filters = filterKey()) { return JSON.stringify([filters, page]); }
function catalogItems() {
  const page = catalog.pages.get(pageKey());
  const remote = catalog.include ? (page?.items || catalog.retained.get(filterKey())?.items || []) : [];
  return [...packs(), ...remote.map(item => ({ ...item, ...catalog.details.get(wikiId(item)) }))];
}
function syncFilterOptions(items) {
  const versions = [...new Set([...COMMON_MC, ...items.flatMap(mcVersions), ...[...catalog.details.values()].flatMap(mcVersions), ui["filter-mc"].value].filter(Boolean))]
    .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
  for (const [id, options, label] of [
    ["filter-mc", versions.map(value => [value, value]), "全部版本"],
    ["filter-loader", SITE_LOADERS.map(value => [value, value]), "全部加载器"],
    ["filter-category", SITE_CATEGORIES.map((value, index) => [String(index + 1), value]), "全部分类"]
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
    node.value = selected;
  }
}
function changeSiteFilters() {
  catalog.page = 1;
  catalog.totalPages = catalog.pages.get(pageKey())?.totalPages || catalog.retained.get(filterKey())?.totalPages || 0;
  // Never present a different filter's results while this request is pending.
  render();
  ensureMcmod();
}
function ensureMcmod() {
  const key = pageKey();
  if (catalog.include && state.link === "live" && !catalog.pages.has(key) && !catalog.listPending.has(key) && !catalog.listErrors.has(key)) requestPage(catalog.page);
}
function clearRequest(map, key) {
  clearTimeout(map.get(key));
  map.delete(key);
}
function requestPage(page, force = false) {
  if (!catalog.include || state.link !== "live" || !connected() || !Number.isInteger(page) || page < 1 || page > 500) return;
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
  catalog.requests.set(requestId, { key, filters, page });
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
  const id = wikiId(item);
  if (!id || !catalog.include || state.link !== "live" || !connected() || catalog.detailPending.has(id) || (!force && catalog.details.has(id))) return;
  catalog.detailErrors.delete(id);
  catalog.detailPending.set(id, setTimeout(() => {
    catalog.detailPending.delete(id);
    catalog.detailErrors.set(id, "详情请求超时，请重试。");
    renderDetail();
  }, REQUEST_TIMEOUT));
  renderDetail();
  if (!post({ type: "mcmod", operation: "detail", id })) {
    clearRequest(catalog.detailPending, id);
    catalog.detailErrors.set(id, "连接已断开，请连接启动器后重试。");
    renderDetail();
  }
}
function receiveMcmod(data) {
  if (data.type === "mcmodList") {
    const request = catalog.requests.get(data.requestId);
    if (!request || catalog.latest.get(request.key) !== data.requestId || Number(data.page) !== request.page) return;
    const { key, filters, page } = request;
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
    clearRequest(catalog.detailPending, item.wikiId);
    catalog.detailErrors.delete(item.wikiId);
    catalog.details.set(item.wikiId, item);
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
      clearRequest(catalog.detailPending, id);
      catalog.detailErrors.set(id, message);
    } else if (data.operation === "download") {
      const id = Number(data.id ?? (catalog.downloads.size === 1 ? catalog.downloads.keys().next().value : 0));
      if (!Number.isSafeInteger(id) || id < 1) return;
      catalog.downloads.set(id, { error: true, message });
    }
    render();
  }
}
function renderCatalogStatus() {
  const live = state.link === "live";
  const key = pageKey();
  const pending = catalog.listPending.has(key);
  const error = catalog.listErrors.get(key);
  const page = catalog.pages.get(key);
  const previous = catalog.retained.get(filterKey());
  const retained = previous && (error || !page) ? ` 仍显示相同筛选的第 ${previous.page} 页结果。` : "";
  ui["mcmod-status"].hidden = !catalog.include;
  ui.pagination.hidden = !catalog.include;
  ui["mcmod-status"].dataset.state = !live ? "offline" : pending ? "loading" : error ? "error" : "ready";
  ui["mcmod-status"].setAttribute("aria-busy", String(live && pending));
  setText("mcmod-message", !live ? "连接启动器后加载 MC百科。" : pending ? `正在加载 MC百科第 ${catalog.page} 页…${retained}` : error ? `${error}${retained}` : page ? (page.items.length ? `MC百科 · 当前页 ${page.items.length} 个整合包` : "MC百科 · 当前页暂无整合包") : "等待加载 MC百科…");
  ui["mcmod-retry"].hidden = !error || pending;
  ui["mcmod-retry"].disabled = !live;
  setText("page-label", `MC百科 · 第 ${catalog.page} 页${catalog.totalPages ? ` / ${catalog.totalPages}` : ""}`);
  ui["previous-page"].disabled = !live || catalog.page <= 1;
  ui["next-page"].disabled = !live || !catalog.totalPages || catalog.page >= catalog.totalPages || catalog.page >= 500;
}
function findDetailItem() {
  if (!catalog.detailId) return null;
  if (isMcmod({ id: catalog.detailId })) {
    const id = wikiId({ id: catalog.detailId });
    return { ...catalog.detailBase, ...catalog.details.get(id) };
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
  if (!isMcmod(item)) choose(id);
  render();
  if (!ui["pack-detail"].open) ui["pack-detail"].showModal();
  if (isMcmod(item)) requestDetail(item);
}
function externalUrl(value) {
  return typeof value === "string" && value && classifyUrl(value).ok ? value : "";
}
function curseForgeUrl(item) {
  const value = externalUrl(item?.curseForgeUrl);
  if (!value) return "";
  const url = new URL(value);
  return /^(?:www\.)?curseforge\.com$/i.test(url.hostname) && /^\/minecraft\/modpacks\/[^/]+\/?$/i.test(url.pathname) ? value : "";
}
function openExternal(value) {
  const url = externalUrl(value);
  if (state.link === "live" && url) post({ type: "openExternal", url });
}
function renderDetail() {
  const item = findDetailItem();
  if (!item) return;
  const remote = isMcmod(item);
  const id = wikiId(item);
  const loaded = remote && catalog.details.has(id);
  const pending = remote && catalog.detailPending.has(id);
  const error = remote && catalog.detailErrors.get(id);
  const live = state.link === "live";
  setText("detail-source", remote ? "MC百科 · 整合包资料" : isSre(item) ? text().test : "启动器预设 · 整合包资料");
  setText("detail-name", item.name || item.id);
  setText("detail-english", item.englishName || "");
  setText("detail-description", item.description || "暂无简介。");
  setImage(ui["detail-icon"], packArt(item, "icon"));
  setText("detail-mc", mcVersions(item).join(" / ") || "未提供");
  setText("detail-version", item.version || "未提供");
  setText("detail-loader", loaders(item).join(" / ") || "未提供");
  setText("detail-authors", authors(item).join("、") || "未提供");
  setText("detail-categories", values(item.categories).join(" · ") || "未提供");
  ui["detail-status"].hidden = !remote || (!!loaded && !pending && !error);
  ui["detail-status"].dataset.state = pending ? "loading" : error ? "error" : "offline";
  ui["detail-status"].setAttribute("aria-busy", String(!!pending));
  setText("detail-message", pending ? "正在获取完整资料与下载来源…" : error || (!live ? "连接启动器后可获取完整详情。" : "完整资料尚未加载。"));
  ui["detail-retry"].hidden = !remote || pending || (!error && !!loaded);
  ui["detail-retry"].disabled = !live || !catalog.include;
  const cf = loaded && curseForgeUrl(item);
  ui["detail-download"].hidden = !cf;
  ui["detail-download"].disabled = mutatingLocked();
  ui["detail-native"].hidden = remote;
  ui["detail-native"].disabled = !live || busy() || !!item.offline;
  const website = externalUrl(item.website);
  ui["detail-website"].hidden = !website;
  ui["detail-website"].disabled = !live;
  ui["detail-website"].textContent = remote ? "在 MC百科查看 ↗" : "访问项目网站 ↗";
  ui["detail-import"].disabled = mutatingLocked();
  setText("detail-guidance", !remote ? "下载、夸克安装与更新在启动器预设卡片中操作；也可导入已有归档。" : !loaded ? "完整详情加载后显示实际下载来源；也可先在 MC百科阅读介绍。" : cf ? "在启动器的 CurseForge 文件列表中选择实际版本下载。" : "暂无 CurseForge 下载入口。请前往作者提供的网站手动下载，再将 ZIP / MRPACK 归档导入启动器。");
  const links = loaded && Array.isArray(item.links) ? item.links.filter(link => externalUrl(link?.url)) : [];
  const linkKey = JSON.stringify([links, live]);
  if (ui["detail-links"].dataset.key !== linkKey) {
    ui["detail-links"].dataset.key = linkKey;
    ui["detail-links"].replaceChildren(...links.map(link => {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = `${link.name || "作者提供的地址"} ↗`;
      button.title = link.url;
      button.disabled = !live;
      button.addEventListener("click", () => openExternal(link.url));
      return button;
    }));
  }
  const download = remote && catalog.downloads.get(id);
  setText("download-status", download?.message || "");
  ui["download-retry"].hidden = !download?.error || !cf;
  ui["download-retry"].disabled = mutatingLocked();
}
function downloadMcmod() {
  const item = findDetailItem();
  const id = wikiId(item);
  if (!item || !isMcmod(item) || !catalog.details.has(id) || !curseForgeUrl(item) || mutatingLocked()) return;
  if (post({ type: "mcmod", operation: "download", id })) {
    // The contract has no success acknowledgement. Report only the hand-off,
    // never a completed download or invented launcher progress.
    catalog.downloads.set(id, { error: false, message: "已请求启动器打开版本列表，请在原生界面选择文件。" });
    renderDetail();
  } else {
    catalog.downloads.set(id, { error: true, message: "无法连接启动器，请重新连接后重试。" });
    renderDetail();
  }
}

ui.filters.addEventListener("submit", event => event.preventDefault());
for (const id of ["filter-mc", "filter-loader", "filter-category"]) ui[id].addEventListener("change", changeSiteFilters);
ui["reset-filters"].addEventListener("click", () => {
  ui.search.value = "";
  for (const id of ["filter-mc", "filter-loader", "filter-category"]) ui[id].value = "";
  changeSiteFilters();
});
ui["include-mcmod"].addEventListener("change", () => {
  catalog.include = ui["include-mcmod"].checked;
  if (!catalog.include && isMcmod({ id: catalog.viewedId })) catalog.viewedId = "";
  render();
  ensureMcmod();
});
ui["previous-page"].addEventListener("click", () => { if (!ui["previous-page"].disabled) requestPage(catalog.page - 1); });
ui["next-page"].addEventListener("click", () => { if (!ui["next-page"].disabled) requestPage(catalog.page + 1); });
ui["mcmod-retry"].addEventListener("click", () => requestPage(catalog.page, true));
ui["close-detail"].addEventListener("click", () => ui["pack-detail"].close());
ui["pack-detail"].addEventListener("cancel", event => {
  if (state.composing || event.isComposing || event.keyCode === 229) event.preventDefault();
});
ui["pack-detail"].addEventListener("close", () => {
  catalog.detailId = "";
  if (catalog.returnFocus?.isConnected) catalog.returnFocus.focus();
  else ui.search.focus();
});
ui["detail-retry"].addEventListener("click", () => requestDetail(findDetailItem(), true));
ui["detail-website"].addEventListener("click", () => openExternal(findDetailItem()?.website));
ui["detail-download"].addEventListener("click", downloadMcmod);
ui["download-retry"].addEventListener("click", downloadMcmod);
ui["detail-import"].addEventListener("click", () => { if (!mutatingLocked()) postLauncher("import"); });
ui["detail-native"].addEventListener("click", () => {
  const item = findDetailItem();
  if (!item || isMcmod(item) || ui["detail-native"].disabled) return;
  choose(item.id);
  ui["pack-detail"].close();
  ui.primary.focus();
});

document.addEventListener("compositionstart", () => { state.composing = true; });
document.addEventListener("compositionend", () => { state.composing = false; });
ui["search-form"].addEventListener("submit", event => event.preventDefault());
ui.search.addEventListener("input", () => render());
ui.search.addEventListener("keydown", event => {
  if (event.key === "Escape" && !state.composing && !event.isComposing && event.keyCode !== 229 && ui.search.value) {
    event.preventDefault();
    ui.search.value = "";
    render();
  }
});
ui.refresh.addEventListener("click", () => {
  if (ui.refresh.disabled) return;
   markPending("refresh");
   render();
   postLauncher("refresh");
   if (catalog.include) requestPage(catalog.page, true);
 });
 ui["open-services"].addEventListener("click", openServices);
 ui["notice-config"].addEventListener("click", openServices);
ui["empty-config"].addEventListener("click", openServices);
ui["clear-search"].addEventListener("click", () => { ui.search.value = ""; ui.search.focus(); render(); });
ui.primary.addEventListener("click", () => {
  if (ui.primary.disabled) return;
  const pack = selectedPack();
  if (!pack || pack.offline) return;
  if (pack.updateAvailable || !pack.installed) enqueue("primary");
  else postNative("games");
});
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
 ui["import-pack"].addEventListener("click", () => { closeMenu(); if (!ui["import-pack"].disabled) postLauncher("import"); });
ui["launch-installed"].addEventListener("click", () => { closeMenu(); enqueue("primary"); });
for (const id of ["close-services", "dismiss-services"]) ui[id].addEventListener("click", () => ui.services.close());
ui.services.addEventListener("close", () => ui["open-services"].focus());
for (const id of ["catalog-url", "quark-url"]) ui[id].addEventListener("input", () => { ui[id].setCustomValidity(""); updateUrlWarning(); });
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
render();
if (connected()) {
  window.chrome.webview.addEventListener("message", event => receive(event.data));
  post({ type: "bootstrap" });
}
