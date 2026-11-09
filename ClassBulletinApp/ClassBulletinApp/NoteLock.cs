using ScheduleCore.Services;

namespace ClassBulletinApp;

/// <summary>
/// 五个便签编辑窗「锁定便签」复选框的统一读写入口。
///
/// <para>★ 锁定状态 historically 散在四处：作业 = <c>AppSettings.HwWinLocked</c>、
/// 缺交 = <c>MsWinLocked</c>、倒计时 / 值日 / 通知 = <c>PlacementOf(key).Locked</c>、
/// 自定义便签 = <c>CustomNote.Locked</c>（模型字段，不走这里）。
/// 编辑窗如果各写一套，改动任何一个存储位置时必然漏掉一两个窗，
/// 表现就是「这个便签勾了锁定没反应，那个却有效」。</para>
///
/// <para>★ 写完设置立刻发 <see cref="AppEvents.NoteLockChanged"/>：悬浮窗已经开着时
/// 也要当场把锁头图标 / 提示文字切过来，不能等重启。</para>
/// </summary>
public static class NoteLock
{
    public static bool Load(string key)
    {
        var s = Store.LoadSettings();
        return key switch
        {
            "homework" => s.HwWinLocked,
            "missing" => s.MsWinLocked,
            _ => s.PlacementOf(key).Locked
        };
    }

    public static void Save(string key, bool locked)
    {
        var s = Store.LoadSettings();
        switch (key)
        {
            case "homework": s.HwWinLocked = locked; break;
            case "missing": s.MsWinLocked = locked; break;
            default: s.PlacementOf(key).Locked = locked; break;
        }
        Store.SaveSettings(s);
        AppEvents.RaiseNoteLockChanged();
    }
}
