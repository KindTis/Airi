using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Airi
{
    class Util
    {
        static public FrameworkElement FindByName(string name, FrameworkElement root)
        {
            Stack<FrameworkElement> tree = new Stack<FrameworkElement>();
            tree.Push(root);

            while (tree.Count > 0)
            {
                FrameworkElement current = tree.Pop();
                if (current.Name == name)
                    return current;

                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; ++i)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(current, i);
                    if (child is FrameworkElement)
                        tree.Push((FrameworkElement)child);
                }
            }

            return null;
        }

        /// Possible flags for the SHFileOperation method.
        [Flags]
        public enum FileOperationFlags : ushort
        {
            /// Do not show a dialog during the process
            FOF_SILENT = 0x0004,
            /// Do not ask the user to confirm selection
            FOF_NOCONFIRMATION = 0x0010,
            /// Delete the file to the recycle bin.  (Required flag to send a file to the bin
            FOF_ALLOWUNDO = 0x0040,
            /// Do not show the names of the files or folders that are being recycled.
            FOF_SIMPLEPROGRESS = 0x0100,
            /// Surpress errors, if any occur during the process.
            FOF_NOERRORUI = 0x0400,
            /// Warn if files are too big to fit in the recycle bin and will need
            /// to be deleted completely.
            FOF_WANTNUKEWARNING = 0x4000,
        }

        /// File Operation Function Type for SHFileOperation
        public enum FileOperationType : uint
        {
            /// Move the objects
            FO_MOVE = 0x0001,
            /// Copy the objects
            FO_COPY = 0x0002,
            /// Delete (or recycle) the objects
            FO_DELETE = 0x0003,
            /// Rename the object(s)
            FO_RENAME = 0x0004,
        }

        /// SHFILEOPSTRUCT for SHFileOperation from COM
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEOPSTRUCT
        {

            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.U4)]
            public FileOperationType wFunc;
            public string pFrom;
            public string pTo;
            public FileOperationFlags fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        public static bool Send(string path, FileOperationFlags flags)
        {
            try
            {
                var fs = new SHFILEOPSTRUCT
                {
                    wFunc = FileOperationType.FO_DELETE,
                    pFrom = path + '\0' + '\0',
                    fFlags = FileOperationFlags.FOF_ALLOWUNDO | flags
                };
                SHFileOperation(ref fs);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// Send file to recycle bin.  Display dialog, display warning if files are too big to fit (FOF_WANTNUKEWARNING)
        public static bool Send(string path)
        {
            return Send(path, FileOperationFlags.FOF_NOCONFIRMATION | FileOperationFlags.FOF_WANTNUKEWARNING);
        }

        /// Send file silently to recycle bin.  Surpress dialog, surpress errors, delete if too large.
        public static bool MoveToRecycleBin(string path)
        {
            return Send(path, FileOperationFlags.FOF_NOCONFIRMATION | FileOperationFlags.FOF_NOERRORUI | FileOperationFlags.FOF_SILENT);

        }
    }
}