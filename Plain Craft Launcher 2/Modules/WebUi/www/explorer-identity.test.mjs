import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {projectLink,merge}=createRequire(import.meta.url)('./explorer-identity.js');
const cf={id:'curseforge-123',source:'curseforge',projectId:'123',slug:'real-pack',resourceType:'modpack',name:'Name does not matter'};
const mr={id:'modrinth-abcd1234',source:'modrinth',projectId:'abcd1234',slug:'another-pack',resourceType:'modpack'};
const mc={id:'mcmod-1426',source:'mcmod',resourceType:'modpack',links:[{url:'https://www.curseforge.com/minecraft/modpacks/real-pack/files/1?from=wiki#top'},{url:'https://modrinth.com/modpack/another-pack/versions'}]};
const mcl={id:'curated',source:'mcl'};
assert.deepEqual(merge([mcl],[mc],[cf,mr]),[mc,mcl]);
assert.deepEqual(merge([mcl],[],[cf,mr]),[cf,mcl,mr]);
assert.equal(merge([],[{...mc,links:[]}],[cf,mr]).length,3);
assert.equal(merge([],[{...mc,name:cf.name,links:[]}],[cf]).length,2); // Never fuzzy-match names.
assert.equal(merge([],[{...mc,resourceType:'datapack'}],[cf]).length,2); // Never cross resource types.
for(const url of ['https://curseforge.com.evil.test/minecraft/modpacks/real-pack','https://evilcurseforge.com/minecraft/modpacks/real-pack','https://user:pass@curseforge.com/minecraft/modpacks/real-pack','javascript:alert(1)','file:///C:/real-pack']) assert.equal(projectLink(url),null);
assert.equal(projectLink('https://modrinth.com/mod/real-data?x=1','datapack'),'datapack:modrinth:slug:real-data');
assert.equal(projectLink('https://curseforge.com/projects/123'),'modpack:curseforge:id:123');
assert.equal(projectLink('https://www.modrinth.com/project/abcd1234'),'modpack:modrinth:id:abcd1234');
assert.equal(projectLink('https://curseforge.com/minecraft/texture-packs/real-pack','resourcepack'),'resourcepack:curseforge:slug:real-pack');
assert.equal(projectLink('https://curseforge.com/minecraft/modpacks/REAL-PACK/'),'modpack:curseforge:slug:real-pack');
assert.equal(merge([],[{...mc,links:[{url:'https://curseforge.com/projects/123'}]}],[cf]).length,1);
const l1={id:'l1',source:'mcl'},l2={id:'l2',source:'mcl'};
const m1={id:'m1',source:'mcmod'},m2={id:'m2',source:'mcmod'},m3={id:'m3',source:'mcmod'};
const c1={id:'c1',source:'curseforge'},r1={id:'r1',source:'modrinth'},c2={id:'c2',source:'curseforge'};
assert.deepEqual(merge([l1,l2],[m1,m2,m3],[c1,r1,c2]),[m1,c1,l1,r1,m2,c2,l2,m3]);
assert.deepEqual(merge([l1,l2],[],[]),[l1,l2]);
console.log('PASS exact encyclopedia precedence, provider/type isolation, legacy URLs, unsafe links and round-robin source ordering');
