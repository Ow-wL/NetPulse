// 스파이크 A 조작 도구. 실행 중인 앱의 WebView2에 CDP로 붙어 한 가지 동작을 한다.
// 사용: node a-drive.mjs <port> <action> [arg]
//   wait-ready | click <selector> | read <selector> | screenshot <file> | eval-frames
import { chromium } from 'playwright-core';

const [port, action, arg] = process.argv.slice(2);
const deadline = Date.now() + 20000;
let browser;
while (!browser) {
  try { browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`); }
  catch (e) { if (Date.now() > deadline) throw e; await new Promise(r => setTimeout(r, 300)); }
}
const page = browser.contexts()[0].pages().find(p => p.url().startsWith('https://app.netpulse.invalid/'));
if (!page) { console.error('앱 화면을 찾지 못했습니다.'); process.exit(2); }

switch (action) {
  case 'wait-ready':
    await page.waitForSelector('#ready[data-ok="1"]', { state: 'attached', timeout: 15000 });
    console.log('ready');
    break;
  case 'click': {
    const el = page.locator(arg);
    const box = await el.boundingBox();
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(700);
    console.log('clicked', arg);
    break;
  }
  case 'read':
    console.log((await page.locator(arg).textContent()).trim());
    break;
  case 'screenshot':
    await page.screenshot({ path: arg });
    console.log('saved', arg);
    break;
  case 'type': {
    const [selector, text] = arg.split('::');
    await page.locator(selector).fill(text);
    console.log((await page.locator(selector).inputValue()));
    break;
  }
  default:
    console.error('알 수 없는 동작', action); process.exit(2);
}
await browser.close().catch(() => {});
