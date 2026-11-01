// =============================================================
// 二一排课 · 批量下载全校基础课表（client-access-token 鉴权版）
// -------------------------------------------------------------
// ★ 用法：浏览器登录 https://paike.21cnjy.com（能看到课表）→ 停在排课页
//   → F12 → Console → 粘贴本文件全部内容 → 回车。
//
// ★ 关键：二一的鉴权是请求头 client-access-token（JWT），不是 cookie。
//   JWT 被存在 cookie "user" 里（值形如 "姓名@//JWT"），本脚本自动从中提取。
//   若你的浏览器里取不到，可把 JWT 手动填到下面 MANUAL_TOKEN。
//
// 产物：grade-tree-<年级>.json / class-lectures-<年级>-<classId>.json /
//      substitute-list-<年级>.json，全部放进 数据对接方案/data/ 即可。
// =============================================================
(async function () {
  const API = 'https://paike.21cnjy.com/query';
  const MANUAL_TOKEN = ''; // 自动从 cookie 读；读不到时手动填 JWT（三段 base64，以 eyJ 开头）

  function getToken() {
    try {
      const m = (document.cookie || '').match(/eyJ[\w-]+\.[\w-]+\.[\w-]+/);
      if (m) return m[0];
    } catch (e) {}
    return MANUAL_TOKEN;
  }

  const TOKEN = getToken();
  console.log('%c[环境] ' + location.origin, 'color:#666');
  console.log('%c[鉴权] ' + (TOKEN ? '已从 cookie 取到 client-access-token（' + TOKEN.slice(0, 20) + '…）' : '未取到 token！'),
              TOKEN ? 'color:#0a0' : 'color:#c00');
  if (!TOKEN) {
    console.log('%c⛔ 取不到 client-access-token：请确认在 paike.21cnjy.com 已登录；或把 JWT 填到 MANUAL_TOKEN 再运行。',
                'color:#c00;font-weight:bold');
    return;
  }

  const HEAD = {
    'client-access-token': TOKEN,
    'x-requested-with': 'XMLHttpRequest',
    'accept': '*/*',
    'referer': 'https://paike.21cnjy.com/timetable/timetableClass.html',
  };

  async function getJSON(url, label) {
    try {
      const res = await fetch(url, { credentials: 'include', headers: HEAD });
      const text = await res.text();
      let j = null;
      try { j = JSON.parse(text); } catch (e) {}
      if (!res.ok || !j || String(j.code) !== '0') {
        console.log(`%c❌ ${label} HTTP ${res.status} code=${j && j.code} ${j && j.msg ? j.msg : ''}`, 'color:#c00');
        return null;
      }
      return j;
    } catch (e) {
      console.log(`%c❌ ${label} 异常 ${e && e.message ? e.message : e}`, 'color:#c00');
      return null;
    }
  }

  function saveJSON(j, name) {
    const blob = new Blob([JSON.stringify(j, null, 2)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  const delay = (ms) => new Promise((r) => setTimeout(r, ms));

  const PROJECTS = [
    {
      grade: '八年级',
      pid: '77cc8c2dba034b04b0ccb77c542e20d1',
      fallback: [
        ['8d0937e43358cc8db0cb820ca2e44eca', '八年级(1)班'],
        ['979907a14b3a9e0d573574eea6de9807', '八年级(2)班'],
        ['ca4022e6e7387b43dc0a7d85090abcaf', '八年级(3)班'],
        ['ec17263d01f793a147c70a17df7826ba', '八年级(4)班'],
        ['b73dac227f643c6330d66b8d3b21e0fe', '八年级(5)班'],
        ['8b1e2bdc1e602ba9372ef6caf191c77b', '八年级(6)班'],
        ['43a3f076a7de5da216dfd4c7a4ce153a', '八年级(7)班'],
        ['012bc0a420685e1348f06cdfeb989b25', '八年级(8)班'],
        ['82abd8a19e958735aa80cf7615d061d2', '八年级(9)班'],
        ['7ce04919ee3e7658c6027e73237c7c4a', '八年级(10)班'],
        ['fc6e2f824ee1eced138712cfb5c4a028', '八年级(11)班'],
        ['83bef6f14561b04fbc147bb7ff60df33', '八年级(12)班'],
        ['29b7b95fcf66af1b0293d25bec4ab5d7', '八年级(13)班'],
        ['772a0d03932494183aceddb43962d156', '八年级(14)班'],
        ['db7d7aa41ec89eb2f157d68ba61e0b97', '八年级(15)班'],
        ['6594e6be2a65a85aab77b9ad2603a0bf', '八年级(16)班'],
        ['3b53432d0c6954deec4575643df0d3ad', '八年级(17)班'],
        ['67a0c0fe06a3b5aa22346b9ba1f3b80a', '八年级(18)班'],
        ['1d300ad3c618d83437868aa8fd9922c8', '八年级(19)班'],
        ['88bbb79bff3e6c239d964f3ce89065dc', '八年级(20)班'],
        ['ab7da4f8ae3aeccdf22bffc7593208a3', '八年级(21)班'],
        ['f7539e8cb6d7922702a5dec64bb94bf4', '八年级(22)班'],
        ['f834c7cd1a14e91942073f873ba501bd', '八年级(23)班'],
        ['414a7e3262d8b8472165827f86867717', '八年级(24)班'],
        ['683ccf688a5261843a1b40ccd8ee50c1', '八年级(25)班'],
        ['7131aa42f0d0928dfecce7f7696cca22', '八年级(26)班'],
        ['c24d9f435579aff421b42a28bcb52653', '八年级(27)班'],
        ['0c984d4b6f70cabcc393e6ccf618d345', '八年级(28)班'],
        ['7913d422ace401deb07de038b910dc8b', '八年级(29)班'],
        ['7f3e189d1c96f88c5cf61c04497333b9', '八年级(30)班']
      ]
    },
    { grade: '七年级', pid: '818007cc3510c7f916204a5043010955', fallback: [] },
    { grade: '九年级', pid: '9a2c21ed598a5abeb8ed7c3ad3c56d98', fallback: [] }
  ];

  let totalOk = 0, totalFail = 0;

  for (const proj of PROJECTS) {
    const { grade, pid } = proj;
    console.log(`%c\n====== 📘 ${grade}（projectId=${pid}）======`, 'color:#06c;font-weight:bold');

    let classes = [];
    const gt = await getJSON(`${API}/grade-tree?projectId=${pid}`, `${grade} grade-tree`);
    if (gt) {
      (gt.data || []).forEach(g => (g.list || []).forEach(c => classes.push([c.id, c.name])));
      saveJSON(gt, `grade-tree-${grade}.json`);
      console.log(`%c✅ grade-tree 解析 ${classes.length} 个班`, 'color:#06c');
    } else {
      classes = (proj.fallback || []).slice();
      console.log(`%c⚠️ grade-tree 失败，用内置兜底 ${classes.length} 个班`, 'color:#c60');
    }

    let ok = 0, fail = 0;
    for (let i = 0; i < classes.length; i++) {
      const [cid, cname] = classes[i];
      const lec = await getJSON(`${API}/class-lectures?projectId=${pid}&classId=${cid}`, cname);
      if (lec) {
        saveJSON(lec, `class-lectures-${grade}-${cid}.json`);
        ok++;
        console.log(`[${i + 1}/${classes.length}] ✅ ${cname}`);
      } else {
        fail++;
        console.log(`[${i + 1}/${classes.length}] ❌ ${cname}`);
      }
      await delay(300);
    }
    console.log(`%c  class-lectures：成功 ${ok} / 失败 ${fail}`, ok === classes.length ? 'color:#0a0' : 'color:#c60');
    totalOk += ok; totalFail += fail;

    const subs = await getJSON(`${API}/substitute-list?projectId=${pid}`, `${grade} substitute-list`);
    if (subs) {
      saveJSON(subs, `substitute-list-${grade}.json`);
      console.log(`%c✅ substitute-list 已存（${(subs.data || []).length} 条）`, 'color:#06c');
    }
  }

  console.log(`%c\n🏁 完成：class-lectures 成功 ${totalOk} / 失败 ${totalFail}。把下载的 JSON 放进 数据对接方案/data/。`,
              totalFail === 0 ? 'color:#0a0;font-weight:bold' : 'color:#c60;font-weight:bold');
})();
