using System.Runtime.InteropServices;

internal static class ProcessErrorMode
{
    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    public static void Configure()
    {
        if (OperatingSystem.IsWindows()) _ = SetErrorMode(GetErrorMode() | 0x0001u | 0x0002u);
    }
}
