using System;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace DotaArchive
{
    internal static class PrototypeProgram
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
                MessageBox.Show("操作未完成：" + e.Exception.Message, "Dota Archive", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new PrototypeForm());
        }
    }
}
