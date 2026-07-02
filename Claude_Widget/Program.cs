using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Claude_Widget.Services;

namespace Claude_Widget
{
    /// <summary>
    /// Real entry point. The headless hook/notify modes finish here without ever
    /// touching WPF, so Claude Code firing <c>ClaudeWidget.exe --hook</c> per tool
    /// call only pays the .NET runtime start cost — not the full WPF load.
    /// UI creation is isolated in <see cref="RunUi"/> (non-inlined) so JIT-ing Main
    /// never resolves PresentationFramework in the headless path.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("--hook", StringComparer.OrdinalIgnoreCase))
            {
                CliHookService.HandleHookFromStdin();
                return 0;
            }

            int notifyIdx = Array.FindIndex(args, a => a.Equals("--notify", StringComparison.OrdinalIgnoreCase));
            if (notifyIdx >= 0)
            {
                if (notifyIdx + 1 < args.Length)
                    CliHookService.WriteText(args[notifyIdx + 1]);
                return 0;
            }

            return RunUi();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunUi()
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
