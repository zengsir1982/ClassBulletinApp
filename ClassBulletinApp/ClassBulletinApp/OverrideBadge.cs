namespace ClassBulletinApp;

/// <summary>
/// 调代课徽章的统一取色与来源说明。
/// <para>★ 目的：一眼区分「这节是我自己在悬浮窗上手动调的」和「这是临时课表导入 / 数据包同步过来的」。
/// 手动调整保持原来的橙（调）/ 蓝（代），导入用紫、同步用青绿，颜色本身就把来源带出来了；</para>
/// <para>不去改科目文字颜色 —— 整屏科目跟着变色反而看不清重点，教室屏 3 米外更分不清深浅。</para>
/// </summary>
internal static class OverrideBadge
{
    /// <summary>来源标识 → 中文说明（未知 / 空值一律按手动处理）。</summary>
    public static string SourceLabel(string? source) => (source ?? "").Trim().ToLowerInvariant() switch
    {
        "temp" => "临时课表导入",
        "remote" => "数据包同步",
        "basepatch" => "长期改课",
        "teacher-view" => "教师课表换算",
        _ => "手动调整"
    };

    /// <summary>
    /// 徽章底色：来源决定色系（手动=橙/蓝、导入=紫/靛、同步=青绿），
    /// 类型决定深浅（「代课」比「调课」稍冷一点），两种信息一次看懂。
    /// </summary>
    public static string Color(string? type, string? source)
    {
        bool sub = string.Equals(type, "代", StringComparison.Ordinal);
        return (source ?? "").Trim().ToLowerInvariant() switch
        {
            "temp" => sub ? "#5B4CDB" : "#8E44AD",
            "remote" => sub ? "#148F77" : "#0E8A6B",
            _ => sub ? "#2D9CDB" : "#E67E22"
        };
    }

    /// <summary>悬停提示：原有备注（若有）+ 来源与类型，例如「临时课表导入（调课）」。</summary>
    public static string Note(string? note, string? type, string? source)
    {
        string kind = string.Equals(type, "代", StringComparison.Ordinal) ? "代课" : "调课";
        string tail = $"{SourceLabel(source)}（{kind}）";
        return string.IsNullOrWhiteSpace(note) ? tail : note.Trim() + "\n" + tail;
    }
}
