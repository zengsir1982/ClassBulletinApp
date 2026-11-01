#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
二一排课 API 解析原型（独立脚本，不依赖班级公布栏 App 主代码）
================================================================
输入：
    data/class-lectures.json   基础课表（class-lectures 接口的 Response）
    data/substitute-list.json  调代课列表（substitute-list 接口的 Response）
输出：
    每个班级的基础课表（slot -> 星期/节次 -> 科目/教师/地点）
    全校调代课的解析结果（区分"调课"与"代课"，转成 (班级, 星期, 节次) 覆盖）

仅用于验证二一排课 API 的数据结构与解析逻辑，不改任何 App 代码。
"""
import json
import os
from datetime import date

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
SLOTS_PER_DAY = 8  # 每周5天 × 每天8节课 = slot 0..39；已确认（project-list 的 nodeSectionList 恰好 8 节/天）
DAY_NAMES = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"]


def slot_to_day_period(slot):
    """slot -> (day_idx 0=周一, period 1-based)。仅当 SLOTS_PER_DAY 正确时成立。"""
    return slot // SLOTS_PER_DAY, slot % SLOTS_PER_DAY + 1


def load(name):
    p = os.path.join(DATA, name)
    if not os.path.exists(p):
        print(f"[跳过] 缺少数据文件: {p}")
        return None
    with open(p, encoding="utf-8") as f:
        return json.load(f).get("data", [])


def parse_base(lectures):
    """class-lectures -> {className: {(day, period): lesson}}"""
    out = {}
    for lec in lectures or []:
        cls = lec.get("className")
        slot = lec.get("slot")
        if cls is None or slot is None:
            continue
        day, period = slot_to_day_period(slot)
        subj = lec.get("subjectName")
        if not subj:
            subj = lec.get("memo") or "（空）"  # 自习 / 教师会议 等写在 memo
        out.setdefault(cls, {})[(day, period)] = {
            "subject": subj,
            "teacher": lec.get("teacherNames"),
            "room": lec.get("roomName"),
            "alternateWeek": lec.get("alternateWeek") or 0,  # 0每周 1单周 2双周
            "memo": lec.get("memo"),
        }
    return out


def parse_subs(subs, only_active_for=None):
    """substitute-list -> [override dict]，区分调课 / 代课。

    调代识别（基于真实数据，subjectName 是真实科目名，不是"代课"）：
      - substituteTeacherNames 非空 且 srcSlot==destSlot → 代课（同节次换老师）
      - substituteTeacherNames 非空 且 srcSlot!=destSlot → 代课+调课（换老师且换时间）
      - substituteTeacherNames 为空 且 srcSlot!=destSlot → 调课（仅换时间）
      - substituteTeacherNames 为空 且 srcSlot==destSlot → 无变动，跳过
    生效判定：only_active_for 传 'YYYY-MM-DD' 时，仅保留
      status != '已失效' 且 validStartDate<=当日<=validEndDate 的记录。
    注意：该接口忽略 startDate/endDate 请求参数，会返回全部历史记录，
          因此"当前有效"必须在本地按日期过滤。
    """
    overrides = []
    for s in subs or []:
        status = s.get("status")
        cls = s.get("className")
        src = s.get("srcSlot")
        dst = s.get("destSlot")
        if cls is None or src is None or dst is None:
            continue
        sub_names = s.get("substituteTeacherNames") or ""
        has_sub = bool(sub_names)
        moved = src != dst
        if not has_sub and not moved:
            continue  # 无代课老师且未换时间 = 无实际变动
        subj = s.get("subjectName") or ""
        orig_teacher = s.get("teacherNames") or ""
        eff_teacher = sub_names if has_sub else orig_teacher
        otype = "代" if has_sub else "调"
        # 本地生效过滤
        if only_active_for:
            vs, ve = s.get("validStartDate"), s.get("validEndDate")
            if status == "已失效":
                continue
            if not (vs and ve and vs <= only_active_for <= ve):
                continue
        overrides.append({
            "className": cls,
            "type": otype,
            "hasSub": has_sub,
            "moved": moved,
            "subject": subj,
            "teacher": eff_teacher,
            "origTeacher": orig_teacher if has_sub else None,
            "subTeacher": sub_names or None,
            "day": slot_to_day_period(dst)[0], "period": slot_to_day_period(dst)[1],
            "srcDay": slot_to_day_period(src)[0], "srcPeriod": slot_to_day_period(src)[1],
            "validStart": s.get("validStartDate"),
            "validEnd": s.get("validEndDate"),
            "status": status,
        })
    return overrides


def load_json(name):
    """通用的 data/<name> 加载器，返回整包 JSON（含 code/msg/data）。"""
    p = os.path.join(DATA, name)
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def load_grade_trees():
    """扫描 data/grade-tree*.json（grade-tree.json 与 grade-tree-<年级>.json 皆可），
    合并所有年级的班级列表 -> [{grade, classId, className}, ...]
    按 classId 去重，防止 grade-tree.json 与 grade-tree-<年级>.json 并存时重复计数。"""
    import glob
    acc = []
    for fp in sorted(glob.glob(os.path.join(DATA, "grade-tree*.json"))):
        payload = load_json(os.path.basename(fp))
        if not payload:
            continue
        acc.extend(parse_grade_tree(payload))
    seen, dedup = set(), []
    for c in acc:
        if c["classId"] in seen:
            continue
        seen.add(c["classId"])
        dedup.append(c)
    return dedup


def load_all_subs():
    """扫描 data/substitute-list*.json（含 substitute-list-<年级>.json），
    合并所有年级的调代课记录 -> list。
    按 (班级,原节次,目标节次,生效起,科目,状态) 去重，防止 sample 与全量并存时重复。"""
    import glob
    acc = []
    for fp in sorted(glob.glob(os.path.join(DATA, "substitute-list*.json"))):
        payload = load_json(os.path.basename(fp))
        if not payload:
            continue
        data = payload.get("data", [])
        if isinstance(data, list):
            acc.extend(data)
    seen, dedup = set(), []
    for s in acc:
        key = (s.get("className"), s.get("srcSlot"), s.get("destSlot"),
               s.get("validStartDate"), s.get("subjectName"), s.get("status"))
        if key in seen:
            continue
        seen.add(key)
        dedup.append(s)
    return dedup


def parse_project_list(payload):
    """project-list 整包 -> [project dict]

    每个 project：
      projectId, name, grade(从 name 解析 七/八/九年级), schoolId,
      workday, holiday, termStartTime,
      sections: [ {part, section, name, start, end} ]   (nodeSectionList，按 part,section 排序)
      common:   [ {name, part, positionStart, positionEnd} ]  (commonSectionList 固定项)
    时间表（sections）即每日 8 节标准时段；common 为 大课间/眼保健操/午休 等固定项，
    它们不占 slot 序号，由 App 渲染层按 part+positionStart 插入。
    """
    import re
    if not isinstance(payload, dict):
        return []
    out = []
    for p in payload.get("data", []) or []:
        if not isinstance(p, dict):
            continue
        name = p.get("name") or ""
        m = re.search(r"(七|八|九)年级", name)
        grade = (m.group(1) + "年级") if m else ""
        sections = []
        for s in sorted(p.get("nodeSectionList", []) or [],
                        key=lambda x: (x.get("part", 0), x.get("section", 0))):
            sections.append({
                "part": s.get("part"), "section": s.get("section"),
                "name": s.get("name"), "start": s.get("startTime"), "end": s.get("endTime"),
            })
        common = []
        for c in p.get("commonSectionList", []) or []:
            common.append({
                "name": c.get("name"), "part": c.get("part"),
                "positionStart": c.get("positionStart"), "positionEnd": c.get("positionEnd"),
            })
        out.append({
            "projectId": p.get("id"), "name": name, "grade": grade,
            "schoolId": p.get("schoolId"), "workday": p.get("workday"),
            "holiday": p.get("holiday"), "termStartTime": p.get("termStartTime"),
            "sections": sections, "common": common,
        })
    return out


def load_project_list():
    """读取 data/project-list.json，返回 parse_project_list 结果；缺失返回 []。"""
    payload = load_json("project-list.json")
    if not payload:
        return []
    return parse_project_list(payload)


def walk_tree(node, grade, acc):
    """递归遍历年级树，收集所有 'classId + 名称' 叶子节点。
    对未知 schema 做容错：凡带 id/classId 且带名称的节点都视为班级。"""
    if isinstance(node, dict):
        cid = node.get("classId") if node.get("classId") is not None else node.get("id")
        cname = node.get("className") if node.get("className") is not None else node.get("name")
        nm = node.get("name") or node.get("gradeName") or node.get("title")
        if cid is not None and cname is not None:
            acc.append({"grade": grade, "classId": cid, "className": cname})
        # 没有 classId 但有 name 的节点视为"年级/分组"容器，下钻时作为 grade 上下文
        new_grade = grade
        if nm and cid is None:
            new_grade = (grade + "/" + nm) if grade else nm
        for v in node.values():
            if isinstance(v, (dict, list)):
                walk_tree(v, new_grade, acc)
    elif isinstance(node, list):
        for it in node:
            walk_tree(it, grade, acc)


def parse_grade_tree(payload):
    """grade-tree 整包 -> [{grade, classId, className}, ...]

    已知真实 schema（2026-10-30 实测）：
        data: [{gradeId, gradeName, grade, list:[{id, name, sort}, ...]}]
    其中 list[].id = classId，list[].name = 班级名。
    优先按此精确解析；若结构不符（未知 schema），退回通用 walk_tree 容错遍历。
    """
    if not isinstance(payload, dict):
        return []
    data = payload.get("data", [])
    acc = []
    # 精确分支：data[i].list[].{id,name}
    if isinstance(data, list):
        for g in data:
            if not isinstance(g, dict):
                continue
            gname = g.get("gradeName") or g.get("name") or ""
            for c in g.get("list", []) or []:
                if not isinstance(c, dict):
                    continue
                cid = c.get("id") or c.get("classId")
                cname = c.get("name") or c.get("className")
                if cid and cname:
                    acc.append({"grade": gname, "classId": cid, "className": cname})
    if acc:
        return acc
    # 兜底：未知结构，递归找所有带 id+name 的叶子
    acc2 = []
    walk_tree(payload, "", acc2)
    return acc2


def class_lectures_all():
    """扫描 data/ 下所有 class-lectures-*.json，合并成 {className: {slot→lesson}}。
    约定命名：class-lectures-<classId>.json（由 grade-tree 枚举后逐班抓取）。"""
    out = {}
    if not os.path.isdir(DATA):
        return out
    import glob
    for fp in glob.glob(os.path.join(DATA, "class-lectures-*.json")):
        payload = load_json(os.path.basename(fp))
        if not payload:
            continue
        lectures = payload.get("data", [])
        m = parse_base(lectures)
        for cls, lessons in m.items():
            out.setdefault(cls, {}).update(lessons)
    return out


def main():
    import glob
    from collections import defaultdict

    gt_files = sorted(glob.glob(os.path.join(DATA, "grade-tree*.json")))
    sub_files = sorted(glob.glob(os.path.join(DATA, "substitute-list*.json")))
    lec_files = sorted(glob.glob(os.path.join(DATA, "class-lectures-*.json")))
    pl = load_project_list()
    print("=== 数据闭环状态 ===")
    print(f"  {'🟢' if pl else '🔴'} project-list.json       ({len(pl)} 个排课项目：七/八/九年级课表方案)")
    print(f"  {'🟢' if gt_files else '🔴'} grade-tree*.json        ({len(gt_files)} 个文件：年级树/班级列表)")
    print(f"  {'🟢' if lec_files else '🔴'} class-lectures-*.json    ({len(lec_files)} 个班已抓)")
    print(f"  {'🟢' if sub_files else '🔴'} substitute-list*.json   ({len(sub_files)} 个文件：调代课)")
    print()

    # 1) 班级列表（多年级合并）
    classes = load_grade_trees()
    if classes:
        by_grade = defaultdict(list)
        for c in classes:
            by_grade[c["grade"]].append(c)
        print(f"=== 班级列表（共 {len(classes)} 个班，{len(by_grade)} 个年级）===")
        for g in sorted(by_grade):
            print(f"  [{g}] {len(by_grade[g])} 个班")
            for c in by_grade[g]:
                print(f"      {c['className']}  classId={c['classId']}")
    else:
        print("[提示] 缺 grade-tree*.json → 无法枚举全部班级。")

    # 1.5) 排课项目 / 每日时间表（来自 project-list.json）
    if pl:
        print("\n=== 排课项目 / 每日时间表（来自 project-list.json）===")
        for p in pl:
            term = p["termStartTime"] or "（未设置）"
            print(f"  {p['grade'] or p['name']}  projectId={p['projectId']}  "
                  f"workday={p['workday']} holiday={p['holiday']}  termStart={term}")
            line = "  ".join(f"{s['name']}({s['start']}-{s['end']})" for s in p["sections"])
            print(f"    时间表: {line}")
            if p["common"]:
                cfix = " / ".join(f"{c['name']}(part{c['part']}@pos{c['positionStart']})" for c in p["common"])
                print(f"    固定项: {cfix}")

    # 2) 基础课表：优先合并多班，否则用单班示例
    base_map = class_lectures_all()
    if not base_map and os.path.exists(os.path.join(DATA, "class-lectures.json")):
        base_map = parse_base(load("class-lectures.json"))
    target = "八年级(1)班"
    if target in base_map:
        print(f"\n=== 基础课表示例：{target}（{len(base_map)} 个班已载入）===")
        for (day, period), les in sorted(base_map[target].items()):
            room = f" /{les['room']}" if les["room"] else ""
            print(f"  {DAY_NAMES[day]} 第{period}节: {les['subject']} / {les['teacher']}{room}")
    elif base_map:
        sample = next(iter(base_map))
        print(f"\n=== 基础课表示例：{sample}（共 {len(base_map)} 个班）===")
        for (day, period), les in sorted(base_map[sample].items()):
            room = f" /{les['room']}" if les["room"] else ""
            print(f"  {DAY_NAMES[day]} 第{period}节: {les['subject']} / {les['teacher']}{room}")

    # 3) 调代课（合并所有年级的 substitute-list-<年级>.json）
    subs = load_all_subs()
    if subs:
        TODAY = date.today().isoformat()
        all_ov = parse_subs(subs)                       # 全部（含历史）
        active_ov = parse_subs(subs, only_active_for=TODAY)  # 今日生效
        n_dai = sum(1 for o in all_ov if o["type"] == "代")
        n_tiao = sum(1 for o in all_ov if o["type"] == "调")
        n_daitiao = sum(1 for o in all_ov if o["hasSub"] and o["moved"])
        print(f"\n=== 调代课解析（合并全部年级）===  总计 {len(all_ov)} 条；今日({TODAY})生效 {len(active_ov)} 条")
        print(f"  类型分布：代课 {n_dai} / 调课 {n_tiao} / 代+调(同时换师换时) {n_daitiao}")
        print("  今日生效样例（前 10 条）：")
        for o in active_ov[:10]:
            teacher = o["teacher"] + (f"（代：{o['subTeacher']}）" if o["hasSub"] else "")
            move = f"  原{DAY_NAMES[o['srcDay']]}第{o['srcPeriod']}节" if o["moved"] else ""
            print(f"  {o['className']} [{o['type']}] {DAY_NAMES[o['day']]}第{o['period']}节 "
                  f"{o['subject']} / {teacher}{move}")
        if not active_ov:
            print("  （今日无生效调代课 → 全部为历史'已失效'，符合预期：最近记录 validEnd<=今天）")
        print("  全部样例（前 12 条，验证识别是否正确）：")
        for o in all_ov[:12]:
            teacher = o["teacher"] + (f"（代：{o['subTeacher']}）" if o["hasSub"] else "")
            move = f"  原{DAY_NAMES[o['srcDay']]}第{o['srcPeriod']}节" if o["moved"] else ""
            print(f"  {o['className']} [{o['type']}] {DAY_NAMES[o['day']]}第{o['period']}节 "
                  f"{o['subject']} / {teacher}{move}  ({o['validStart']}~{o['validEnd']} {o['status']})")
    else:
        print("\n[提示] 缺 substitute-list*.json → 跳过调代课解析。")


if __name__ == "__main__":
    main()
