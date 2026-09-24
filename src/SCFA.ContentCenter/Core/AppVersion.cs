using System.Reflection;

namespace SCFA.ContentCenter.Core;

public static class AppVersion
{
    public static string Informational { get; } = ReadInformationalVersion();
    public static string Display => "V" + Informational;

    private static string ReadInformationalVersion()
    {
        var value = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(value)) return "4.0.0";
        return value.Split('+', 2)[0];
    }
}
