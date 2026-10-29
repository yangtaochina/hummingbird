namespace Hummingbird.App;

/// <summary>
/// 程序入口。除了标准 WinForms 启动，还挂了三层全局异常兜底：
/// 任何角落抛出的异常都会进日志文件（logs\）并弹窗提示，而不是无声闪退——
/// 现场出问题时打开当天的 log 就能看到"程序死在哪一步、为什么"。
/// </summary>
internal static class Program
{
    /// <summary>全局日志器：主窗体创建之前就能用（启动阶段的异常也要记）。</summary>
    private static readonly Core.IAppLogger Log = new Core.MemoryLogger();

    [STAThread]
    private static void Main()
    {
        // --- 全局异常兜底（三层） ---
        // 1) UI 线程异常（按钮点击等事件处理器里没 try/catch 的）
        System.Windows.Forms.Application.ThreadException += (_, e) =>
            Fatal("界面线程异常", e.Exception);
        // 2) 后台线程异常（Task/线程池里未观察到的）
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("后台任务异常：" + e.Exception.InnerExceptions[0].Message);
            e.SetObserved(); // 不让进程崩
        };
        // 3) 兜底的兜底：以上都没接住的
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal("未处理异常", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));

        Log.Info("程序启动");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm(Log));

        Log.Info("程序退出");
    }

    /// <summary>致命异常统一处理：写日志（含堆栈）+ 弹窗，程序继续运行（不闪退）。</summary>
    private static void Fatal(string title, Exception ex)
    {
        try
        {
            Log.Error($"{title}：{ex.Message}{Environment.NewLine}堆栈：{ex.StackTrace}");
        }
        catch { /* 日志本身失败就没办法了 */ }

        try
        {
            MessageBox.Show(
                $"{title}：{ex.Message}\r\n\r\n详细信息已写入日志文件（程序目录\\logs\\）。\r\n\r\n{ex.StackTrace}",
                "蜂鸟上位机", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* 无界面上下文时弹窗也会失败，忽略 */ }
    }
}
