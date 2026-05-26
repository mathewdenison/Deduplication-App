using System;
using System.IO;

namespace NASDeduplicator
{
    public class FileTask
    {
        public FileInfo File { get; set; }
        public string BaseName { get; set; }
        public string QuickHash { get; set; }
    }

    public class FileRecord
    {
        public FileInfo File { get; set; }
        public string BaseName { get; set; }
        public string Hash { get; set; }
        public DateTime LastWriteTime { get; set; }
        public int PathLength { get; set; }
    }

    public class ActionRecord
    {
        public string FileName { get; }
        public string Directory { get; }
        public string LastWriteTime { get; }
        public string MasterLatestTime { get; }
        public string Action { get; }
        public string Reason { get; }
        public string Path { get; }

        public ActionRecord(FileInfo file, string action, string reason, DateTime writeTime, DateTime? masterTime = null)
        {
            FileName = file.Name;
            Directory = file.DirectoryName;
            Action = action;
            Reason = reason;
            Path = file.FullName;
            LastWriteTime = writeTime.ToString("yyyy-MM-dd HH:mm:ss");
            MasterLatestTime = masterTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        }
    }

    public class CacheEntry
    {
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public string QuickHash { get; set; }
        public string TargetHash { get; set; }
    }
}
