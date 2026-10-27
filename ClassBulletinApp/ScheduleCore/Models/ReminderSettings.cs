namespace ScheduleCore.Models;

/// <summary>
/// 上下课提醒中的一类提醒配置（上课 / 下课 / 放学 共用同一结构）。
/// 文案支持占位符：{p}=节次、{s}=科目、{t}=时间(HH:mm)。
/// </summary>
public class ReminderProfile
{
    /// <summary>本类提醒是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>提前多少秒提醒（精确到秒）；0 = 仅在该事件发生的时刻提醒。</summary>
    public int LeadSeconds { get; set; }

    /// <summary>弹窗标题模板（{p}=节次 {s}=科目 {t}=时间）。</summary>
    public string Title { get; set; } = "";

    /// <summary>弹窗详情模板。</summary>
    public string Detail { get; set; } = "";

    /// <summary>语音播报内容模板（朗读这段文字）。</summary>
    public string VoiceText { get; set; } = "";

    /// <summary>弹幕内容模板（推送到通知条滚动显示；留空则不推送弹幕）。</summary>
    public string Danmaku { get; set; } = "";
}

/// <summary>
/// 上下课提醒设置（参照 ClassIsland 的课前 / 上课 / 下课 / 放学提醒设计）：
/// 依据当前显示的班级 / 教师课表的节次时间，到点自动弹出提醒窗 / 语音 / 通知条弹幕。
/// 上课、下课、放学各自独立配置；提前量精确到秒。
/// </summary>
public class ReminderSettings
{
    /// <summary>总开关：是否启用上下课提醒。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>上课提醒（含课前提前提醒）。</summary>
    public ReminderProfile ClassStart { get; set; } = new();

    /// <summary>下课提醒（含下课前提前提醒）。</summary>
    public ReminderProfile ClassEnd { get; set; } = new();

    /// <summary>放学提醒（当天最后一节结束后）。</summary>
    public ReminderProfile AfterSchool { get; set; } = new();

    /// <summary>提醒时是否播放提示音。</summary>
    public bool PlaySound { get; set; }

    /// <summary>是否启用语音播报（调用系统语音合成 SAPI 朗读提醒内容）。与提示音相互独立。</summary>
    public bool VoiceEnabled { get; set; }

    /// <summary>音色（系统已安装的 TTS 语音，留空 = 系统默认语音）。</summary>
    public string VoiceName { get; set; } = "";

    /// <summary>语音语速 -10（最慢）.. 10（最快），0 为正常。</summary>
    public int VoiceRate { get; set; }

    /// <summary>语音音量 0..100。</summary>
    public int VoiceVolume { get; set; } = 100;

    /// <summary>提醒窗口显示时长（秒）。</summary>
    public int DisplaySeconds { get; set; } = 8;

    public ReminderSettings()
    {
        ClassStart = new ReminderProfile
        {
            Enabled = true,
            LeadSeconds = 60,
            Title = "即将上课 · 第{p}节",
            Detail = "{s}　{t} 开始上课，请回座位准备",
            VoiceText = "第{p}节 {s} 即将上课，请回座位准备",
            Danmaku = "📣 第{p}节 {s} 即将上课"
        };
        ClassEnd = new ReminderProfile
        {
            Enabled = true,
            LeadSeconds = 0,
            Title = "课间休息 · 第{p}节",
            Detail = "{s} 已下课，{t} 下课",
            VoiceText = "第{p}节 {s} 下课，课间休息",
            Danmaku = "📣 第{p}节 {s} 下课，课间休息"
        };
        AfterSchool = new ReminderProfile
        {
            Enabled = true,
            LeadSeconds = 0,
            Title = "放学了",
            Detail = "今天课程全部结束，放学啦",
            VoiceText = "今天的课程全部结束，放学了",
            Danmaku = "📣 放学了，今天课程全部结束"
        };
    }
}
