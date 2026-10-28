using System.Reflection;

namespace Emerald.Core;

/// <summary>
/// Which build of Emerald this is, from the version stamped on the assemblies at build time
/// (Directory.Build.props at the root of the repository), so an operator can say which one a
/// plant is running.
/// </summary>
public static class EmeraldVersion
{
    /// <summary>The version alone: "1.0.0".</summary>
    public static string Number { get; } = Read();

    /// <summary>As shown on screen: "v1.0.0".</summary>
    public static string Text => $"v{Number}";

    private static string Read()
    {
        Assembly assembly = typeof(EmeraldVersion).Assembly;

        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // A "+commit" suffix, where a build adds one, is for the build log rather than the screen.
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
