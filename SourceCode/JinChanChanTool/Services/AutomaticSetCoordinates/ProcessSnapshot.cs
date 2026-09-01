namespace JinChanChanTool.Services.AutoSetCoordinates
{
    /// <summary>
    /// 进程发现阶段生成的轻量快照。
    /// 仅保存窗口绑定需要的数据，避免在 UI 和服务之间长期持有 Process 对象。
    /// </summary>
    public sealed class ProcessSnapshot
    {
        public int Id { get; }
        public string ProcessName { get; }
        public nint MainWindowHandle { get; }
        public string MainWindowTitle { get; }
        public string? ExecutablePath { get; }

        public ProcessSnapshot(
            int id,
            string processName,
            nint mainWindowHandle,
            string mainWindowTitle,
            string? executablePath)
        {
            Id = id;
            ProcessName = processName;
            MainWindowHandle = mainWindowHandle;
            MainWindowTitle = mainWindowTitle;
            ExecutablePath = executablePath;
        }
    }
}
