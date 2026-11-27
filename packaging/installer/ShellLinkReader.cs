using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class PolishShellLinkReader
{
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder target, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
    }

    public static string[] Read(string file)
    {
        var cls = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true);
        var instance = Activator.CreateInstance(cls);
        try
        {
            ((IPersistFile)instance).Load(file, 0); // STGM_READ; never resolve or run the link.
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            var directory = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            link.GetWorkingDirectory(directory, directory.Capacity);
            link.GetArguments(arguments, arguments.Capacity);
            return new[] { target.ToString(), directory.ToString(), arguments.ToString() };
        }
        finally { Marshal.ReleaseComObject(instance); }
    }
}
