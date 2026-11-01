# 04 · ErYiClient 并入方案（二一排课数据源）

> 目的：把已在 `数据对接方案/data/` 验证过的二一排课 API 解析逻辑，封装成班级公布栏 App 的
> 一个**数据源客户端**，让 App 能直接吃「浏览器抓包落盘的 JSON」，不再依赖手工 Excel 整理。
> 同时把**手动 Excel 导入**保留为最终兜底，并预留**可选实时拉取**（配置 Cookie 后直连 API）。

---

## 1. 三档数据源定位

| 档位 | 说明 | 状态 |
|---|---|---|
| **ErYi 缓存（主）** | 浏览器抓包 → 落盘 JSON → `ErYiClient.BuildFromCache()` 归一化 | ✅ 本次交付 |
| **Excel 导入（兜底）** | 现有 `ScheduleImporter`（全校总课表 / 单班 / 教师课表） | 保留不动 |
| **ErYi 实时拉取（可选）** | 配置 Cookie 后 `HttpClient` 直连三接口写回缓存 | ⏸ 接口需登录，默认关闭，仅留接口 |

设计原则：**任何一档失败都回退到上一档**，绝不导致展示空白。

---

## 2. 数据映射（二一 JSON → ScheduleCore 模型）

| 二一文件 | 目标模型 | 关键字段映射 |
|---|---|---|
| `project-list.json` | `ErYiMeta.Grades[]` | `nodeSectionList` → 每级每日 8 节时间表（period→`start-end`）；`commonSectionList` → 固定项（大课间/眼保健操/午休，含插入位置） |
| `grade-tree-<年级>.json` | （仅枚举用，实际课表来自 class-lectures） | `data[].list[].{id,name}` → 班级清单 |
| `class-lectures-<年级>-<CID>.json` | `ScheduleFile.Classes[]` | `slot` → `day=slot/8, period=slot%8+1`；`subjectName/teacherNames/roomName/alternateWeek(0/1/2)/memo` → `Lesson` |
| `substitute-list-<年级>.json` | `List<LessonOverride>` | `substituteTeacherNames` 非空 → 代课；`srcSlot≠destSlot` → 调课；今日生效过滤（`status≠已失效` 且 `validStart≤今天≤validEnd`）→ `Source="temp"`, `Type="调"/"代"` |

> 合并策略：**全校所有年级的所有班一次性载入同一个 `ScheduleFile`**（约 90 班，内存可控）。
> `SelectedClass` 命中即显示该班；班级名含「七/八/九年级」可反查年级→取对应固定项集。
> 调代覆盖以 `ClassName|DayIdx|Period` 为唯一键，跨年级天然不冲突。

---

## 3. ErYiClient 类（见 `ClassBulletinApp/ScheduleCore/Services/ErYiClient.cs`）

核心方法：

- `ErYiResult BuildFromCache(string dir)`
  - 读 `project-list.json` → 时间表 + 固定项（按年级）
  - 扫 `class-lectures-*.json` → 合并成 `ScheduleFile`（每节 `Lesson.TimeRange` 注入对应时段，支撑「当前课高亮」）
  - 扫 `substitute-list-*.json` → 今日生效的 `LessonOverride`（`Source="temp"`）
  - 返回 `(ScheduleFile, List<LessonOverride>, ErYiMeta)`
- `void FetchLive(string baseUrl, string cookie, string dir)` —— 可选实时拉取（接口需登录，默认关闭；`project-list` 端点 URL 尚未确认，先留结构化桩）
- 内部 DTO + 解析：属性名大小写不敏感（`PropertyNameCaseInsensitive=true`），直接对上二一 camelCase 字段
- 多年级：全部并入一个 `ScheduleFile`；`ParseGrade()` 从班级名反查年级
- 固定项：两级表示——`ErYiMeta.Grades[].Fixed[]`（原始 part + afterSection）与**随课表持久化的** `ScheduleFile.FixedByGrade[年级] → List<FixedSlot>{Name, AfterPeriod, TimeRange}`。`AfterPeriod` 已换算成全局节次（part1：K+1，part3：K+5），`TimeRange` 由相邻两节间隙推出（大课间 08:40-09:10、眼保健操 10:40-10:55 / 15:30-15:45、七年级午休 11:35-14:00）。

`ErYiResult` / `ErYiMeta` / `GradeInfo` / `FixedSection` 一并定义在 `ErYiClient.cs` 内，便于 UI 直接消费。

---

## 4. 接入点与改动清单

1. **`AppSettings` 新增字段**（低风险，纯增）：
   ```csharp
   public string ScheduleSource { get; set; } = "ErYi";   // "ErYi" | "Excel"
   public string ErYiDataDir { get; set; } = "";          // 留空=默认 AppData/eryi
   public string ErYiCookie { get; set; } = "";           // 仅实时拉取用，默认空=关闭
   public string ErYiBaseUrl { get; set; } = ErYiClient.DefaultBaseUrl;
   ```
2. **启动加载（`MainWindow.Window_Loaded`，已实施）**：
   - 新增私有方法 `TryLoadErYi(settings, out fromErYi, out overrideCount)`：
     - `ScheduleSource != "ErYi"` → 直接返回 null，走原有 `Store.LoadSchedule()`（Excel 持久化结果）。
     - 解析缓存目录：优先 `settings.ErYiDataDir`；为空则用 `%APPDATA%/ClassBulletinApp/eryi`；**DEBUG 构建**下若默认目录不存在，回退到开发目录 `F:\study\班级公布栏\数据对接方案\data`（免复制直接验证）。
     - 调 `ErYiClient.BuildFromCache(dir)`；`Classes.Count>0` 时持久化 `Store.SaveSchedule` + `Store.SaveOverrides`。
     - **调代覆盖合并**：用新的 `Source="temp"` 覆盖**替换**旧的 temp，**保留**用户手填的 `Source="manual"`（语义与现有 `ImportTempSchedule` 一致，避免清掉手动记录）。
     - 异常 / 空缓存 → 返回 null，回退 Excel。
   - `Window_Loaded` 改为：`schedule = TryLoadErYi(...)`；为 null 时 `schedule = Store.LoadSchedule()`；状态栏区分 ErYi / Excel 来源提示。
   - `ApplyImportedSchedule`（Excel 导入）末尾新增 `st.ScheduleSource = "Excel"`：用户主动用 Excel 定义课表后，下次启动不再被 ErYi 缓存覆盖（平顺过渡：老设置无此字段时反序列化为默认 "ErYi"，缓存空则回退 Excel）。
3. **调代覆盖应用**：`TeacherOverrideApplier.BuildTeacherOverrides()` 已接受 `IEnumerable<LessonOverride>`，ErYi 的 `Source="temp"` 覆盖天然接入，无需改。
4. **数据管理 Tab**：新增「从二一缓存刷新」「导出当前课表为 Excel」按钮（复用 `ScheduleImporter` 反向导出，可选）。
5. **悬浮窗 / 桌面背面**（✅ 已实施 P3）：`BoardBuilder.Build` 按「班级名 → 年级 → `FixedByGrade`」取固定时段，插到对应节次之后，生成 `PeriodRow{IsFixed=true, Period=0}`；悬浮窗（`ScheduleWindow.xaml`）用 DataTrigger 渲染成灰色紧凑分隔条（小字号灰字），桌面背景（`BackdropRenderer`）跳过固定行以免挤压 8 节课。`Period=0` 天然被拖动调课（`row.Period > 0`）与上课提醒（`row.Period <= 0` 跳过）排除。

---

## 5. 多年级部署说明

- 一台教室机只对应一个班（含年级），但载入全校 90 班做成**统一部署包**最省事：每台机只 `SelectedClass` 不同。
- 若需按年级分发，可在 `ErYiDataDir` 只放该年级的 `class-lectures-<年级>-*` 子集，`BuildFromCache` 同样正确（它按文件扫描）。
- 固定项差异（七年级比八/九多一个「午休」）由 `ErYiMeta` 按年级给出，渲染时跟随当前班所在年级。

---

## 6. 实时拉取（可选增强，配置门槛）

- 仅当 `ErYiCookie` 非空时启用；默认关闭（教室机难以稳定获取登录态，ToS 也偏灰）。
- `FetchLive` 用 `HttpClient`（`cookie` 注入请求头）依次请求 `project-list → grade-tree → class-lectures → substitute-list`，写盘成与浏览器抓包**完全一致**的产物目录，之后走 `BuildFromCache`。
- 失败一律回退缓存 / Excel，不影响展示。
- 待确认：`project-list` 接口的确切 URL（用户贴的数据来自某列表接口，路径待补）。

---

## 7. 实施步骤（分阶段）

- **P1 缓存载入**：`ErYiClient.BuildFromCache` + 映射 + 时间区间注入（✅ 已交付，并经 C# 端到端验证）
- **P2 启动切换**：`AppSettings` 字段 + `MainWindow` 加载分支（✅ 已实施：`TryLoadErYi` / `ResolveErYiDataDir` / Excel 切源；WPF 主项目编译 0 错 0 警，C# 端到端验证通过）
- **P3 固定项渲染**：✅ 已实施——`ScheduleFile.FixedByGrade` 持久化 + `BoardBuilder` 插行 + 悬浮窗灰色分隔条（桌面背景跳过）。真实 90 班数据验证：七年级 4 项（含午休）、八/九年级各 3 项，时间区间与渲染顺序全部正确，序列化往返不丢。
- **P4 实时拉取**：`FetchLive` 实现（补 `project-list` 端点后）
- **P5 数据管理 UI**：刷新 / 导出按钮

---

## 8. 风险与待确认

1. `class-lectures` 真实响应字段需一份真实样本**最终确认**（已按已知字段写；落盘后跑一次即验证）。
2. 实时拉取需登录态，教室机难获取 → **缓存为主**是现实选择。
3. 全校课表一次性载入：`ScheduleFile` 约 90 班，内存可控；若未来扩到全校含小学部再评估分年级懒加载。
4. 单双周（`alternateWeek` 0/1/2）已映射到 `Lesson.Week`，与现有单双周渲染一致。

---

## 9. 当前数据状态与下一步（2026-10-30 实施记录）

### 9.1 ✅ `data/` 真实数据已抓全（2026-10-30 20:2x 更新）
- 已确认二一鉴权靠请求头 **`client-access-token`（JWT）**，而非 cookie 会话；JWT 也存在 cookie `user`（值形如 `姓名@//JWT`）。
- 用 `fetch_all.py`（urllib 带 `client-access-token`）**从服务端直接抓全**：
  - `grade-tree-七/八/九.json`（各 30 班，共 90）
  - `class-lectures-<年级>-<CID>.json`（90 个班）
  - `substitute-list-七/八/九.json`（三校合计 4070 条历史）
  - `data/` 共 97 个 json、约 8.2 MB。
- 浏览器端 `batch_download_classes.js` 已同步为 `client-access-token` 版（自动从 cookie 读 JWT），往后刷新数据直接在排课页控制台跑即可。

### 9.2 真实数据字段（已核实，与 DTO 一致）
- `class-lectures.data[]`：`className / slot / subjectName / shortSubjectName / teacherNames / roomName / alternateWeek / memo / lessonHours / isMergeClass` 等。
- `alternateWeek`：**0=全周、1=单周、2=双周，且可能为 `null`（按全周处理）**。
- `substitute-list.data[]`：`className / srcSlot / destSlot / subjectName / teacherNames / substituteTeacherNames / validStartDate / validEndDate / status`。
- **代课记录 `subjectName` 可能是"代课"占位**，真实科目需从原课表 `srcSlot` 反查。

### 9.3 验证记录（C# 端到端，合成数据）
在临时目录用真实 `project-list.json` + 合成 `class-lectures` / `substitute-list` 调用 `ErYiClient.BuildFromCache`：
- 周一第1节时间 = `08:00-08:40` ✓（修复前误为 14:00）
- 周五第7节时间 = `15:45-16:25` ✓（修复前为空）
- 今日生效覆盖：代课 1 条 + 调课 1 条正确生成（`Source="temp"`）
- 已失效（`validEnd`<今天）条目被正确过滤

### 9.4 修复的时间表 bug（已合入 ErYiClient.cs）
二一 `nodeSectionList` 中**上午(part1)与下午(part3)的 `section` 都从 0 开始**（上午 0-3 = 第1-4节，下午 0-3 = 第5-8节）。
原实现用 `section` 单独作字典 key，导致下午 `section0`(14:00) 覆盖上午 `section0`(08:00) 且节次错位。
已改为映射为**全局节次 1-8**：`period = part==1 ? section+1 : section+5`，`class-lectures` 取时间用 `slot%8+1`。

### 9.5 真实数据端到端验证结果（C# `BuildFromCache`，2026-10-30）
用 `data/` 全量真实数据跑 `ErYiClient.BuildFromCache`：
- **班级数 90**（七/八/九各 30），三个年级均 8 节时间表；
- 单双周并存格 **120 格 / 240 条 Lesson**（`Lesson.Week` 1/2，UI 已按 parity 筛选）；
- 今日生效覆盖 **602 条**（去重后）；
- 覆盖中科目仍为"代课"的条数 **0 / 602**（反查生效）；
- 八年级(8)班周一 8 节时间 `08:00-08:40 … 16:35-17:15` 全部正确。

### 9.6 真实数据下修复的两个 bug（已合入 ErYiClient.cs）
1. **`lecturesDto.AlternateWeek` 由 `int` 改 `int?`**：七/八年级每个班文件都含 `alternateWeek: null`（各 60 条），`int` 遇 null 抛 `JsonException` → 整个文件被 `catch` 跳过，导致只加载九年级 30 班。改为可空、`Week = AlternateWeek ?? 0`。
2. **代课科目反查**：`substitute-list` 中 `subjectName == "代课"` 的记录，改从该班原课表 `srcSlot` 取真实科目（如 七年级(26)班 周2第6节 原为"代课"，实际为「劳动」）。

### 9.7 刷新数据的两种方式
- **服务端**：`python fetch_all.py`（`TOKEN` 常量需替换为最新 `client-access-token`，JWT 约 8 小时过期）。
- **浏览器**：登录排课页 → F12 Console → 跑 `batch_download_classes.js`（自动从 cookie 读 JWT）→ 下载的 json 覆盖进 `data/`。
