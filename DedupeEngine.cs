using System;
using System.IO;
using System.IO.Hashing;

namespace NASDeduplicator
{
    public static class DedupeEngine
    {
        public static string CalculateQuickHash(string filePath)
        {
            string longPath = FileSystemHandler.ToLongPath(filePath);
            using (var stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                try
                {
                    int bytesRead = stream.Read(buffer, 0, 1048576);
                    if (bytesRead == 0) return "EMPTY";
                    var hasher = new XxHash64();
                    hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                    return BitConverter.ToString(hasher.GetCurrentHash()).Replace("-", "").ToUpperInvariant();
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        public static string CalculateFullHash(string filePath)
        {
            string longPath = FileSystemHandler.ToLongPath(filePath);
            using (var stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
            {
                var hasher = new XxHash64();
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                try
                {
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
                return BitConverter.ToString(hasher.GetCurrentHash()).Replace("-", "").ToUpperInvariant();
            }
        }

        public static string CalculateSamplingHash(string filePath, out long bytesReadTotal)
        {
            bytesReadTotal = 0;
            string longPath = FileSystemHandler.ToLongPath(filePath);
            using (var stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess))
            {
                var fi = new FileInfo(longPath);
                long length = fi.Length;
                var hasher = new XxHash64();
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                try
                {
                    int bytesRead = stream.Read(buffer, 0, 1048576);
                    if (bytesRead > 0)
                    {
                        hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                        bytesReadTotal += bytesRead;
                    }
                    if (length > 3145728)
                    {
                        long middlePos = length / 2 - 524288;
                        stream.Seek(middlePos, SeekOrigin.Begin);
                        bytesRead = stream.Read(buffer, 0, 1048576);
                        if (bytesRead > 0)
                        {
                            hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            bytesReadTotal += bytesRead;
                        }
                        long endPos = length - 1048576;
                        stream.Seek(endPos, SeekOrigin.Begin);
                        bytesRead = stream.Read(buffer, 0, 1048576);
                        if (bytesRead > 0)
                        {
                            hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            bytesReadTotal += bytesRead;
                        }
                    }
                    return "SAMPLED_" + BitConverter.ToString(hasher.GetCurrentHash()).Replace("-", "").ToUpperInvariant();
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        public static string GetTargetHash(FileTask task, long mb10, long gb1, out long bytesRead)
        {
            if (task.File.Length > mb10 && task.File.Length < gb1)
            {
                return CalculateSamplingHash(task.File.FullName, out bytesRead);
            }
            else
            {
                bytesRead = task.File.Length;
                return CalculateFullHash(task.File.FullName);
            }
        }
    }
}
