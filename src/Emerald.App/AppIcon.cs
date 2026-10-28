using System.Windows;
using System.Windows.Media.Imaging;

namespace Emerald.App;

/// <summary>
/// Emerald's icon, for window title bars and the taskbar.
///
/// <b>Why this exists at all.</b> A WPF window that sets no <see cref="Window.Icon"/> falls back
/// to the icon embedded in the executable, which is set from the same artwork — so in principle
/// nothing here is needed. In practice that is a framework behaviour nobody can see failing
/// until a window comes up with the wrong picture, and setting it explicitly costs one line at
/// each window. The windows in the other modules are created from here, so they are given it on
/// the way out.
///
/// Loaded once and frozen. A <see cref="BitmapFrame"/> is shared across threads only when
/// frozen, and re-decoding a 130 KB multi-size icon for every window would be work for nothing.
/// </summary>
internal static class AppIcon
{
    private static BitmapFrame? _image;

    /// <summary>
    /// The icon, or null if it could not be loaded.
    ///
    /// Null rather than throwing: a missing icon is a cosmetic fault and must not be the reason
    /// a capture deck will not open. A window given null simply keeps the executable's own.
    /// </summary>
    internal static BitmapFrame? Image
    {
        get
        {
            if (_image is not null) return _image;

            try
            {
                var uri = new Uri("pack://application:,,,/emerald.ico");

                // The decoder hands back every size in the file; WPF then picks the one it wants
                // per use, which is what makes a 16-pixel taskbar icon sharp rather than a
                // squashed 256.
                BitmapDecoder decoder = BitmapDecoder.Create(
                    uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                _image = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;

                _image?.Freeze();
            }
            catch (Exception)
            {
                _image = null;
            }

            return _image;
        }
    }

    /// <summary>Gives a window Emerald's icon, if there is one to give.</summary>
    internal static void ApplyTo(Window window)
    {
        if (Image is { } icon) window.Icon = icon;
    }
}
