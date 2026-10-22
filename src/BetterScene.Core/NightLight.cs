using Microsoft.Win32;

namespace BetterScene.Core;

/// <summary>
/// 读取 Windows「夜间模式」（夜灯 / Night Light）的开关状态。
///
/// Windows 没有公开 API 查询它，状态只存在于 CloudStore 里的一个二进制块。
/// 因此这里**只读**，绝不写入——写错字节会破坏用户的夜灯设置，
/// 而「夜间模式开启时切深色」本身也不需要改夜灯。
///
/// 判定方式：块里有一段固定标记 D0 0A 02 C6 14。
/// 关闭状态下标记位于第 23 字节；开启时系统会插入额外字节，标记随之后移。
/// 同时兼容 data[18]==0x15 这一常见特征，多个信号取或，避免版本差异导致误判。
/// 无法识别时返回 false 并记录日志，规则只是不触发，不会造成错误切换。
/// </summary>
public static class NightLight
{
    private const string StateKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.bluelightreductionstate\" +
        @"windows.data.bluelightreduction.bluelightreductionstate";

    private static readonly byte[] Marker = { 0xD0, 0x0A, 0x02, 0xC6, 0x14 };

    private static bool _unrecognizedLogged;

    public static bool TryRead(out bool enabled)
    {
        enabled = false;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StateKey);
            if (key?.GetValue("Data") is not byte[] data || data.Length < 24) return false;

            enabled = IsOn(data);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"read night light state failed: {ex.Message}");
            return false;
        }
    }

    public static bool IsOn() => TryRead(out bool enabled) && enabled;

    private static bool IsOn(byte[] data)
    {
        // 特征 1：开启时第 18 字节为 0x15（关闭为 0x13）。
        if (data[18] == 0x15) return true;

        // 特征 2：开启时标记被插入的字节往后推。
        int marker = IndexOf(data, Marker);
        if (marker >= 0) return marker > 23;

        if (!_unrecognizedLogged)
        {
            _unrecognizedLogged = true;
            Log.Warn($"night light state not recognized (len={data.Length}), rule will stay idle");
        }

        return false;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    hit = false;
                    break;
                }
            }
            if (hit) return i;
        }
        return -1;
    }
}
