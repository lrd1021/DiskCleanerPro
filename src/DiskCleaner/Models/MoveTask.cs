using DiskCleaner.Helpers;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 文件搬家任务
    /// </summary>
    public class MoveTask : ViewModelBase
    {
        public enum MoveStatus
        {
            Pending,
            Moving,
            Completed,
            Failed,
            Skipped
        }

        private MoveStatus _status;
        private int _progress;

        public string FileName { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public long FileSizeBytes { get; set; }
        public bool CreateSymlink { get; set; }

        public MoveStatus Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        public int Progress
        {
            get => _progress;
            set => Set(ref _progress, value);
        }

        public string SizeDisplay => FileSizeFormatter.Format(FileSizeBytes);
        public string StatusText => Status switch
        {
            MoveStatus.Pending => "等待中",
            MoveStatus.Moving => "搬移中...",
            MoveStatus.Completed => "已完成",
            MoveStatus.Failed => "失败",
            MoveStatus.Skipped => "已跳过",
            _ => ""
        };
    }
}
