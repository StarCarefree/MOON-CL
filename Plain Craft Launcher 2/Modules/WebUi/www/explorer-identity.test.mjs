import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {projectLink,merge}=createRequire(import.meta.url)('./explorer-identity.js');
const cf={id:'curseforge-123',source:'curseforge',projectId:'123',slug:'real-pack',resourceType:'modpack',name:'Name does not matter'};
const mr={id:'modrinth-abcd1234',source:'modrinth',projectId:'abcd1234',slug:'another-pack',resourceType:'modpack'};
const mc={id:'mcmod-1426',source:'mcmod',resourceType:'modpack',links:[{url:'https://www.curseforge.com/minecraft/modpacks/real-pack/files/1?from=wiki#top'},{url:'https://modrinth.com/modpack/another-pack/versions'}]};
const mcl={id:'curated',source:'mcl'};
assert.deepEqual(merge([mcl],[mc],[cf,mr]),[mcl,mc]);
assert.deepEqual(merge([mcl],[],[cf,mr]),[mcl,cf,mr]);
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
console.log('PASS exact encyclopedia precedence, provider/type isolation, legacy URLs, unsafe links and MCL-first ordering');
