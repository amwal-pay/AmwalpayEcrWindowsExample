namespace AmwalECR.Example.Core.Data;

/// <summary>
/// How an ECR reaches the POS terminal — TMS <c>ecrMode</c> values:
/// <c>1</c> USB cable, <c>2</c> Wi‑Fi, <c>3</c> Bluetooth, <c>4</c> Web Service.
/// </summary>
public enum EcrMode
{
    UsbCable = 1,
    Wifi = 2,
    Bluetooth = 3,
    WebService = 4,
}

public static class EcrModes
{
    public static EcrMode Default => EcrMode.Wifi;

    public static IReadOnlyList<EcrMode> All { get; } =
        new[] { EcrMode.UsbCable, EcrMode.Wifi, EcrMode.Bluetooth, EcrMode.WebService };

    public static int Value(this EcrMode mode) => (int)mode;

    public static string Label(this EcrMode mode) => mode switch
    {
        EcrMode.UsbCable => "USB Cable",
        EcrMode.Wifi => "Wi‑Fi",
        EcrMode.Bluetooth => "Bluetooth",
        EcrMode.WebService => "Web Service",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Whether the terminal is reached over a socket. Wi‑Fi alone: a USB cable
    /// carries no IP.
    /// </summary>
    public static bool IsIpBased(this EcrMode mode) => mode == EcrMode.Wifi;

    public static bool IsUsbCable(this EcrMode mode) => mode == EcrMode.UsbCable;

    /// <summary>
    /// Whether this sample can run transactions over the mode.
    /// <para>
    /// Narrower than the Android sample, which also drives a USB cable: the
    /// Windows SDK ships no USB channel (it carries no native USB dependency on
    /// purpose), and Bluetooth was never wired in either sample. Both stay
    /// registrable so a terminal list can be kept in step with the Android one.
    /// </para>
    /// </summary>
    public static bool IsSupportedInExample(this EcrMode mode) =>
        mode == EcrMode.Wifi || mode == EcrMode.WebService;

    public static EcrMode? FromValue(int value) =>
        Enum.IsDefined(typeof(EcrMode), value) ? (EcrMode)value : null;
}
