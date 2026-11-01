using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleCore.Services;

// System.Windows.Forms（托盘）也注入全局 using，以下类型有同名冲突
using MediaColor = System.Windows.Media.Color;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace ClassBulletinApp;

/// <summary>
/// 升级后的一次性说明（同一版本只弹一次，由 <c>AppSettings.UpgradeNoticeShownFor</c> 控制）。
///
/// <para>为什么要专门做：r8/r9 这几次改动里有几条**会让人以为"数据丢了"的行为变化**
/// （调课记录突然只作用于某一周、周视图突然从周日开始、清除按钮不再一把清空）。
/// 不解释一次，教室机上看到的就是"课表怎么变回去了"。</para>
/// </summary>
public class UpgradeNoticeWindow : Window
{
    public UpgradeNoticeWindow(string version, IReadOnlyList<(string Title, string Detail)> items)
    {
        Title = "更新说明";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

        root.Children.Add(new TextBlock
        {
            Text = $"已更新到 {version}",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x2A, 0x37)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        root.Children.Add(new TextBlock
        {
            Text = "下面几处跟以前不一样。不需要做任何操作也能正常上课；要改设置时再看第 1 条。这个提示只会出现一次。",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x6B, 0x72, 0x80)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        foreach (var (title, detail) in items)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };

            block.Children.Add(new TextBlock
            {
                Text = "· " + title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0x2D, 0x6C, 0xDF)),
                TextWrapping = TextWrapping.Wrap
            });

            block.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 13,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0x37, 0x41, 0x51)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 2, 0, 0)
            });

            root.Children.Add(block);
        }

        var ok = new Button
        {
            Content = "知道了",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(26, 8, 26, 8),
            HorizontalAlignment = HAlign.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Background = new SolidColorBrush(MediaColor.FromRgb(0xEA, 0xF2, 0xFD)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x9E, 0xC1, 0xF0))
        };
        ok.Click += (_, _) => { DialogResult = true; };

        root.Children.Add(ok);
        Content = root;
    }

    /// <summary>
    /// 按当前状态拼出本次要说明的内容。抽成静态方法便于测试与 <c>--shot</c> 预览。
    /// </summary>
    /// <param name="fromVersion">
    /// 上一次弹说明时的版本（<c>AppSettings.UpgradeNoticeShownFor</c>）。用来决定要不要把
    /// 上一版（1.0.2）的说明也一并带上 —— 有教室机可能跳着版本升上来，漏掉那段解释
    /// 就会看到"课表怎么自己变回去了"。
    /// </param>
    public static List<(string Title, string Detail)> BuildItems(AppSettingsSnapshot snap, string? fromVersion = null)
    {
        var items = new List<(string, string)>();

        // ===== 1.0.3 =====

        // ① 身份变了（最容易"打不开设置"的一条，放最前面）
        items.Add(("开机默认身份改成「电教管理员」",
            "以前登录的账号现在是管理员，仍可正常使用；但开机自动登录改用受限账号 local（密码 000），"
          + "它只能控制本机显示，改不了课表、也进不了数据管理。\n"
          + "要改设置或导入课表：用户中心 →「切换账号」，用管理员账号登录即可，不用重启程序。"));

        // ② 三类角色
        items.Add(("新增用户管理：管理员 / 班主任 / 电教管理员",
            "管理员：改全部数据、发布、管理账号（用户中心 →「管理账号」可新增或从 Excel 名单导入）。\n"
          + "班主任：只能改自己班的课表，改不了别人的、也发布不了。\n"
          + "电教管理员（学生）：只能控制本机显示。\n"
          + "★ 界面上的限制只防误操作，不是安全防护 —— 能改数据文件的人照样能绕过。"));

        // ③ 覆盖策略
        items.Add(("主机可选择是否覆盖本机的手工调课",
            "发布机在「数据管理 → 自动更新设置 → 同步策略」里选。默认是「保留本机调课」。\n"
          + "选「覆盖」后，本机手工改的调课在同步时会被停用（不会删除），"
          + "随时可以在「数据管理 → 恢复被主机停用的本机调课」一键恢复。"));

        // ===== 1.0.2（跳版本升级时补上）=====
        if (NeedsLegacyItems(fromVersion))
        {
            string migrated = string.IsNullOrWhiteSpace(snap.MigratedAt)
                ? ""
                : $"\n你原有的记录已于 {snap.MigratedAt} 自动归入各自所在的周，不会一次全部消失。";

            // ① 调课记录带生效区间（最容易"看起来像丢数据"的一条）
            items.Add(("调课 / 代课现在带「生效日期」",
                $"以前手动改的课会永远顶在课表上，现在只作用于改动时那一周，过期自动回到原课表。"
              + $"想改长期生效的，请改「原始课表」。{migrated}"));

            // ② 周视图起始日
            items.Add(("周视图的「一周从哪天开始」可设置",
                $"目前是 {WeekHelper.StartDayName(snap.WeekStartDay)}开头。"
              + "要改到「显示设置 → 课表周视图 → 一周从…开始」。已排好的调课不会挪位。"));

            // ③ 清除改成三种范围
            items.Add(("「清除临时调课」改成三种范围",
                "可选：只清本周 / 清指定日期范围 / 清全部。不再是一按就把整个学期清空。"
              + "长期改课（改在原始课表上的）永远不会被这个按钮清掉。"));

            // ④ 自动更新
            items.Add(("自动更新支持共享文件夹 / U 盘 / FTP",
                "在「数据管理 → 自动更新设置」里配置。发布机改了 Excel 会自动打包上传，"
              + "其余教室机定时比对摘要自动更新；连不上时继续用本地数据，不影响上课。"));
        }

        return items;
    }

    /// <summary>
    /// 上一次提示停在 1.0.1 或更早（或从未提示过）时，把 1.0.2 的说明也带上。
    /// <para>★ 注意版本号是四段式 <c>1.0.2.0</c>：发布号在 <see cref="Version.Build"/> 位，
    /// <see cref="Version.Minor"/> 恒为 0，按 Minor 判会一直成立（踩过一次）。</para>
    /// 解析失败按"新装、不需要"处理 —— 别让一条版本号把更新说明卡死。
    /// </summary>
    static bool NeedsLegacyItems(string? fromVersion)
    {
        if (string.IsNullOrWhiteSpace(fromVersion)) return false;
        if (!Version.TryParse(fromVersion.Trim(), out var v)) return false;

        // 1.0.x：x 在 Build 位；更老的 0.x 也一并按"需要补说明"处理
        if (v.Major < 1) return true;
        if (v.Major > 1) return false;                 // 将来 1.1.x 之后不必再补 1.0.2 的说明
        int release = v.Build >= 0 ? v.Build : 0;
        return release < 2;
    }
}

/// <summary>拼升级说明需要的当前状态快照（避免窗口直接依赖 Store）。</summary>
public record AppSettingsSnapshot(int WeekStartDay, string MigratedAt);
