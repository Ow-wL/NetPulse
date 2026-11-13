// 측정 요약 JSON에서 합격선 판정표를 만든다. 보고서 수치를 손으로 옮기지 않기 위해서다.
// 사용: node make-results.mjs <요약 폴더> <출력 md>
// P1(유리)과 P9(대체 동작)는 캡처를 눈으로 판정하므로 여기서는 "캡처 판정"으로만 표시한다.
import fs from 'node:fs';
import path from 'node:path';

const [dir, out] = process.argv.slice(2);
const names = { 'a-net10': 'A-net10', 'a-net48': 'A-net48', c: 'C' };
const runs = Object.keys(names).filter(k => fs.existsSync(path.join(dir, `summary-${k}.json`)))
  .map(k => ({ key: k, s: JSON.parse(fs.readFileSync(path.join(dir, `summary-${k}.json`), 'utf8').replace(/^﻿/, '')) }));

const ok = (b) => (b ? '통과' : '**불합격**');
const frame = (s, label) => s.frames.find(f => f.label === label) || { avgFps: NaN, p95Ms: NaN };
// P6 합격선: 관리자 권한 없이, 네 API가 각각 결과 또는 명시적 실패 상태를 낸다.
// 값이 'fail:<예외>' 형태여도 명시적 실패 상태이므로 통과다. 키가 없거나 비었으면 불합격.
const probeMap = (p) => Object.fromEntries(p.split(',').map(kv => { const i = kv.indexOf('='); return [kv.slice(0, i), kv.slice(i + 1)]; }));
const probesOk = (p) => { const m = probeMap(p); return m.elevated === 'no' && ['icmp', 'eventlog', 'bestinterface', 'interfaces'].every(k => typeof m[k] === 'string' && m[k].length > 0); };
const probesSucceeded = (p) => /icmp=Success/.test(p) && /eventlog=read:\d+/.test(p) && /bestinterface=ok/.test(p) && /interfaces=count:\d+/.test(p);

const rows = [];
const add = (label, criterion, f) => rows.push([label, criterion, ...runs.map(r => f(r.s))]);
const verdict = {};
for (const r of runs) {
  const s = r.s, sc = frame(s, 'scene'), res = s.resources, p4 = s.p4, p5 = s.p5;
  verdict[r.key] = {
    P2: sc.avgFps >= 55 && sc.p95Ms <= 25,
    P3: res.cpuPercent <= 15 && res.memoryMBMax <= 400,
    P4: p4.elapsedSeconds >= 60 && p4.firstIndex === 0 && p4.duplicates === 0 && p4.missing === 0 && p4.maxLateMs <= 100,
    P5: p5.missedWhileHidden === 0 && p5.displayMsSinceShow <= 5000 && p5.displayedIndex >= p5.latestAtShow && p5.externalReadValue >= p5.latestAtShow,
    P6: probesOk(s.probes),
    P7: s.publishMB <= 300 && s.startupMs_run2 <= 3000 && s.readOnlyStart === true && s.readOnlyFolderWritable === false,
    P8: typeof s.roundTextBefore === 'string' && s.koreanInput === '화상회의 중 끊김',
  };
}
add('P1 창 안쪽 유리', '패널 뒤가 흐려 보임', () => '캡처 판정');
add('P2 프레임 (정지 장면)', '평균 55 fps↑, 95백분위 25 ms↓', s => `${frame(s, 'scene').avgFps} fps / ${frame(s, 'scene').p95Ms} ms`);
add('　테마 반복 전환 중 (6초)', '', s => `${frame(s, 'theme-toggle').avgFps} / ${frame(s, 'theme-toggle').p95Ms}`);
add('　화면 반복 전환 중 (6초)', '', s => `${frame(s, 'page-switch').avgFps} / ${frame(s, 'page-switch').p95Ms}`);
add('P3 CPU (10초, 전체 코어 기준)', '15%↓', s => `${s.resources.cpuPercent}%`);
add('　메모리 작업 집합 합계 (평균 / 최대)', '최대 400 MB↓', s => `${s.resources.memoryMBAvg} / ${s.resources.memoryMBMax} MB`);
add('　(보조) 전용 작업 집합 (평균 / 최대)', '판정에 안 씀', s => `${s.resources.privateMBAvg} / ${s.resources.privateMBMax} MB`);
add('　관측된 프로세스 수', '', s => `${s.resources.processesSeen}`);
add('P4 측정 영향', '실제 시각 60초 이상, 0번부터 중복·누락 0, 지연 최대 100 ms↓', s => `${s.p4.elapsedSeconds}초, ${s.p4.firstIndex}~${s.p4.lastIndex}번 ${s.p4.rounds}회차, 중복 ${s.p4.duplicates}, 누락 ${s.p4.missing}, 최대 ${s.p4.maxLateMs} ms`);
add('P5 30초 숨김 후 (앱 안에서 잼)', '누락 0, 다시 보인 뒤 5초 안에 최신 회차', s => `누락 ${s.p5.missedWhileHidden}, ${s.p5.displayMsSinceShow} ms 만에 ${s.p5.displayedIndex}번 표시 (복귀 시 최신 ${s.p5.latestAtShow})`);
add('　(보조) 바깥에서 읽은 값', '최신 회차 이상', s => `${s.p5.externalReadValue} (다시 보인 뒤 ${s.p5.externalReadMsSinceShow} ms, 도구 지연 포함)`);
add('P6 비관리자 진단 API', '관리자 아님, 네 API 모두 결과 또는 명시적 실패', s => probesOk(s.probes) ? (probesSucceeded(s.probes) ? '4/4 성공' : '명시적 실패 포함') : s.probes);
add('P7 배포 크기 (파일 수)', '300 MB↓', s => `${s.publishMB} MB (${s.publishFiles})`);
add('　첫 실행 / 두 번째 실행 시작', '두 번째 3초↓', s => `${(s.startupMs_run1 / 1000).toFixed(2)} / ${(s.startupMs_run2 / 1000).toFixed(2)}초`);
add('　쓰기 금지 폴더에서 시작', '가능', s => s.readOnlyStart && !s.readOnlyFolderWritable ? '가능' : '불가');
add('P8 자동화', '스크립트 하나로 끝까지', s => verdict[runs.find(r => r.s === s).key].P8 ? '통과' : '실패');
add('P9 대체 동작', '설정 읽기 + 대체 화면', () => '캡처 판정');
add('**자동 판정 (P2~P8)**', '', s => {
  const v = verdict[runs.find(r => r.s === s).key];
  const failed = Object.entries(v).filter(([, b]) => !b).map(([k]) => k);
  return failed.length ? `**불합격 (${failed.join(', ')})**` : '통과';
});

const head = ['항목', '합격선', ...runs.map(r => names[r.key])];
let md = `<!-- spikes/tools/make-results.mjs 가 요약 JSON에서 만든 파일. 손으로 고치지 않는다. -->\n\n`;
md += `| ${head.join(' | ')} |\n| ${head.map(() => '---').join(' | ')} |\n`;
for (const r of rows) md += `| ${r.join(' | ')} |\n`;
md += `\n**측정 근거**\n\n| 후보 | 측정 시각 | 소스 커밋 (작업트리) | SDK | 빌드 명령 | 실행 파일 SHA-256 |\n| --- | --- | --- | --- | --- | --- |\n`;
for (const r of runs) md += `| ${names[r.key]} | ${r.s.measuredAt} | \`${r.s.gitHead}\` (${r.s.worktreeClean ? '깨끗함' : '**커밋 안 된 변경 있음**'}) | ${r.s.sdk} | \`${r.s.buildCommand}\` | \`${r.s.exeSha256.slice(0, 16)}…\` |\n`;
md += `\nWebView2 런타임: ${runs.filter(r => r.s.webview2).map(r => `${names[r.key]} ${r.s.webview2}`).join(', ') || '해당 없음'}\n`;
fs.writeFileSync(out, md, 'utf8');
console.log(md);
