using System;
using DiskCleaner.Helpers;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 回收站管理服务 — 使用 Windows Shell API 查询和清空回收站
    /// </summary>
    public class RecycleBinManager
    {
        public Action<int, string> OnProgress { get; set; }

        /// <summary>查询回收站信息</summary>
        public RecycleBinInfo Query(string drive = null)
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
            };
            int result = NativeMethods.SHQueryRecycleBin(drive, ref info);

            if (result == 0)
            {
                return new RecycleBinInfo
                {
                    SizeBytes = info.i64Size,
                    ItemCount = info.i64NumItems,
                    IsEmpty = info.i64NumItems == 0
                };
            }
            return new RecycleBinInfo { SizeBytes = 0, ItemCount = 0, IsEmpty = true };
        }

        /// <summary>清空回收站</summary>
        public bool Empty(string drive = null, bool silent = true)
        {
            OnProgress?.Invoke(0, "正在清空回收站...");
            uint flags = NativeMethods.SHERB_NOCONFIRMATION;
            if (silent) flags |= NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;

            int result = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, drive, flags);
            OnProgress?.Invoke(100, result == 0 ? "回收站已清空" : $"清空失败 (错误码: {result})");
            return result == 0;
        }

        /// <summary>清空所有盘的回收站</summary>
        public bool EmptyAll(bool silent = true)
        {
            OnProgress?.Invoke(0, "正在清空所有回收站...");
            uint flags = NativeMethods.SHERB_NOCONFIRMATION;
            if (silent) flags |= NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;

            int result = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, flags);
            OnProgress?.Invoke(100, result == 0 ? "回收站已清空" : $"清空失败 (错误码: {result})");
            return result == 0;
        }
    }

    public class RecycleBinInfo
    {
        public long SizeBytes { get; set; }
        public long ItemCount { get; set; }
        public bool IsEmpty { get; set; }
        public string SizeDisplay => FileSizeFormatter.Format(SizeBytes);
        public string ItemDisplay => ItemCount == 0 ? "空" : $"{ItemCount} 项";
    }
}
