using System.Diagnostics;

namespace AppNetworkControl;

internal static class StartupManager
{
    private const string TaskName = "应用联网控制_开机自启";

    public static bool IsEnabled()
    {
        try { return Run(["/Query", "/TN", TaskName], throwOnError: false) == 0; }
        catch { return false; }
    }

    public static void Enable()
    {
        string executablePath = Application.ExecutablePath;
        string command = $"\"{executablePath}\" --startup";
        Run(["/Create", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", command, "/F"], throwOnError: true);
    }

    public static void Disable()
    {
        if (!IsEnabled()) return;
        Run(["/Delete", "/TN", TaskName, "/F"], throwOnError: true);
    }

    private static int Run(IEnumerable<string> arguments, bool throwOnError)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Windows 任务计划程序。");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException("Windows 无法保存开机自启设置。\n" + (string.IsNullOrWhiteSpace(error) ? output : error).Trim());
        return process.ExitCode;
    }
}
