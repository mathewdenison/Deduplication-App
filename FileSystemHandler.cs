using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public static class FileSystemHandler
    {
        public static string ToLongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (!OperatingSystem.IsWindows()) return path;
            if (path.StartsWith(@"\\?\")) return path;

            if (path.StartsWith(@"\\"))
            {
                return @"\\?\UNC\" + path.Substring(2);
            }
            return @"\\?\" + path;
        }

        public static string ToShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\")) return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\")) return path.Substring(4);
            return path;
        }

        public static string GetLogicalPath(string fullPath, string sourceRoot)
        {
            string shortPath = ToShortPath(fullPath);
            string shortRoot = ToShortPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootFolderName = Path.GetFileName(shortRoot);
            
            string relativePath = Path.GetRelativePath(shortRoot, shortPath);
            if (relativePath == "." || relativePath == "..") return relativePath;

            var segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var normalizedSegments = new List<string>();

            foreach (var segment in segments)
            {
                // Skip segment if it's the same as the previous segment (adjacent duplicate)
                if (normalizedSegments.Count > 0 && string.Equals(segment, normalizedSegments.Last(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                // Also skip if it's the very first segment and it matches the root folder name
                // This handles the case where "Root/" contains "Root/Root/"
                if (normalizedSegments.Count == 0 && string.Equals(segment, rootFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                normalizedSegments.Add(segment);
            }

            return Path.Combine(normalizedSegments.ToArray());
        }

        public static bool IsNetworkError(Exception ex)
        {
            if (OperatingSystem.IsWindows())
            {
                int hr = ex.HResult & 0xFFFF;
                if (hr == 64 || hr == 53 || hr == 59 || hr == 121) return true;
            }

            string msg = ex.Message.ToLowerInvariant();
            return msg.Contains("network name cannot be found") ||
                   msg.Contains("network path was not found") ||
                   msg.Contains("connection reset") ||
                   msg.Contains("broken pipe") ||
                   msg.Contains("timed out");
        }

        public static (int success, int fail) SafeMove(List<ActionRecord> list, string targetBase, string sourceRoot, bool writeAuditTrail, Action<string, ConsoleColor, object> logAction)
        {
            int totalSuccess = 0, totalFail = 0;
            
            // Normalize inputs
            string cleanSourceRoot = ToShortPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string cleanTargetBase = ToShortPath(targetBase).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string longSourceRoot = ToLongPath(cleanSourceRoot);
            if (!Directory.Exists(longSourceRoot))
            {
                logAction($"CRITICAL: Source root {cleanSourceRoot} is not accessible. Aborting moves.", ConsoleColor.Red, null);
                return (0, list.Count);
            }

            var folderGroups = list.GroupBy(r => r.Directory).ToList();

            Parallel.ForEach(folderGroups, new ParallelOptions { MaxDegreeOfParallelism = 64 }, folderGroup =>
            {
                string firstItemPath = ToShortPath(folderGroup.First().Path);
                
                // Calculate relative directory using "clean" paths
                string itemDir = Path.GetDirectoryName(firstItemPath) ?? "";
                string relativeDir = Path.GetRelativePath(cleanSourceRoot, itemDir);
                if (relativeDir == ".") relativeDir = "";
                
                string longDestDir = ToLongPath(Path.Combine(cleanTargetBase, relativeDir));

                try
                {
                    Directory.CreateDirectory(longDestDir);
                }
                catch (Exception ex)
                {
                    logAction($"CRITICAL: Could not create directory {longDestDir}: {ex.Message}", ConsoleColor.Red, null);
                    Interlocked.Add(ref totalFail, folderGroup.Count());
                    return;
                }

                foreach (var item in folderGroup)
                {
                    int retries = 3;
                    bool moved = false;
                    string shortSourceFile = ToShortPath(item.Path);
                    string longSourceFile = ToLongPath(shortSourceFile);
                    
                    string destFileName = Path.GetFileName(shortSourceFile);
                    string longDestFile = Path.Combine(longDestDir, destFileName);

                    // SAFETY: Never delete if source and dest resolved to the same path
                    if (string.Equals(longSourceFile, longDestFile, StringComparison.OrdinalIgnoreCase))
                    {
                        logAction($"SAFETY SKIP: Source and Dest are same path: {shortSourceFile}", ConsoleColor.Yellow, null);
                        Interlocked.Increment(ref totalFail);
                        continue;
                    }

                    while (retries > 0 && !moved)
                    {
                        try
                        {
                            if (!File.Exists(longSourceFile))
                            {
                                moved = true;
                                break;
                            }

                            var sourceInfo = new FileInfo(longSourceFile);
                            bool alreadyCopied = false;

                            if (File.Exists(longDestFile) && new FileInfo(longDestFile).Length == sourceInfo.Length) { alreadyCopied = true; }

                            if (!alreadyCopied)
                            {
                                File.Copy(longSourceFile, longDestFile, true);
                            }

                            if (File.Exists(longDestFile) && new FileInfo(longDestFile).Length == sourceInfo.Length)
                            {
                                File.Delete(longSourceFile);
                                if (writeAuditTrail)
                                {
                                    try
                                    {
                                        string auditFilePath = longDestFile + ".AuditTrail.txt";
                                        string auditContent =
                                            "--- AUTOMATED DEDUPLICATION AUDIT ---\r\n" +
                                            $"Original File Location: {item.Path}\r\n" +
                                            $"Action Taken:           {item.Action}\r\n" +
                                            $"Reason for Isolation:   {item.Reason}\r\n" +
                                            $"--------------------------------------\r\n" +
                                            $"This File Modified:     {item.LastWriteTime}\r\n" +
                                            $"Master File Modified:   {item.MasterLatestTime}\r\n\r\n" +
                                            "NOTE: This file shares a name with an active file, but its contents are different. " +
                                            "Please review to ensure no unique edits are lost before deleting.";
                                        File.WriteAllText(auditFilePath, auditContent);
                                    }
                                    catch { }
                                }

                                TelemetryEngine.SendEvent(new
                                {
                                    @event = "FILE_MOVE_SUCCESS",
                                    fileName = item.FileName,
                                    sourcePath = item.Path,
                                    destPath = longDestFile,
                                    action = item.Action,
                                    reason = item.Reason,
                                    fileSize = sourceInfo.Length,
                                    lastWriteTime = item.LastWriteTime,
                                    masterLatestTime = item.MasterLatestTime
                                });

                                Interlocked.Increment(ref totalSuccess);
                                moved = true;
                            }
                            else { throw new Exception("Byte-size verification failed after copy."); }
                        }
                        catch (IOException ex) when (IsNetworkError(ex))
                        {
                            retries--;
                            if (retries > 0)
                            {
                                logAction($" NETWORK BLIP: {shortSourceFile}. Retrying in 5s... ({retries} left)", ConsoleColor.Magenta, null);
                                Thread.Sleep(5000);
                            }
                            else
                            {
                                logAction($" PERMANENT NETWORK FAILURE: {shortSourceFile} - {ex.Message}", ConsoleColor.Red, null);
                                Interlocked.Increment(ref totalFail);
                            }
                        }
                        catch (Exception ex)
                        {
                            logAction($" FAILED MOVE: {shortSourceFile} - {ex.Message}", ConsoleColor.Red, null);
                            Interlocked.Increment(ref totalFail);
                            break;
                        }
                    }
                }
            });

            return (totalSuccess, totalFail);
        }
    }
}
