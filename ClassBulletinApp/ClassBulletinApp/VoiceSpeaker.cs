using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ClassBulletinApp;

/// <summary>
/// 系统语音播报（Windows SAPI / SpVoice）。直接走 COM，不额外引入 NuGet 包。
/// 播报为异步，不会阻塞界面；任何失败都静默忽略，绝不影响提醒弹窗。
/// </summary>
public static class VoiceSpeaker
{
    // SVSFlagsAsync = 1（异步播报），SVSFPurgeBeforeSpeak = 2（打断上一条）
    private const int SpeakFlags = 1 | 2;

    private static object? _voice;
    private static readonly object Gate = new();

    private static object? GetVoice()
    {
        if (_voice != null) return _voice;
        lock (Gate)
        {
            if (_voice != null) return _voice;
            try
            {
                var t = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (t == null) return null;
                _voice = Activator.CreateInstance(t);
            }
            catch { _voice = null; }
            return _voice;
        }
    }

    /// <summary>枚举系统已安装的语音（如「Microsoft Huihui Desktop」）。</summary>
    public static List<string> GetInstalledVoices()
    {
        var list = new List<string>();
        try
        {
            dynamic? sp = GetVoice();
            if (sp == null) return list;
            dynamic tokens = sp.GetVoices(string.Empty, string.Empty);
            int count = (int)tokens.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic tok = tokens.Item(i);
                    string desc = (string)tok.GetDescription();
                    if (!string.IsNullOrWhiteSpace(desc) && !list.Contains(desc)) list.Add(desc);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>异步朗读一段文本。voiceName 为空时自动优先选择中文语音（避免默认英文语音读中文）。</summary>
    public static void Speak(string text, string? voiceName, int rate, int volume)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            dynamic? sp = GetVoice();
            if (sp == null) return;
            sp.Rate = Math.Clamp(rate, -10, 10);
            sp.Volume = Math.Clamp(volume, 0, 100);

            string target = voiceName ?? "";
            if (string.IsNullOrWhiteSpace(target)) target = PickChineseVoice(sp);

            if (!string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    dynamic tokens = sp.GetVoices(string.Empty, string.Empty);
                    int count = (int)tokens.Count;
                    for (int i = 0; i < count; i++)
                    {
                        dynamic tok = tokens.Item(i);
                        if (string.Equals((string)tok.GetDescription(), target, StringComparison.OrdinalIgnoreCase))
                        {
                            sp.Voice = tok;
                            break;
                        }
                    }
                }
                catch { }
            }

            sp.Speak(text, SpeakFlags);
        }
        catch
        {
            // 语音合成不可用（未安装中文语音 / SAPI 被禁用）时静默跳过
        }
    }

    /// <summary>在系统语音里挑一个中文语音（简体优先），没有则返回空串用系统默认。</summary>
    private static string PickChineseVoice(dynamic sp)
    {
        try
        {
            dynamic tokens = sp.GetVoices(string.Empty, string.Empty);
            int count = (int)tokens.Count;
            for (int i = 0; i < count; i++)
            {
                string d = (string)tokens.Item(i).GetDescription() ?? "";
                if (d.IndexOf("Simplified", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    d.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0)
                    return d;
            }
            for (int i = 0; i < count; i++)
            {
                string d = (string)tokens.Item(i).GetDescription() ?? "";
                if (d.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.IndexOf("中文", StringComparison.OrdinalIgnoreCase) >= 0)
                    return d;
            }
        }
        catch { }
        return "";
    }

    /// <summary>立即停止当前播报。</summary>
    public static void Stop()
    {
        try
        {
            dynamic? sp = GetVoice();
            sp?.Speak(string.Empty, 3);   // SVSFPurgeBeforeSpeak
        }
        catch { }
    }
}
