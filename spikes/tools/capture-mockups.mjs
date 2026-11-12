// 디자인 시안 캡처. 시안 × 테마, 끊김 상태, 효과 줄이기, 테마 전환 연속 프레임.
// 사용: node capture-mockups.mjs <출력 폴더>
import { chromium } from 'playwright-core';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

const out = process.argv[2];
fs.mkdirSync(out, { recursive: true });
const file = pathToFileURL(path.resolve(import.meta.dirname, '../design/home-mockups.html')).href;
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });

async function shot(query, name, wait = 2600) {
  await page.goto(`${file}?chrome=0&${query}`);
  await page.waitForTimeout(wait);
  await page.screenshot({ path: path.join(out, name) });
  console.log('saved', name);
}

for (const v of ['aurora', 'obsidian', 'prism']) {
  for (const t of ['dark', 'light']) await shot(`variant=${v}&theme=${t}`, `${v}-${t}.png`);
  await shot(`variant=${v}&theme=dark&state=problem`, `${v}-dark-problem.png`);
}
await shot('variant=aurora&theme=dark&transparency=reduce&motion=reduce', 'aurora-dark-reduced.png', 800);

// 테마 전환 연속 프레임: 비교 막대를 띄워 라이트 버튼을 누른다
await page.goto(`${file}?variant=aurora&theme=dark`);
await page.waitForTimeout(2600);
const button = page.locator('[data-t="light"]');
const box = await button.boundingBox();
await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
for (const ms of [90, 180, 300, 600]) {
  await page.waitForTimeout(ms === 90 ? 90 : ms - [90, 180, 300, 600][[90, 180, 300, 600].indexOf(ms) - 1]);
  await page.screenshot({ path: path.join(out, `transition-${String(ms).padStart(3, '0')}ms.png`) });
}
console.log('saved transition frames');
await browser.close();
