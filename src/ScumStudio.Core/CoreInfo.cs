namespace ScumStudio.Core;

/// <summary>Identification constants for the ScumStudio.Core assembly.</summary>
public static class CoreInfo
{
    /// <summary>Assembly display name.</summary>
    public const string Name = "ScumStudio.Core";

    /// <summary>Product name shown in UIs, logs and the CLI.</summary>
    public const string ProductName = "SCUM Modding Studio";

    /// <summary>Unreal Engine version of the SCUM cooked content this tool targets.</summary>
    public const string TargetEngineVersion = "4.27.2";

    /// <summary>Informational version of the running build (from the assembly attributes).</summary>
    public static string Version { get; } =
        typeof(CoreInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(CoreInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
