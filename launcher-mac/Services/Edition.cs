namespace DustoreLauncherV.Mac.Services;

/// <summary>Free or Prime, fixed at build time (-p:DustoreEdition=Prime).</summary>
public static class Edition
{
    // The Prime download is its own edition. It remains available offline and never
    // changes edition in response to a website session or a local activation file.
#if PRIME
    public const bool IsPrimeBuild = true;
#else
    public const bool IsPrimeBuild = false;
#endif
    public static bool IsPrime => IsPrimeBuild;
    public static string Name => IsPrime ? "Prime" : "Free";

    /// <summary>Free converts at a capped pace; Prime has no limit.</summary>
    public const long FreeExBytesPerSecond = 2L * 1024 * 1024;
    /// <summary>Free waits this long before each eX transfer; Prime starts at once.</summary>
    public const int FreeQueueSeconds = 15;
}
