using System.Reflection;

namespace HeadphoneControl;

internal static class AppVersion
{
    // <Version> in HeadphoneControl.csproj. The SDK appends "+<commit>" to the informational version in a git
    // checkout; the commit is not part of the version people see.
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? throw new InvalidOperationException("The app assembly has no informational version.");
}
