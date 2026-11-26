using System;
using System.IO;
using System.Reflection;

namespace Free.DBTools
{
    internal static class AssemblyRedirects
    {
        private static readonly object SyncRoot = new object();
        private static bool _installed;

        public static void Install()
        {
            lock (SyncRoot)
            {
                if (_installed)
                {
                    return;
                }
                _installed = true;
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name).Name;
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string fileName = null;

                if (string.Equals(requested, "DmProvider", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = "DM.DmProvider.dll";
                }
                else if (string.Equals(requested, "Kdbndp", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = "Kdbndp.dll";
                }

                if (fileName == null)
                {
                    return null;
                }

                var path = Path.Combine(baseDirectory, fileName);
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
