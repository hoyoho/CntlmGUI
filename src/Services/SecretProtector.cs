using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CntlmGUI.Services;

/// <summary>
/// 用 Windows DPAPI（当前用户范围）加密本地保存的密码，
/// 避免 settings.json 里出现明文，也避免把密码放到命令行参数里。
/// </summary>
public static class SecretProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>加密为 Base64；空字符串返回空。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Transform(bytes, encrypt: true));
    }

    /// <summary>解密 Base64；空字符串返回空。</summary>
    public static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        var bytes = Convert.FromBase64String(cipher);
        return Encoding.UTF8.GetString(Transform(bytes, encrypt: false));
    }

    private static byte[] Transform(byte[] input, bool encrypt)
    {
        var inPtr = Marshal.AllocHGlobal(input.Length);
        var inBlob = new DataBlob { Size = input.Length, Data = inPtr };
        var outBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inPtr, input.Length);
            var ok = encrypt
                ? CryptProtectData(ref inBlob, "CntlmGUI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), encrypt ? "加密失败" : "解密失败");

            var result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
        }
    }
}
