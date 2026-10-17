using System;
using System.Threading;
using System.Windows.Forms;

namespace War3FixFont
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(true, "War3FixFont", out var mutexCreated);
            if (mutexCreated)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Main());
            }
            else
            {
                // 已有后台实例时直接结束，避免重复修复和弹出提示。
                return;
            }
        }
    }
}
