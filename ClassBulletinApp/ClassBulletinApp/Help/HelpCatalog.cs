using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassBulletinApp.Help;

/// <summary>一篇帮助主题。</summary>
public sealed class HelpTopic
{
    public string Id = "";
    public string Title = "";
    /// <summary>分组：<c>quick</c>＝快速上手，<c>fix</c>＝出问题怎么办。</summary>
    public string Group = "quick";
    /// <summary>卡片上那一行小字（一句话说清"这篇解决什么"）。</summary>
    public string Summary = "";
    /// <summary>正文（极简标记，见 <see cref="HelpMarkup"/>）。</summary>
    public string Body = "";
    /// <summary>去掉标记后的纯文本，供搜索用。</summary>
    public string Plain = "";
}

/// <summary>
/// 帮助内容目录。正文全部内嵌在 <c>Resources/Help/help.txt</c>，格式：
/// <code>
/// == 主题id | 标题 | 分组 | 摘要
/// 正文…
/// </code>
///
/// <para>★ 目录和搜索索引都从这一份文件**自动生成**，不再手写第二份清单 ——
/// 手写的那种索引，改一次标题就会有一处对不上。</para>
///
/// <para>★ 内容内嵌而不是放外部目录：30 台教室机靠数据包互相覆盖，任何"可单独替换的
/// 说明文件夹"最后都会变成半新半旧。跟着版本走，至少每一版是自洽的。</para>
/// </summary>
public static class HelpCatalog
{
    const string ResourceUri = "pack://application:,,,/Resources/Help/help.txt";

    static List<HelpTopic>? _topics;

    /// <summary>全部主题（按文件里的书写顺序）。</summary>
    public static IReadOnlyList<HelpTopic> Topics => _topics ??= Load();

    public static HelpTopic? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : Topics.FirstOrDefault(t => t.Id == id);

    public static IEnumerable<HelpTopic> Group(string g) => Topics.Where(t => t.Group == g);

    /// <summary>
    /// 按关键字搜标题 + 正文。中文用子串匹配就够（几十篇的量，分词反而会把"作业"搜丢）；
    /// 标题命中的排在前面 —— 人搜"作业"想要的是那篇讲作业的，不是正文里顺口提到作业的。
    /// </summary>
    public static List<HelpTopic> Search(string? keyword)
    {
        string kw = (keyword ?? "").Trim();
        if (kw.Length == 0) return new List<HelpTopic>();

        return Topics
            .Select(t => new
            {
                Topic = t,
                InTitle = t.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)
                          || t.Summary.Contains(kw, StringComparison.OrdinalIgnoreCase),
                InBody = t.Plain.Contains(kw, StringComparison.OrdinalIgnoreCase)
            })
            .Where(x => x.InTitle || x.InBody)
            .OrderByDescending(x => x.InTitle)
            .Select(x => x.Topic)
            .ToList();
    }

    static List<HelpTopic> Load()
    {
        var list = new List<HelpTopic>();

        string text;
        try
        {
            var sri = System.Windows.Application.GetResourceStream(new Uri(ResourceUri));
            if (sri == null) return list;
            using var sr = new StreamReader(sri.Stream, Encoding.UTF8);
            text = sr.ReadToEnd();
        }
        catch
        {
            return list;    // 资源没打包进来时，帮助窗口显示"内容缺失"而不是崩
        }

        HelpTopic? cur = null;
        var body = new StringBuilder();

        void Commit()
        {
            if (cur == null) return;
            cur.Body = body.ToString().Trim();
            cur.Plain = StripMarkup(cur.Body);
            list.Add(cur);
            body.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            if (line.StartsWith("==", StringComparison.Ordinal))
            {
                Commit();
                var parts = line.TrimStart('=').Split('|');
                cur = new HelpTopic
                {
                    Id = parts.Length > 0 ? parts[0].Trim() : "",
                    Title = parts.Length > 1 ? parts[1].Trim() : "",
                    Group = parts.Length > 2 ? parts[2].Trim() : "quick",
                    Summary = parts.Length > 3 ? parts[3].Trim() : ""
                };
                if (cur.Id.Length == 0) cur = null;
                continue;
            }
            if (cur != null) body.AppendLine(line);
        }
        Commit();

        return list;
    }

    /// <summary>去掉标记，只留可搜的纯文本。</summary>
    static string StripMarkup(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var raw in s.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("![](", StringComparison.Ordinal)) continue;

            line = line.TrimStart('!', '>', '#', '-', ' ', '△');
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\d+[.、)]\s*", "");
            line = line.Replace("**", "").Replace("`", "");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[(.+?)\]\((.+?)\)", "$1");
            sb.Append(line).Append(' ');
        }
        return sb.ToString();
    }
}
