# -*- coding: utf-8 -*-
"""
二一排课 · 服务端批量抓取（用 client-access-token 鉴权）
------------------------------------------------------------
用法：python fetch_all.py
产物：data/grade-tree-<年级>.json、class-lectures-<年级>-<classId>.json、
      substitute-list-<年级>.json

★ token 有效期有限（JWT exp），过期后重新登录网页，从 Network 的
  grade-tree 请求头里复制最新 client-access-token，替换下面 TOKEN 即可。
"""
import json, os, time, ssl
import urllib.request

TOKEN = ("eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9."
         "eyJzdWIiOiJudWxsIiwiaXNTcGVjaWFsR3Vlc3QiOiIwIiwibW9iaWxlIjoiIiwiaXNzIjoiMjFjbmp5LmNvbSIsImlzVHJpYWwiOiJmYWxzZSIsInJlbGF0aW9uU2Nob29sSWRzIjoibnVsbCIsInVzZXJJZCI6Im51bGwiLCJyZWFsTmFtZSI6IuabvuiDnOW6tyIsImF1ZCI6ImNvbS5jbjIxankiLCJpc1N1cGVybWFuIjoiMCIsInRlYWNoZXJJZCI6ImUyYzBiY2NmZDc4YzlmYTQxNDAwM2RjZTQwZTRjYmI2IiwiaWRlbnRpdHkiOiIyIiwic2Nob29sSWQiOiI1MTMwNzU3Iiwic2Nob29sTmFtZSI6IuS4reWxseW4guS4nOWHpOS4reWtpiIsImV4cCI6MTc5MzM4NjgwMCwicHJvamVjdElkIjpudWxsLCJpYXQiOjE3OTMzNTg0MjIsImVtYWlsIjpudWxsLCJ1c2VybmFtZSI6bnVsbH0."
         "O-lw-dS5LElEOKDTaGNqriYWxvTLLRbQnzRaMIy_j6w")

API = "https://paike.21cnjy.com/query"
BASE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(BASE, "data")
os.makedirs(DATA, exist_ok=True)

PROJECTS = [
    ("八年级", "77cc8c2dba034b04b0ccb77c542e20d1"),
    ("九年级", "9a2c21ed598a5abeb8ed7c3ad3c56d98"),
    ("七年级", "818007cc3510c7f916204a5043010955"),
]

HEADERS = {
    "client-access-token": TOKEN,
    "x-requested-with": "XMLHttpRequest",
    "accept": "*/*",
    "referer": "https://paike.21cnjy.com/timetable/timetableClass.html",
    "user-agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                   "(KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36 Edg/149.0.0.0"),
}

_ctx = ssl.create_default_context()
_ctx.check_hostname = False
_ctx.verify_mode = ssl.CERT_NONE


def get_json(url, label):
    req = urllib.request.Request(url, headers=HEADERS)
    try:
        with urllib.request.urlopen(req, timeout=25, context=_ctx) as r:
            text = r.read().decode("utf-8", "replace")
        j = json.loads(text)
        if str(j.get("code")) != "0":
            print(f"  x {label} code={j.get('code')} msg={j.get('msg')}")
            return None
        return j
    except Exception as e:
        print(f"  x {label} 异常 {e}")
        return None


def save(obj, name):
    p = os.path.join(DATA, name)
    with open(p, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    print(f"  ok {name} ({os.path.getsize(p)}B)")


def main():
    grand_classes = 0
    for grade, pid in PROJECTS:
        print(f"\n=== {grade}  projectId={pid} ===")
        gt = get_json(f"{API}/grade-tree?projectId={pid}", f"{grade} grade-tree")
        classes = []
        if gt:
            save(gt, f"grade-tree-{grade}.json")
            for g in gt.get("data", []):
                for c in g.get("list", []):
                    classes.append((c["id"], c["name"]))
            print(f"  班级数：{len(classes)}")
        else:
            print(f"  !! {grade} grade-tree 失败，跳过该年级")
        grand_classes += len(classes)

        ok = fail = 0
        for i, (cid, cname) in enumerate(classes, 1):
            lec = get_json(f"{API}/class-lectures?projectId={pid}&classId={cid}", cname)
            if lec:
                save(lec, f"class-lectures-{grade}-{cid}.json")
                ok += 1
            else:
                fail += 1
            time.sleep(0.3)
        print(f"  class-lectures：成功 {ok} / 失败 {fail}")

        subs = get_json(f"{API}/substitute-list?projectId={pid}", f"{grade} substitute-list")
        if subs:
            save(subs, f"substitute-list-{grade}.json")
            print(f"  substitute-list 条数：{len(subs.get('data') or [])}")

    print(f"\n=== 完成，全校共 {grand_classes} 个班 ===\n")


if __name__ == "__main__":
    main()
