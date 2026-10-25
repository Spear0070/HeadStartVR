using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HeadStartVR.Services;

public static class IconHelper
{
    /// <summary>Extract the exe's own icon at up to 256px (UE shipping exes usually carry the game icon).</summary>
    public static ImageSource? ExtractIcon(string exePath, int size = 256)
    {
        if (!File.Exists(exePath)) return null;
        var icons = new IntPtr[1];
        var ids = new int[1];
        try
        {
            var n = PrivateExtractIcons(exePath, 0, size, size, icons, ids, 1, 0);
            if (n == 0 || icons[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
        }
    }

    public static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);
}

/// <summary>Polls Xbox-style controllers (XInput) for the "inject now" combo: hold Back + Start.</summary>
public static class Gamepad
{
    private const ushort XINPUT_GAMEPAD_START = 0x0010;
    private const ushort XINPUT_GAMEPAD_BACK = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int index, out XINPUT_STATE state);

    public static bool IsBackStartHeld()
    {
        try
        {
            for (var i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out var s) == 0 &&
                    (s.wButtons & (XINPUT_GAMEPAD_BACK | XINPUT_GAMEPAD_START)) == (XINPUT_GAMEPAD_BACK | XINPUT_GAMEPAD_START))
                    return true;
            }
        }
        catch
        {
            // XInput missing: combo unavailable.
        }
        return false;
    }
}

/// <summary>Spoken prompts through Windows' built-in voice, so you hear progress in the headset.</summary>
public static class Voice
{
    private static dynamic? _voice;
    public static bool Enabled { get; set; } = true;

    public static void Say(string text)
    {
        if (!Enabled) return;
        try
        {
            _voice ??= Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!);
            const int SVSFlagsAsync = 1, SVSFPurgeBeforeSpeak = 2;
            _voice!.Speak(text, SVSFlagsAsync | SVSFPurgeBeforeSpeak);
        }
        catch
        {
            // No speech engine: stay silent.
        }
    }
}
