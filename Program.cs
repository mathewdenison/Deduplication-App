#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing; // Requires: dotnet add package System.IO.Hashing
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    class Program
    {
        // --- CONFIGURATION ---
        static string SourceBase;
        static string ArchiveBase;
        static string SuspectBase;
        static readonly string CsvPath = @"C:\Temp\dedupe_master_report.csv";
        static readonly string SuspectCsvPath = @"C:\Temp\suspect_mismatch_report.csv";
        static readonly string LogPath = @"C:\Temp\dedupe_master_log.txt";
        static readonly string CachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hash_cache.json");
        
        static readonly int SystemCores = Environment.ProcessorCount;
        static readonly long MaxSizeBytes = 2147483648; // 2 GB Cap
        static readonly long MB10 = 10 * 1024 * 1024;
        static readonly long GB1 = 1024L * 1024 * 1024;

        static readonly object _logLock = new object();
        static readonly Regex CopyRegex = new Regex(@"\s*\(\d+\)$|\s+-\s+Copy$|\(\d+\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Tracking for End-of-Run Analytics
        static long _totalBytesHashed = 0;
        static int _filesHashedSinceLastSave = 0;
        static ConcurrentDictionary<string, CacheEntry> _hashCache = new ConcurrentDictionary<string, CacheEntry>();

        static void Main(string[] args)
        {
            Console.WriteLine("=== NAS Deduplicator Configuration ===");
            SourceBase = RequestPath("Enter SOURCE path (e.g. \\\\192.168.1.50\\Clients): ");
            ArchiveBase = RequestPath("Enter ARCHIVE/DUPES path (e.g. \\\\192.168.1.50\\Dupes): ");
            SuspectBase = RequestPath("Enter SUSPECT path (e.g. \\\\192.168.1.50\\Assumed_Copies): ");

            AppDomain.CurrentDomain.ProcessExit += (s, e) => SaveCache();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; SaveCache(); Environment.Exit(0); };

            Stopwatch totalTimer = Stopwatch.StartNew();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                LoadCache();
                WriteLog("=======================================================", ConsoleColor.Cyan);
            WriteLog("  STARTING C# DEDUPLICATION: ENTERPRISE TIER (TWO-STAGE)", ConsoleColor.Cyan);
            WriteLog("=======================================================", ConsoleColor.Cyan);

            // Re-verify after logging start
            if (!Directory.Exists(ToLongPath(SourceBase)))
            {
                WriteLog($"CRITICAL ERROR: Source path not found ({SourceBase}). Halting.", ConsoleColor.Red);
                return;
            }

            // ==========================================
            // PHASE 1A: FAST INDEXING & SIZE SHORT-CIRCUIT
            // ==========================================
            WriteLog("PHASE 1A: Deep Scanning & Size Short-Circuiting...", ConsoleColor.Yellow);
            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
            
            var allFiles = new List<FileTask>();
            int filesDiscovered = 0;

            foreach (var f in Directory.EnumerateFiles(SourceBase, "*.*", enumOptions))
            {
                try
                {
                    var fi = new FileInfo(f);
                    string baseName = CopyRegex.Replace(Path.GetFileNameWithoutExtension(fi.Name), "") + fi.Extension;
                    allFiles.Add(new FileTask { File = fi, BaseName = baseName });

                    filesDiscovered++;
                    if (filesDiscovered % 50000 == 0) WriteLog($"Scanning... Discovered {filesDiscovered:N0} files so far.", ConsoleColor.DarkGray);
                }
                catch { }
            }

            var baseNameGroups = allFiles.GroupBy(x => x.BaseName).Where(g => g.Count() > 1).ToList();
            var successfulHashes = new ConcurrentBag<FileRecord>();
            var filesToHash = new List<FileTask>();

            int skippedBySize = 0;

            foreach (var group in baseNameGroups)
            {
                var sizeGroups = group.GroupBy(f => f.File.Length).ToList();
                foreach (var sg in sizeGroups)
                {
                    if (sg.Count() == 1) 
                    {
                        var uniqueFile = sg.First();
                        successfulHashes.Add(new FileRecord
                        {
                            File = uniqueFile.File, BaseName = uniqueFile.BaseName,
                            Hash = "SIZE_MISMATCH_" + uniqueFile.File.Length, 
                            LastWriteTime = uniqueFile.File.LastWriteTime, PathLength = uniqueFile.File.FullName.Length
                        });
                        skippedBySize++;
                    }
                    else { filesToHash.AddRange(sg); }
                }
            }

            WriteLog($"Identified {baseNameGroups.Sum(g => g.Count())} total grouped files.", ConsoleColor.Gray);
            WriteLog($"Short-Circuited {skippedBySize} files based on unique byte sizes.", ConsoleColor.Green);

            // ==========================================
            // PHASE 1B: THE QUICK HASH (1MB SIP)
            // ==========================================
            var smallFiles = filesToHash.Where(f => f.File.Length <= MB10).ToList();
            var medLargeFiles = filesToHash.Where(f => f.File.Length > MB10).ToList();
            var filesForFullHash = new List<FileTask>(smallFiles); // Small files skip quick hash
            var lockedQueue = new ConcurrentBag<FileTask>();

            if (medLargeFiles.Any())
            {
                WriteLog($"PHASE 1B: Performing 'Quick Hash' (1MB Sip) on {medLargeFiles.Count} Medium/Large files...", ConsoleColor.Yellow);
                int quickHashSkipped = 0;

                Parallel.ForEach(medLargeFiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(SystemCores * 4, 32) }, task =>
                {
                    if (task.File.Length > MaxSizeBytes) return;
                    try
                    {
                        if (_hashCache.TryGetValue(task.File.FullName, out var entry) && 
                            entry.Size == task.File.Length && 
                            entry.LastWriteTime == task.File.LastWriteTime && 
                            !string.IsNullOrEmpty(entry.QuickHash))
                        {
                            task.QuickHash = entry.QuickHash;
                        }
                        else
                        {
                            task.QuickHash = CalculateQuickHash(task.File.FullName);
                            var newEntry = entry ?? new CacheEntry { Size = task.File.Length, LastWriteTime = task.File.LastWriteTime };
                            newEntry.QuickHash = task.QuickHash;
                            _hashCache[task.File.FullName] = newEntry;
                            Interlocked.Add(ref _totalBytesHashed, Math.Min(task.File.Length, 1048576));

                            if (Interlocked.Increment(ref _filesHashedSinceLastSave) >= 1000)
                            {
                                Interlocked.Exchange(ref _filesHashedSinceLastSave, 0);
                                SaveCache();
                            }
                        }
                    }
                    catch (IOException) { lockedQueue.Add(task); }
                    catch (Exception ex) { WriteLog($"ERROR reading {task.File.FullName}: {ex.Message}", ConsoleColor.Red); }
                });

                // Triage Quick Hash Results
                var qhGroups = medLargeFiles.Where(f => f.QuickHash != null).GroupBy(f => new { f.BaseName, f.File.Length });
                foreach (var group in qhGroups)
                {
                    var hashSubGroups = group.GroupBy(f => f.QuickHash).ToList();
                    foreach (var hsg in hashSubGroups)
                    {
                        if (hsg.Count() == 1) // Unique Quick Hash!
                        {
                            var uniqueFile = hsg.First();
                            successfulHashes.Add(new FileRecord
                            {
                                File = uniqueFile.File, BaseName = uniqueFile.BaseName,
                                Hash = "QUICK_MISMATCH_" + uniqueFile.QuickHash,
                                LastWriteTime = uniqueFile.File.LastWriteTime, PathLength = uniqueFile.File.FullName.Length
                            });
                            quickHashSkipped++;
                        }
                        else { filesForFullHash.AddRange(hsg); } // Requires Full Hash
                    }
                }
                WriteLog($"Quick Hash complete. Eliminated {quickHashSkipped} modified large files instantly!", ConsoleColor.Green);
            }

            // ==========================================
            // PHASE 1C: TIERED MASS HASHING (WITH WATCHDOG)
            // ==========================================
            WriteLog($"PHASE 1C: Tiered Full Hashing Started ({filesForFullHash.Count} files remaining)...", ConsoleColor.Yellow);

            var activeHashes = new ConcurrentDictionary<string, (DateTime StartTime, long SizeBytes)>();
            var watchdogToken = new CancellationTokenSource();

            var watchdogTask = Task.Run(async () =>
            {
                while (!watchdogToken.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(15000, watchdogToken.Token); } catch { break; }
                    var now = DateTime.UtcNow;
                    foreach (var kvp in activeHashes)
                    {
                        var elapsed = now - kvp.Value.StartTime;
                        if (elapsed.TotalSeconds > 45)
                        {
                            double sizeMB = kvp.Value.SizeBytes / 1048576.0;
                            WriteLog($"[WATCHDOG] Hashing for {elapsed.TotalSeconds:F0}s -> {Path.GetFileName(kvp.Key)} ({sizeMB:F1} MB).", ConsoleColor.Magenta);
                        }
                    }
                }
            });

            var tierSmall = filesForFullHash.Where(f => f.File.Length <= MB10).OrderBy(f => f.File.Length).ToList();
            var tierSampling = filesForFullHash.Where(f => f.File.Length > MB10 && f.File.Length < GB1).OrderBy(f => f.File.Length).ToList();
            var tierFullLarge = filesForFullHash.Where(f => f.File.Length >= GB1).OrderBy(f => f.File.Length).ToList();

            int processedCount = 0;
            int totalToHash = filesForFullHash.Count;

            void ProcessTier(List<FileTask> tierQueue, int maxThreads, string tierName)
            {
                if (!tierQueue.Any()) return;
                WriteLog($"--- Starting {tierName} Tier ({tierQueue.Count} files) with {maxThreads} Threads ---", ConsoleColor.DarkCyan);

                Parallel.ForEach(tierQueue, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, task =>
                {
                    if (task.File.Length > MaxSizeBytes) return;

                    try
                    {
                        activeHashes.TryAdd(task.File.FullName, (DateTime.UtcNow, task.File.Length));
                        string calculatedHash;
                        long bytesRead = 0;
                        try 
                        {
                            if (_hashCache.TryGetValue(task.File.FullName, out var entry) && 
                                entry.Size == task.File.Length && 
                                entry.LastWriteTime == task.File.LastWriteTime && 
                                !string.IsNullOrEmpty(entry.TargetHash))
                            {
                                calculatedHash = entry.TargetHash;
                            }
                            else
                            {
                                calculatedHash = GetTargetHash(task, out bytesRead);
                                var newEntry = entry ?? new CacheEntry { Size = task.File.Length, LastWriteTime = task.File.LastWriteTime };
                                newEntry.TargetHash = calculatedHash;
                                _hashCache[task.File.FullName] = newEntry;

                                if (Interlocked.Increment(ref _filesHashedSinceLastSave) >= 1000)
                                {
                                    Interlocked.Exchange(ref _filesHashedSinceLastSave, 0);
                                    SaveCache();
                                }
                            }
                        }
                        finally { activeHashes.TryRemove(task.File.FullName, out _); }

                        successfulHashes.Add(new FileRecord
                        {
                            File = task.File, BaseName = task.BaseName, Hash = calculatedHash,
                            LastWriteTime = task.File.LastWriteTime, PathLength = task.File.FullName.Length
                        });

                        if (bytesRead > 0) Interlocked.Add(ref _totalBytesHashed, bytesRead);
                    }
                    catch (IOException) { lockedQueue.Add(task); }
                    catch (Exception ex) { WriteLog($"ERROR reading {task.File.FullName}: {ex.Message}", ConsoleColor.Red); }

                    int current = Interlocked.Increment(ref processedCount);
                    if (current % (Math.Max(1, totalToHash / 10)) == 0 || current == totalToHash)
                    {
                        int activeT = Process.GetCurrentProcess().Threads.Count;
                        WriteLog($"Hashing: {current}/{totalToHash} | Active Threads: ~{activeT}", ConsoleColor.DarkGray);
                    }
                });
            }

            ProcessTier(tierSmall, Math.Min(SystemCores * 4, 32), "SMALL (< 10MB)");
            ProcessTier(tierSampling, Math.Min(SystemCores * 2, 16), "SAMPLING (10MB - 1GB)");
            ProcessTier(tierFullLarge, Math.Min(SystemCores, 4), "LARGE FULL (>= 1GB)");

            watchdogToken.Cancel(); // Stop watchdog

            // ==========================================
            // PHASE 1D: THE DELAYED RETRY
            // ==========================================
            if (lockedQueue.Any())
            {
                WriteLog($"PHASE 1D: Retrying {lockedQueue.Count} previously locked files...", ConsoleColor.Yellow);
                Parallel.ForEach(lockedQueue, new ParallelOptions { MaxDegreeOfParallelism = 4 }, task =>
                {
                    try
                    {
                        string calculatedHash;
                        long bytesRead = 0;

                        if (_hashCache.TryGetValue(task.File.FullName, out var entry) && 
                            entry.Size == task.File.Length && 
                            entry.LastWriteTime == task.File.LastWriteTime && 
                            !string.IsNullOrEmpty(entry.TargetHash))
                        {
                            calculatedHash = entry.TargetHash;
                        }
                        else
                        {
                            calculatedHash = GetTargetHash(task, out bytesRead);
                            var newEntry = entry ?? new CacheEntry { Size = task.File.Length, LastWriteTime = task.File.LastWriteTime };
                            newEntry.TargetHash = calculatedHash;
                            _hashCache[task.File.FullName] = newEntry;
                        }

                        successfulHashes.Add(new FileRecord
                        {
                            File = task.File, BaseName = task.BaseName, Hash = calculatedHash,
                            LastWriteTime = task.File.LastWriteTime, PathLength = task.File.FullName.Length
                        });
                        if (bytesRead > 0) Interlocked.Add(ref _totalBytesHashed, bytesRead);
                        WriteLog($"RETRY SUCCESS: {task.File.Name}", ConsoleColor.Green);
                    }
                    catch { WriteLog($"PERMANENTLY LOCKED: {task.File.FullName}", ConsoleColor.DarkGray); }
                });
            }

            // ==========================================
            // PHASE 1E: DECISION LOGIC (TRIAGE)
            // ==========================================
            WriteLog("PHASE 1E: Analyzing hashed and short-circuited files...", ConsoleColor.Yellow);
            
            var identicalReport = new ConcurrentBag<ActionRecord>();
            var suspectReport = new ConcurrentBag<ActionRecord>();

            var finalGroups = successfulHashes.GroupBy(f => f.BaseName).Where(g => g.Count() > 1).ToList();

            foreach (var group in finalGroups)
            {
                var hashGroups = group.GroupBy(f => f.Hash).ToList();
                var identicalGroups = hashGroups.Where(g => g.Count() > 1).ToList();
                var moveIdenticalPaths = new HashSet<string>();

                foreach (var hGroup in identicalGroups)
                {
                    var sortedClones = hGroup.OrderBy(f => f.PathLength).ToList();
                    var keepFile = sortedClones.First();

                    identicalReport.Add(new ActionRecord(keepFile.File, "KEEP", "Identical Clones - Shortest Path", keepFile.LastWriteTime));

                    foreach (var moveFile in sortedClones.Skip(1))
                    {
                        identicalReport.Add(new ActionRecord(moveFile.File, "MOVE_IDENTICAL", "Identical Clone - Deeper Path", moveFile.LastWriteTime));
                        moveIdenticalPaths.Add(moveFile.File.FullName);
                    }
                }

                var remainingFiles = group.Where(f => !moveIdenticalPaths.Contains(f.File.FullName)).ToList();
                var dirGroups = remainingFiles.GroupBy(f => f.File.DirectoryName).ToList();

                foreach (var dGroup in dirGroups)
                {
                    if (dGroup.Count() > 1)
                    {
                        var sortedByDate = dGroup.OrderByDescending(f => f.LastWriteTime).ToList();
                        var masterKeep = sortedByDate.First();

                        foreach (var suspectFile in sortedByDate.Skip(1))
                        {
                            suspectReport.Add(new ActionRecord(suspectFile.File, "MOVE_SUSPECT", "Same Directory Name Match + Hash Mismatch", suspectFile.LastWriteTime, masterKeep.LastWriteTime));
                        }
                    }
                }
            }

            ExportCsv(CsvPath, identicalReport);
            ExportCsv(SuspectCsvPath, suspectReport);
            WriteLog("PHASE 1 COMPLETE. CSVs generated.", ConsoleColor.Green);

            // ==========================================
            // PHASE 2 & 3: SAFE MOVES
            // ==========================================
            WriteLog("PHASE 2: Archiving Perfect Clones...", ConsoleColor.Yellow);
            var identicalMoves = identicalReport.Where(r => r.Action == "MOVE_IDENTICAL").ToList();
            if (identicalMoves.Any())
            {
                var resultIdentical = SafeMove(identicalMoves, ArchiveBase, SourceBase, false);
                WriteLog($"Processed Clones: Successfully moved {resultIdentical.success}. Failed: {resultIdentical.fail}.", ConsoleColor.Green);
            }
            else { WriteLog("No identical duplicates to move.", ConsoleColor.Green); }

            WriteLog("PHASE 3: Isolating Suspect/Modified Copies...", ConsoleColor.Yellow);
            var suspectMoves = suspectReport.Where(r => r.Action == "MOVE_SUSPECT").ToList();
            if (suspectMoves.Any())
            {
                var resultSuspect = SafeMove(suspectMoves, SuspectBase, SourceBase, true);
                WriteLog($"Processed Suspects: Successfully isolated {resultSuspect.success}. Failed: {resultSuspect.fail}.", ConsoleColor.Green);
            }
            else { WriteLog("No suspect mismatch copies found.", ConsoleColor.Green); }

            // ==========================================
            // PHASE 4: EMPTY FOLDER SWEEP
            // ==========================================
            WriteLog("PHASE 4: Empty Folder Sweep Started...", ConsoleColor.Yellow);
            var allFolders = Directory.EnumerateDirectories(SourceBase, "*", enumOptions).OrderByDescending(d => d.Length).ToList();
            foreach (var folder in allFolders)
            {
                try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false); }
                catch { }
            }

            SaveCache();
            totalTimer.Stop();

            // ==========================================
            // PERFORMANCE ANALYTICS
            // ==========================================
            WriteLog("=======================================================", ConsoleColor.Cyan);
            WriteLog("  ALL PHASES COMPLETE. MASTER WORKFLOW FINISHED.", ConsoleColor.Cyan);
            WriteLog("=======================================================", ConsoleColor.Cyan);
            
            double gigabytesHashed = _totalBytesHashed / 1024.0 / 1024.0 / 1024.0;
            double minutesElapsed = totalTimer.Elapsed.TotalMinutes;
            double megabytesPerSecond = (_totalBytesHashed / 1024.0 / 1024.0) / (totalTimer.Elapsed.TotalSeconds > 0 ? totalTimer.Elapsed.TotalSeconds : 1);

            WriteLog($"Total Time Elapsed:   {minutesElapsed:F2} Minutes", ConsoleColor.Green);
            WriteLog($"Data Pushed (Hashing):{gigabytesHashed:F2} GB", ConsoleColor.Green);
            if (totalTimer.Elapsed.TotalSeconds > 0) WriteLog($"Average Hash Speed:   {megabytesPerSecond:F2} MB/s", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                WriteLog($"CRITICAL RUNTIME ERROR: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                SaveCache();
            }
        }

        // --- HELPER METHODS ---

        static (int success, int fail) SafeMove(List<ActionRecord> list, string targetBase, string sourceRoot, bool writeAuditTrail = false)
        {
            int totalSuccess = 0, totalFail = 0;

            string longSourceRoot = ToLongPath(sourceRoot);
            string longTargetBase = ToLongPath(targetBase);

            if (!Directory.Exists(longSourceRoot))
            {
                WriteLog($"CRITICAL: Source root {sourceRoot} is not accessible. Aborting moves.", ConsoleColor.Red);
                return (0, list.Count);
            }

            // Grouping by Folder guarantees that no two threads touch the same directory index simultaneously
            var folderGroups = list.GroupBy(r => r.Directory).ToList();

            Parallel.ForEach(folderGroups, new ParallelOptions { MaxDegreeOfParallelism = 64 }, folderGroup =>
            {
                string firstItemPath = folderGroup.First().Path;
                string relativeDir = Path.GetDirectoryName(firstItemPath).Replace(sourceRoot, "", StringComparison.OrdinalIgnoreCase).TrimStart('\\');
                string longDestDir = Path.Combine(longTargetBase, relativeDir);

                try 
                { 
                    Directory.CreateDirectory(longDestDir); 
                }
                catch (Exception ex)
                {
                    WriteLog($"CRITICAL: Could not create directory {longDestDir}: {ex.Message}", ConsoleColor.Red);
                    Interlocked.Add(ref totalFail, folderGroup.Count());
                    return;
                }

                foreach (var item in folderGroup)
                {
                    int retries = 3;
                    bool moved = false;
                    string longSourceFile = ToLongPath(item.Path);
                    string destFileName = Path.GetFileName(item.Path);
                    string longDestFile = Path.Combine(longDestDir, destFileName);

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
                                    } catch { }
                                }
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
                                WriteLog($" NETWORK BLIP: {item.Path}. Retrying in 5s... ({retries} left)", ConsoleColor.Magenta);
                                Thread.Sleep(5000);
                            }
                            else
                            {
                                WriteLog($" PERMANENT NETWORK FAILURE: {item.Path} - {ex.Message}", ConsoleColor.Red);
                                Interlocked.Increment(ref totalFail);
                            }
                        }
                        catch (Exception ex)
                        {
                            WriteLog($" FAILED MOVE: {item.Path} - {ex.Message}", ConsoleColor.Red);
                            Interlocked.Increment(ref totalFail);
                            break; 
                        }
                    }
                }
            });

            return (totalSuccess, totalFail);
        }

        static string ToLongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\")) return path; // Already prefixed

            if (path.StartsWith(@"\\")) // UNC Path
            {
                return @"\\?\UNC\" + path.Substring(2);
            }
            
            return @"\\?\" + path; // Local Path
        }

        static bool IsNetworkError(Exception ex)
        {
            // 0x80070040 = The network name cannot be found.
            // 0x80070035 = The network path was not found.
            // 0x8007003B = An unexpected network error occurred.
            // 0x80070079 = The semaphore timeout period has expired.
            int hr = ex.HResult & 0xFFFF;
            return hr == 64 || hr == 53 || hr == 59 || hr == 121;
        }

        static string CalculateQuickHash(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                try
                {
                    // Read only the first 1 Megabyte
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

        static string CalculateFullHash(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
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

        static string CalculateSamplingHash(string filePath, out long bytesReadTotal)
        {
            bytesReadTotal = 0;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess))
            {
                var fi = new FileInfo(filePath);
                long length = fi.Length;
                var hasher = new XxHash64();
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                try
                {
                    // Sample 1: Start (1MB)
                    int bytesRead = stream.Read(buffer, 0, 1048576);
                    if (bytesRead > 0)
                    {
                        hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                        bytesReadTotal += bytesRead;
                    }

                    if (length > 3145728) // If > 3MB
                    {
                        // Sample 2: Middle (1MB)
                        long middlePos = length / 2 - 524288;
                        stream.Seek(middlePos, SeekOrigin.Begin);
                        bytesRead = stream.Read(buffer, 0, 1048576);
                        if (bytesRead > 0)
                        {
                            hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            bytesReadTotal += bytesRead;
                        }

                        // Sample 3: End (1MB)
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

        static string GetTargetHash(FileTask task, out long bytesRead)
        {
            if (task.File.Length > MB10 && task.File.Length < GB1)
            {
                return CalculateSamplingHash(task.File.FullName, out bytesRead);
            }
            else
            {
                bytesRead = task.File.Length;
                return CalculateFullHash(task.File.FullName);
            }
        }

        static void WriteLog(string message, ConsoleColor color = ConsoleColor.White)
        {
            lock (_logLock)
            {
                Console.ForegroundColor = color;
                string formattedMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                Console.WriteLine(formattedMsg);
                Console.ResetColor();
                File.AppendAllText(LogPath, formattedMsg + Environment.NewLine);
            }
        }

        static void ExportCsv(string path, IEnumerable<ActionRecord> records)
        {
            var lines = new List<string> { "FileName,Directory,LastWriteTime,MasterLatestTime,Action,Reason,Path" };
            lines.AddRange(records.OrderBy(r => r.Path).Select(r => 
                $"\"{r.FileName}\",\"{r.Directory}\",\"{r.LastWriteTime}\",\"{r.MasterLatestTime}\",\"{r.Action}\",\"{r.Reason}\",\"{r.Path}\""));
            File.WriteAllLines(path, lines);
        }

        static void LoadCache()
        {
            if (File.Exists(CachePath))
            {
                try
                {
                    string json = File.ReadAllText(CachePath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                    if (data != null) _hashCache = new ConcurrentDictionary<string, CacheEntry>(data);
                    WriteLog($"Loaded {_hashCache.Count} entries from hash cache.", ConsoleColor.DarkGray);
                }
                catch (Exception ex) { WriteLog($"Failed to load hash cache: {ex.Message}", ConsoleColor.Red); }
            }
        }

        static void SaveCache()
        {
            try
            {
                string json = JsonSerializer.Serialize(_hashCache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(CachePath, json);
                WriteLog($"Saved {_hashCache.Count} entries to hash cache.", ConsoleColor.DarkGray);
            }
            catch (Exception ex) { WriteLog($"Failed to save hash cache: {ex.Message}", ConsoleColor.Red); }
        }

        static string RequestPath(string prompt)
        {
            while (true)
            {
                Console.Write(prompt);
                string input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input)) continue;

                // Handle Long Path prefix for the check
                if (Directory.Exists(ToLongPath(input)))
                {
                    return input;
                }
                
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Path '{input}' is not accessible or does not exist.");
                Console.ResetColor();
            }
        }
    }

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

    public static class DedupeEngine
    {
        public static string ToLongPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\")) return path; // Already prefixed

            if (path.StartsWith(@"\\")) // UNC Path
            {
                return @"\\?\UNC\" + path.Substring(2);
            }
            
            return @"\\?\" + path; // Local Path
        }

        public static bool IsNetworkError(Exception ex)
        {
            // 0x80070040 = The network name cannot be found.
            // 0x80070035 = The network path was not found.
            // 0x8007003B = An unexpected network error occurred.
            // 0x80070079 = The semaphore timeout period has expired.
            int hr = ex.HResult & 0xFFFF;
            return hr == 64 || hr == 53 || hr == 59 || hr == 121;
        }

        public static string CalculateQuickHash(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                try
                {
                    // Read only the first 1 Megabyte
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
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan))
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
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess))
            {
                var fi = new FileInfo(filePath);
                long length = fi.Length;
                var hasher = new XxHash64();
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1048576);
                try
                {
                    // Sample 1: Start (1MB)
                    int bytesRead = stream.Read(buffer, 0, 1048576);
                    if (bytesRead > 0)
                    {
                        hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                        bytesReadTotal += bytesRead;
                    }

                    if (length > 3145728) // If > 3MB
                    {
                        // Sample 2: Middle (1MB)
                        long middlePos = length / 2 - 524288;
                        stream.Seek(middlePos, SeekOrigin.Begin);
                        bytesRead = stream.Read(buffer, 0, 1048576);
                        if (bytesRead > 0)
                        {
                            hasher.Append(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                            bytesReadTotal += bytesRead;
                        }

                        // Sample 3: End (1MB)
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

    class ActionRecord
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
            FileName = file.Name; Directory = file.DirectoryName; Action = action; Reason = reason; Path = file.FullName;
            LastWriteTime = writeTime.ToString("yyyy-MM-dd HH:mm:ss");
            MasterLatestTime = masterTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        }
    }

    class CacheEntry
    {
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public string QuickHash { get; set; }
        public string TargetHash { get; set; }
    }
}