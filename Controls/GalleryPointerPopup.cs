using System.ComponentModel;
using Airi.Infrastructure;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows;

namespace Airi.Controls;

/// <summary>A layered WPF popup whose native window passes pointer input to the cards below.</summary>
public sealed class GalleryPointerPopup : Popup
{
    private const int ExtendedStyle = -20;
    private const long Transparent = 0x20, NoActivate = 0x08000000;
    protected override void OnOpened(EventArgs e)
    {
        if (Child is not null && PresentationSource.FromVisual(Child) is HwndSource source)
        {
            var style = GetWindowLongPtr(source.Handle, ExtendedStyle);
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLongPtr(source.Handle, ExtendedStyle, new nint(style.ToInt64() | Transparent | NoActivate)) == 0 && Marshal.GetLastWin32Error() != 0)
            {
                AppLogger.Error("Gallery tooltip pointer transparency failed.", new Win32Exception(Marshal.GetLastWin32Error()));
                IsOpen = false;
                return;
            }
        }
        base.OnOpened(e);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
}
