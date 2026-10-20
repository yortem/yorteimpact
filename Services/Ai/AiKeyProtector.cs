using System.Runtime.InteropServices;
using System.Text;

namespace YorteImpact.Services.Ai;

/// <summary>
/// Encrypts the stored API key for the current Windows user with DPAPI
/// (<c>crypt32.dll</c>), so <c>settings.json</c> never holds it in plain text.
/// Values that are not encrypted (for example a key pasted into an older
/// settings file) are returned unchanged and encrypted on the next save.
/// </summary>
public static class AiKeyProtector
{
    private const string Prefix = "dpapi:";
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (plain.StartsWith(Prefix, StringComparison.Ordinal)) return plain;

        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var input = new DataBlob { Size = bytes.Length, Data = buffer };
            if (!CryptProtectData(ref input, "YorteImpact AI key", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out DataBlob output))
                return plain;

            try
            {
                byte[] encrypted = new byte[output.Size];
                Marshal.Copy(output.Data, encrypted, 0, output.Size);
                return Prefix + Convert.ToBase64String(encrypted);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        catch
        {
            return plain;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        try
        {
            byte[] bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var input = new DataBlob { Size = bytes.Length, Data = buffer };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, CryptProtectUiForbidden, out DataBlob output))
                    return "";

                try
                {
                    byte[] plain = new byte[output.Size];
                    Marshal.Copy(output.Data, plain, 0, output.Size);
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return "";
        }
    }
}
