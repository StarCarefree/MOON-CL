"use strict";
/* Browse bridge. Host owns the native shell. This page never draws it and never
   posts projection/navigate/window/action messages.
   Uplink: {type:'launcher', operation, ...params} and {type:'native', page}.
   Real HandleAsync ops used here: select(id), refresh, primary, installQuark,
   cancel, configure(catalogUrls, catalogUrl, quarkServiceUrl), checkUpdates, import.
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
  description: "SRE 哈比列车 · 内置测试整合包。静态预览只展示这一条，不代表已安装，也不能在未连接时下载。",
  version: "V4.3.0",
  minecraft: "1.21.1",
  installed: false,
  updateAvailable: false
};
const COPY = {
  zh: {
    connection: { offline: "静态预览 · 未连接本地启动器", connecting: "正在连接启动器", live: "已连接本地启动器", lost: "连接失效" },
    search: "搜索", placeholder: "名称、版本或描述", refresh: "刷新资源", services: "服务配置",
    recommend: "推荐", recommendNote: "不是排行，只列出目录里的其他条目", catalog: "全部条目",
    test: "测试整合包", catalogBadge: "目录整合包", offlineState: "离线 · 安装状态未知",
    notInstalled: "未安装", installed: "已安装", update: "可更新", missing: "无下载来源信息",
    download: "下载整合包", updateAction: "更新整合包", gotoGames: "返回我的游戏", busy: "任务进行中",
    cancel: "取消", more: "更多", versions: "查看版本",
    check: "检查更新", quark: "通过夸克下载", importPack: "导入整合包", launch: "启动已安装的整合包",
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
    catalogMissing: "整合包目录地址未配置。除内置测试整合包外不会出现其他条目。请先配置目录，再刷新资源。下载测试包会调用本地启动器的真实安装，本页不伪造进度；若本机没有可用归档或解析失败，错误会显示在这里。",
    catalogReady: "条目来自已配置目录和本地安装状态。不展示人气、活动或下载量。",
    emptyHost: "启动器没有返回整合包条目。",
    emptySearch: "没有匹配的整合包。",
    recommendOffline: "离线预览只有这一条测试整合包，不会编造推荐游戏。",
    recommendNone: "目录里没有其他条目。配置目录地址并刷新后，它们会出现在这里。",
    recommendSearch: "没有符合搜索的推荐条目。",
    hintOffline: "这是离线静态预览，不是已连接的宿主。业务按钮已禁用，没有使用假目录或假安装状态。",
    hintLive: "游戏目录、开始游戏和版本列表在原生界面。此页只浏览整合包，并把下载、更新、夸克与导入交给本地启动器。",
    hintQuark: "通过夸克下载使用当前选中条目，由启动器解析并执行。失败会返回真实错误，不会在页面里假下载。",
    launchTitle: "仅在已安装且没有待更新时调用启动器 primary。有更新时请使用主按钮更新。",
    urlInvalid: "请输入不含账号密码的 HTTP 或 HTTPS 地址。",
    urlRemote: "该地址使用未加密 HTTP，且不是本机地址。启动器会照常保存，请确认信任来源。",
    urlLocal: "本机 HTTP 地址可以保存。",
    urlHttps: "HTTPS 地址可以保存。",
    urlEmpty: "留空会清除已保存的地址。夸克留空则使用官方 API。",
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
    eyebrow: "补充浏览"
  },
  en: {
    connection: { offline: "Static preview · launcher not connected", connecting: "Connecting to launcher", live: "Launcher connected", lost: "Connection lost" },
    search: "Search", placeholder: "Name, version, or description", refresh: "Refresh", services: "Services",
    recommend: "Picks", recommendNote: "Other catalog entries, not a ranking", catalog: "All entries",
    test: "Test modpack", catalogBadge: "Catalog pack", offlineState: "Offline · install state unknown",
    notInstalled: "Not installed", installed: "Installed", update: "Update available", missing: "No source metadata",
    download: "Download pack", updateAction: "Update pack", gotoGames: "Back to my games", busy: "Task in progress",
    cancel: "Cancel", more: "More", versions: "Versions",
    check: "Check updates", quark: "Download via Quark", importPack: "Import pack", launch: "Launch installed pack",
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
    catalogMissing: "Catalog URL is not configured. Only the built-in test pack is available. Configure the catalog, then refresh. Download calls the real launcher install and does not fake progress.",
    catalogReady: "Entries come from the configured catalog and local install state. No popularity or download counts.",
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
    eyebrow: "Browse add-on"
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
  const list = Array.isArray(state.launcher.presets) ? state.launcher.presets : [];
  return list.filter(item => item && typeof item.id === "string" && item.id.length > 0);
}
function queryText() { return ui.search.value.trim().toLocaleLowerCase(); }
function matches(item, query) {
  if (!query) return true;
  return [item.name, item.id, item.description, item.version, item.minecraft, item.installedVersion, item.updateVersion]
    .some(part => String(part || "").toLocaleLowerCase().includes(query));
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
      copy.append(title, detail, pill);
      node.append(art, copy);
      node.parts = { image, title, detail, pill };
      node.addEventListener("click", () => activate(node.dataset.packId));
    }
    const selected = item.id === selectedId();
    node.classList.toggle("selected", selected);
    node.setAttribute("aria-selected", String(selected));
    node.tabIndex = 0;
    node.parts.title.textContent = item.name || id;
    const version = [item.minecraft ? `MC ${item.minecraft}` : "", item.version || ""].filter(Boolean).join(" · ");
    node.parts.detail.textContent = version || item.id;
    node.parts.pill.textContent = isSre(item) ? `${text().test} · ${statusLabel(item)}` : statusLabel(item);
    node.parts.pill.className = `pill${item.updateAvailable ? " update" : item.installed ? " installed" : item.offline ? " offline" : ""}`;
    const label = `${item.name || id}，${statusLabel(item)}`;
    node.setAttribute("aria-label", label);
    node.title = label;
    node.disabled = busy();
    setImage(node.parts.image, packArt(item, "icon"));
    if (parent.children[index] !== node) parent.insertBefore(node, parent.children[index] || null);
  });
  for (const [id, node] of previous) if (!keep.has(id)) node.remove();
   const buttons = [...parent.querySelectorAll("button")];
   const activeInside = buttons.includes(document.activeElement);
   buttons.forEach((node, index) => {
     const preferred = activeInside ? node === document.activeElement : node.dataset.packId === selectedId() || (!selectedId() && index === 0);
     node.tabIndex = preferred ? 0 : -1;
   });
 }
 
 function choose(id) {
   if (state.link !== "live" || busy()) return;
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
   if (!pack || mutatingLocked()) return false;
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
  document.querySelector(".eyebrow").textContent = t.eyebrow;
  document.querySelector(".title-block h1").textContent = t.pageTitle;

  const list = packs();
  const query = queryText();
  const visible = list.filter(item => matches(item, query));
  const selected = selectedPack(list);
  const others = visible.filter(item => item.id !== selected?.id).slice(0, 4);
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
  setText("pack-description", selected?.description || OFFLINE_SRE.description);
  setText("meta-mc", selected?.minecraft || "—");
  setText("meta-ver", selected?.version || "—");
  setText("meta-installed", selected?.installedVersion || "—");
  setImage(ui["banner-image"], packArt(selected, "background"));
  ui.banner.setAttribute("aria-busy", String(isBusy || !!state.pending));

  const plan = !selected ? "disabled" : !live ? "disabled" : selected.updateAvailable ? "update" : !selected.installed ? "download" : "games";
  const primaryLabel = plan === "update" ? t.updateAction : plan === "games" ? t.gotoGames : isBusy ? t.busy : t.download;
  ui.primary.textContent = isBusy && plan !== "games" ? t.busy : primaryLabel;
  ui.primary.disabled = !selected || !live || lost || ((plan === "download" || plan === "update") && (isBusy || !!state.pending || running));
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
    ["install-quark", live && !mutatingLocked()],
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
  syncButtons(ui["recommend-list"], others, "rec", choose);
  syncButtons(ui.cards, visible, "card", choose);
   [...ui.cards.querySelectorAll("button")].forEach(node => {
     if (!node.id) node.id = `pack-${node.dataset.packId.replace(/[^a-zA-Z0-9_-]/g, "_")}`;
     node.setAttribute("role", "option");
   });
   const activeCard = selected ? [...ui.cards.querySelectorAll("button")].find(node => node.dataset.packId === selected.id) : null;
   ui.cards.setAttribute("aria-activedescendant", activeCard?.id || "");
  setText("result-count", t.count(visible.length));
  const noPacks = live && list.length === 0;
  ui.empty.hidden = visible.length > 0;
  setText("empty-text", noPacks ? t.emptyHost : t.emptySearch);
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
  buttons[Math.max(0, Math.min(buttons.length - 1, next))]?.focus();
}

ui["search-form"].addEventListener("submit", event => event.preventDefault());
ui.search.addEventListener("input", () => render());
ui.search.addEventListener("keydown", event => {
  if (event.key === "Escape" && !event.isComposing && event.keyCode !== 229 && ui.search.value) {
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
 });
 ui["open-services"].addEventListener("click", openServices);
 ui["notice-config"].addEventListener("click", openServices);
ui["empty-config"].addEventListener("click", openServices);
ui["clear-search"].addEventListener("click", () => { ui.search.value = ""; ui.search.focus(); render(); });
ui.primary.addEventListener("click", () => {
  if (ui.primary.disabled) return;
  const pack = selectedPack();
  if (!pack) return;
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
