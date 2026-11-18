using System;
using System.Windows.Forms;

namespace Emerald.Setup;

/// <summary>
/// Emerald-Setup.exe (or Emerald-EDL-Setup.exe): the published Emerald folder in one file, with
/// a window that asks where things go and then runs that folder's own install.bat with the
/// answers. The install itself - files, settings, shortcuts, firewall, uninstall entry, and the
/// DELTACAST driver when the build carries it - is install.bat's, exactly as when it is run by
/// hand, so there is one install and not two that drift apart.
///
/// Built by publish.bat: this program, then the zipped folder appended to it by pack-setup.ps1.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!Environment.Is64BitOperatingSystem)
        {
            MessageBox.Show("Emerald needs 64-bit Windows.", "Emerald setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        Payload payload;

        try
        {
            payload = Payload.Open(Application.ExecutablePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"This setup file is damaged and cannot be read:\n\n{ex.Message}\n\n" +
                            "Copy it again from where it was built.", "Emerald setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (payload is null)
        {
            MessageBox.Show("This is the setup program on its own, without Emerald inside it.\n\n" +
                            "Run publish.bat to build the complete Emerald-Setup.exe.", "Emerald setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using (payload)
        {
            var form = new SetupForm(payload);
            Application.Run(form);
            return form.Succeeded ? 0 : 1;
        }
    }
}
