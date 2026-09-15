namespace MechrevoLite.Diagnostics;

/// <summary>
/// 打包调度的测试缝：导出必须在线程池上跑，绝不占用 UI 线程（否则大日志会让界面卡死）。
/// 单测直接对这个缝断言「工作不在调用线程上执行」。
/// </summary>
internal static class DiagnosticPackRunner
{
    internal static Task<DiagnosticPackResult> ExportAsync(string zipPath, DiagnosticPackInputs inputs) =>
        RunOffCallingThread(() => DiagnosticPackExporter.Export(zipPath, inputs));

    internal static Task<T> RunOffCallingThread<T>(Func<T> work) =>
        Task.Run(work);
}
