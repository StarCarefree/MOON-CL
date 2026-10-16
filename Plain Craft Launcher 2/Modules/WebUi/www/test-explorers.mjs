// Test helpers select explicit explorer combinations; production UI is genuinely multi-select.
export async function onlyExplorer(page, source, mcl = true) {
  const wanted = new Set([...(mcl ? ['mcl'] : []), source]);
  for (const name of ['mcl','curseforge','modrinth','mcmod']) {
    const input = page.locator(name === 'mcmod' ? '#include-mcmod' : `#source-${name}`);
    if (!wanted.has(name) && await input.isChecked()) await input.uncheck();
  }
  for (const name of wanted) await page.locator(name === 'mcmod' ? '#include-mcmod' : `#source-${name}`).check();
}
