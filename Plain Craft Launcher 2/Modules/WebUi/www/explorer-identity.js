"use strict";
// Pure project identity rules. No fuzzy names, network, filesystem or HTML rendering.
(function (root) {
  const kinds = new Set(["modpack", "datapack", "world", "shader", "resourcepack"]);
  function projectLink(value, resourceType = "modpack") {
    if (typeof value !== "string" || !kinds.has(resourceType)) return null;
    let url; try { url = new URL(value); } catch { return null; }
    if (!["https:", "http:"].includes(url.protocol) || url.username || url.password) return null;
    const host = url.hostname.toLowerCase();
    const parts = url.pathname.split("/").filter(Boolean).map(p => { try { return decodeURIComponent(p); } catch { return ""; } });
    let source, token, id = false;
    if (host === "www.curseforge.com" || host === "curseforge.com") {
      source = "curseforge";
      const paths = { modpacks: "modpack", "data-packs": "datapack", datapacks: "datapack", worlds: "world", shaders: "shader", "texture-packs": "resourcepack", resourcepacks: "resourcepack" };
      if (parts[0] === "minecraft" && paths[parts[1]] === resourceType) token = parts[2];
      else if (parts[0] === "projects" && /^\d+$/.test(parts[1] || "")) { token = parts[1]; id = true; }
    } else if (host === "modrinth.com" || host === "www.modrinth.com") {
      source = "modrinth";
      if (parts[0] === resourceType || resourceType === "datapack" && parts[0] === "mod") token = parts[1];
      else if (parts[0] === "project" && /^[A-Za-z0-9]{8}$/.test(parts[1] || "")) { token = parts[1]; id = true; }
    }
    if (!source || !token || !/^[A-Za-z0-9_-]+$/.test(token)) return null;
    return `${resourceType}:${source}:${id ? "id:" + token : "slug:" + token.toLowerCase()}`;
  }
  function keys(item) {
    const type = item?.resourceType || "modpack";
    const result = new Set();
    if (!kinds.has(type)) return result;
    if (["curseforge", "modrinth"].includes(item?.source)) {
      if (item.projectId) result.add(`${type}:${item.source}:id:${item.projectId}`);
      if (item.slug) result.add(`${type}:${item.source}:slug:${String(item.slug).toLowerCase()}`);
      const website = projectLink(item.website, type); if (website) result.add(website);
    } else if (item?.source === "mcmod") {
      for (const value of [item.curseForgeUrl, item.modrinthUrl, ...(Array.isArray(item.links) ? item.links.map(link => link?.url) : [])]) {
        const key = projectLink(value, type); if (key) result.add(key);
      }
    }
    return result;
  }
  function merge(mcl, encyclopedia, platforms) {
    const covered = new Set(encyclopedia.flatMap(item => [...keys(item)]));
    const filtered = platforms.filter(item => ![...keys(item)].some(key => covered.has(key)));
    // 按来源分桶做 round-robin：mcmod → curseforge → mcl → modrinth → 其他 source 按首次出现顺序
    const buckets = [[...encyclopedia], [], [...mcl], []];
    const extra = new Map();
    for (const item of filtered) {
      const bucket = item.source === "curseforge" ? buckets[1] : item.source === "modrinth" ? buckets[3] : null;
      if (bucket) bucket.push(item);
      else {
        if (!extra.has(item.source)) extra.set(item.source, []);
        extra.get(item.source).push(item);
      }
    }
    const queues = [...buckets, ...extra.values()];
    const result = [];
    for (let i = 0; ; i++) {
      let any = false;
      for (const bucket of queues) {
        if (i < bucket.length) { result.push(bucket[i]); any = true; }
      }
      if (!any) return result;
    }
  }
  const api = Object.freeze({ projectLink, keys, merge });
  root.ExplorerIdentity = api;
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : window);
