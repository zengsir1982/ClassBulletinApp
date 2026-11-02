using System;
using System.Collections.Generic;

namespace ScheduleCore.Services;

/// <summary>
/// 版本号比较（「软件更新」用）：把 <c>v1.0.10</c> / <c>1.0.9</c> / <c>1.0.5-beta</c> 这类标签
/// 归一成 4 段数字再比 —— 字符串比较会把 1.0.10 判成比 1.0.9 旧。
/// <para>放在 ScheduleCore 是为了能被 ParserTest 直接回归（<c>ParserTest updatever</c>）。</para>
/// </summary>
public static class AppVersionCompare
{
    /// <summary>remote 是否比 current 新。</summary>
    public static bool IsNewer(string? remote, string? current)
    {
        if (TryParse(remote, out var a) && TryParse(current, out var b)) return a > b;

        // 解析不出来（异常标签）：只要文本不同就当"有新版本"，让用户自己看一眼
        return !string.Equals(Clean(remote), Clean(current), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把标签归一成 4 段 <see cref="Version"/>（缺的段补 0）。</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        string clean = Clean(text);
        if (clean.Length == 0) return false;

        var nums = new List<int>();
        foreach (var part in clean.Split('.'))
        {
            if (!int.TryParse(part, out int n) || n < 0) return false;
            nums.Add(n);
        }
        while (nums.Count < 4) nums.Add(0);
        if (nums.Count > 4) nums.RemoveRange(4, nums.Count - 4);

        version = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    /// <summary>只保留开头的数字与点（<c>v1.0.5</c> → <c>1.0.5</c>，<c>1.0.5-beta</c> → <c>1.0.5</c>）。</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new System.Text.StringBuilder();
        foreach (char c in text.Trim())
        {
            if (char.IsDigit(c) || c == '.') sb.Append(c);
            else if (sb.Length > 0) break;      // 遇到后缀（-beta / 空格）就收尾
        }
        return sb.ToString().Trim('.');
    }
}
