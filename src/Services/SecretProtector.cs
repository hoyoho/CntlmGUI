using System.Runtime.InteropServices;
using System.Text;

namespace CntlmGUI.Services;

/// <summary>
/// 使用 Windows DPAPI（当前用户）加密/解密密码，避免引入 NuGet 依赖。
/// </summary>
internal static class SecretProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = Encoding.UTF8.GetBytes(plain);
        var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            if (!CryptProtectData(ref input, "CntlmGUI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                return null;
            }

            try
            {
                var encrypted = new byte[output.cbData];
                Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                return Convert.ToBase64String(encrypted);
            }
            finally
            {
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;

        byte[] data;
        try
        {
            data = Convert.FromBase64String(protectedBase64);
        }
        catch
        {
            return null;
        }

        var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                return null;
            }

            try
            {
                var plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, output.cbData);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }
}
