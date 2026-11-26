using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Free.DBTools
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AssemblyRedirects.Install();

            if (e.Args != null && e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                var exitCode = SelfTest.Run();
                Shutdown(exitCode);
                return;
            }

            base.OnStartup(e);

            ThemeManager.Apply(ThemeManager.LoadSaved());
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                "程序遇到未处理的异常：\n\n" + Core.DatabaseSession.DescribeException(e.Exception),
                "Free.DBTools",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
