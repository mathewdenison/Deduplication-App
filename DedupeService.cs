using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public class DedupeService
    {
        private readonly IHubContext<LogHub> _hubContext;
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;

        // Configuration
        public string SourceBase { get; set; } = "";
        public string ArchiveBase { get; set; } = "";
        public string SuspectBase { get; set; } = "";
        
        // Progress/Analytics
        public string CurrentPhase { get; private set; } = "Idle";
        public int FilesDiscovered { get; private set; } = 0;
        private long _totalBytesHashed = 0;
        public long TotalBytesHashed => _totalBytesHashed;
        public int SuccessCount { get; private set; } = 0;
        public int FailCount { get; private set; } = 0;
        public double ElapsedMinutes { get; private set; } = 0;

        private readonly object _logLock = new object();
        private readonly Regex CopyRegex = new Regex(@"\s*\(\d+\)$|\s+-\s+Copy$|\(\d+\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private ConcurrentDictionary<string, CacheEntry>? _hashCache;
        private int _filesHashedSinceLastSave = 0;

        public DedupeService(IHubContext<LogHub>? hubContext = null)
        {
            _hubContext = hubContext;
        }

        public bool IsRunning => _isRunning;

        public async Task StartRunAsync(CancellationToken externalToken = default)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            await RunWorkflow(_cts.Token);
        }

        public void StopRun()
        {
            _cts?.Cancel();
            CurrentPhase = "Stopped";
            _isRunning = false;
        }

        private async Task RunWorkflow(CancellationToken token)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            try
            {
                FilesDiscovered = 0;
                _totalBytesHashed = 0;
                SuccessCount = 0;
                FailCount = 0;

                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                else dataDir = AppDomain.CurrentDomain.BaseDirectory;

                CacheManager.CachePath = Path.Combine(dataDir, "hash_cache.json");
                _hashCache = CacheManager.LoadCache(WriteLog);

                TelemetryEngine.SendEvent(new { @event = "WORKFLOW_START", source = SourceBase, archive = ArchiveBase, suspect = SuspectBase });

                CurrentPhase = "PHASE 1A: Indexing";
                WriteLog("Starting Scan...", ConsoleColor.Cyan);
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Indexing" });

                string longSourceBase = FileSystemHandler.ToLongPath(SourceBase);
                if (!Directory.Exists(longSourceBase))
                {
                    WriteLog($"CRITICAL ERROR: Source path not found ({SourceBase}).", ConsoleColor.Red);
                    return;
                }

                var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
                var allFiles = new List<FileTask>();
                string[] junkFiles = { "thumbs.db", ".ds_store", "desktop.ini" };

                foreach (var f in Directory.EnumerateFiles(longSourceBase, "*.*", enumOptions))
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        var fi = new FileInfo(f);
                        if (junkFiles.Contains(fi.Name.ToLowerInvariant())) continue;

                        string baseName = CopyRegex.Replace(Path.GetFileNameWithoutExtension(fi.Name), "") + fi.Extension;
                        allFiles.Add(new FileTask { File = fi, BaseName = baseName });
                        FilesDiscovered++;
                        if (FilesDiscovered % 10000 == 0) WriteLog($"Scanning... Found {FilesDiscovered:N0} files.", ConsoleColor.DarkGray);
                    }
                    catch { }
                }

                if (token.IsCancellationRequested) return;
                TelemetryEngine.SendEvent(new { @event = "SCAN_COMPLETE", filesDiscovered = FilesDiscovered });

                CurrentPhase = "PHASE 1B: Global Grouping";
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Global Grouping" });
                var sizeGroups = allFiles.GroupBy(f => f.File.Length).Where(g => g.Count() > 1 && g.Key > 0).ToList();
                var filesToHash = new List<FileTask>();
                foreach (var g in sizeGroups) filesToHash.AddRange(g);

                WriteLog($"Grouped {sizeGroups.Count} size buckets ({filesToHash.Count} candidate files).", ConsoleColor.Gray);

                CurrentPhase = "PHASE 1C: Quick Hash";
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Quick Hash" });
                var medLargeFiles = filesToHash.Where(f => f.File.Length > 10485760).ToList();
                var lockedQueue = new ConcurrentBag<FileTask>();

                if (medLargeFiles.Any())
                {
                    WriteLog($"Performing 'Quick Hash' (1MB Sip) on {medLargeFiles.Count} Large files...", ConsoleColor.Yellow);
                    int quickProcessed = 0;
                    Parallel.ForEach(medLargeFiles, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 16 }, task =>
                    {
                        try
                        {
                            string cacheKey = FileSystemHandler.ToShortPath(task.File.FullName);
                            if (_hashCache!.TryGetValue(cacheKey, out var entry) &&
                                entry.Size == task.File.Length &&
                                entry.LastWriteTime == task.File.LastWriteTime &&
                                !string.IsNullOrEmpty(entry.QuickHash))
                            {
                                task.QuickHash = entry.QuickHash;
                            }
                            else
                            {
                                task.QuickHash = DedupeEngine.CalculateQuickHash(task.File.FullName);
                                var newEntry = entry ?? new CacheEntry { Size = task.File.Length, LastWriteTime = task.File.LastWriteTime };
                                newEntry.QuickHash = task.QuickHash;
                                _hashCache[cacheKey] = newEntry;
                                Interlocked.Add(ref _totalBytesHashed, Math.Min(task.File.Length, 1048576));
                                if (Interlocked.Increment(ref _filesHashedSinceLastSave) >= 1000)
                                {
                                    Interlocked.Exchange(ref _filesHashedSinceLastSave, 0);
                                    CacheManager.SaveCache(_hashCache, WriteLog);
                                }
                            }
                        }
                        catch { lockedQueue.Add(task); }
                        
                        int cur = Interlocked.Increment(ref quickProcessed);
                        if (cur % (Math.Max(1, medLargeFiles.Count / 10)) == 0 || cur == medLargeFiles.Count)
                            WriteLog($"Quick Hash: {cur}/{medLargeFiles.Count}", ConsoleColor.DarkGray);
                    });
                }

                CurrentPhase = "PHASE 1D: Tiered Hashing";
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Tiered Hashing" });
                var filesForFullHash = new List<FileTask>();
                var sizeAndQuickGroups = filesToHash.GroupBy(f => new { f.File.Length, f.QuickHash }).ToList();
                var successfulHashes = new ConcurrentBag<FileRecord>();

                foreach (var sg in sizeAndQuickGroups)
                {
                    if (sg.Count() > 1) filesForFullHash.AddRange(sg);
                    else
                    {
                        var f = sg.First();
                        successfulHashes.Add(new FileRecord { File = f.File, BaseName = f.BaseName, Hash = "UNIQUE_" + Guid.NewGuid(), LastWriteTime = f.File.LastWriteTime });
                    }
                }

                var tierSmall = filesForFullHash.Where(f => f.File.Length <= 10485760).OrderBy(f => f.File.Length).ToList();
                var tierSampling = filesForFullHash.Where(f => f.File.Length > 10485760 && f.File.Length < 1073741824).OrderBy(f => f.File.Length).ToList();
                var tierFullLarge = filesForFullHash.Where(f => f.File.Length >= 1073741824).OrderBy(f => f.File.Length).ToList();

                int hashedCount = 0;
                int totalToHash = filesForFullHash.Count;
                long runningBytes = 0;
                void ProcessTier(List<FileTask> tierQueue, int maxThreads, string tierName)
                {
                    if (!tierQueue.Any()) return;
                    WriteLog($"--- Starting {tierName} Tier ({tierQueue.Count} files) with {maxThreads} Threads ---", ConsoleColor.DarkCyan);
                    Parallel.ForEach(tierQueue, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = maxThreads }, task =>
                    {
                        try
                        {
                            string calculatedHash;
                            long bytesRead = 0;
                            string cacheKey = FileSystemHandler.ToShortPath(task.File.FullName);
                            if (_hashCache!.TryGetValue(cacheKey, out var entry) &&
                                entry.Size == task.File.Length &&
                                entry.LastWriteTime == task.File.LastWriteTime &&
                                !string.IsNullOrEmpty(entry.TargetHash))
                            {
                                calculatedHash = entry.TargetHash;
                            }
                            else
                            {
                                calculatedHash = DedupeEngine.GetTargetHash(task, 10485760, 1073741824, out bytesRead);
                                var newEntry = entry ?? new CacheEntry { Size = task.File.Length, LastWriteTime = task.File.LastWriteTime };
                                newEntry.TargetHash = calculatedHash;
                                _hashCache[cacheKey] = newEntry;
                                if (Interlocked.Increment(ref _filesHashedSinceLastSave) >= 1000)
                                {
                                    Interlocked.Exchange(ref _filesHashedSinceLastSave, 0);
                                    CacheManager.SaveCache(_hashCache, WriteLog);
                                }
                            }
                            successfulHashes.Add(new FileRecord { File = task.File, BaseName = task.BaseName, Hash = calculatedHash, LastWriteTime = task.File.LastWriteTime, PathLength = task.File.FullName.Length });
                            Interlocked.Add(ref runningBytes, bytesRead);
                            _totalBytesHashed = runningBytes;
                        }
                        catch { }

                        int cur = Interlocked.Increment(ref hashedCount);
                        if (cur % (Math.Max(1, totalToHash / 20)) == 0 || cur == totalToHash)
                        {
                            int activeT = Process.GetCurrentProcess().Threads.Count;
                            double pct = (double)cur / totalToHash * 100;
                            WriteLog($"[{tierName}] Progress: {pct:F0}% ({cur}/{totalToHash}) | Active Threads: ~{activeT}", ConsoleColor.DarkGray);
                        }
                    });
                }

                ProcessTier(tierSmall, 32, "SMALL (<10MB)");
                ProcessTier(tierSampling, 16, "SAMPLING (10MB-1GB)");
                ProcessTier(tierFullLarge, 4, "LARGE FULL (>1GB)");

                CurrentPhase = "PHASE 1E: Decision Triage";
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Decision Triage" });
                var identicalReport = new ConcurrentBag<ActionRecord>();
                var suspectReport = new ConcurrentBag<ActionRecord>();
                
                // 1. Group by Logical Identity (Name + Normalized Path)
                var logicalGroups = successfulHashes.GroupBy(f => FileSystemHandler.GetLogicalPath(f.File.FullName, SourceBase)).ToList();
                var moveIdenticalPaths = new HashSet<string>();
                var moveSuspectPaths = new HashSet<string>();

                foreach (var lGroup in logicalGroups)
                {
                    // Winner is the one with the LATEST write time. 
                    // If times are tied, the one with the SHORTEST path wins.
                    var sorted = lGroup.OrderByDescending(f => f.LastWriteTime)
                                       .ThenBy(f => f.File.FullName.Length)
                                       .ToList();
                    
                    var winner = sorted.First();

                    foreach (var loser in sorted.Skip(1))
                    {
                        if (string.Equals(loser.Hash, winner.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            identicalReport.Add(new ActionRecord(loser.File, "MOVE_IDENTICAL", "Nested/Recursive Clone", loser.LastWriteTime));
                            movePathsAdd(loser.File.FullName, moveIdenticalPaths);
                        }
                        else
                        {
                            suspectReport.Add(new ActionRecord(loser.File, "MOVE_SUSPECT", "Nested Version Mismatch (Older Version)", loser.LastWriteTime, winner.LastWriteTime));
                            movePathsAdd(loser.File.FullName, moveSuspectPaths);
                        }
                    }
                }

                // 2. Global Hash Deduplication (for files that aren't logically related but are still identical)
                var remainingForGlobal = successfulHashes
                    .Where(f => !moveIdenticalPaths.Contains(f.File.FullName) && !moveSuspectPaths.Contains(f.File.FullName))
                    .ToList();

                var globalHashGroups = remainingForGlobal.GroupBy(f => f.Hash).Where(g => g.Count() > 1 && !g.Key.StartsWith("UNIQUE")).ToList();

                foreach (var hGroup in globalHashGroups)
                {
                    var sorted = hGroup.OrderBy(f => f.File.FullName.Length).ToList();
                    var master = sorted.First();
                    foreach (var m in sorted.Skip(1)) 
                    { 
                        identicalReport.Add(new ActionRecord(m.File, "MOVE_IDENTICAL", "Global Identical Clone", m.LastWriteTime)); 
                        movePathsAdd(m.File.FullName, moveIdenticalPaths); 
                    }
                }

                CurrentPhase = "PHASE 2/3: Moving Files";
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Moving Files" });
                WriteLog($"Moving {identicalReport.Count(r => r.Action == "MOVE_IDENTICAL")} Clones and {suspectReport.Count} Suspects...", ConsoleColor.Yellow);
                var res1 = FileSystemHandler.SafeMove(identicalReport.Where(r => r.Action == "MOVE_IDENTICAL").ToList(), ArchiveBase, SourceBase, false, WriteLog);
                var res2 = FileSystemHandler.SafeMove(suspectReport.ToList(), SuspectBase, SourceBase, true, WriteLog);
                
                SuccessCount = res1.success + res2.success;
                FailCount = res1.fail + res2.fail;

                CurrentPhase = "PHASE 5: Path Optimization";
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Path Optimization" });
                WriteLog("Identifying files for path promotion...", ConsoleColor.Cyan);
                var remainingSourceFiles = Directory.EnumerateFiles(longSourceBase, "*.*", enumOptions).ToList();
                string[] junkFilesList = { "thumbs.db", ".ds_store", "desktop.ini" };
                
                var promotionTasks = new List<(string current, string target)>();
                foreach (var f in remainingSourceFiles)
                {
                    string fileName = Path.GetFileName(f).ToLowerInvariant();
                    if (junkFilesList.Contains(fileName)) continue;

                    string logicalRelPath = FileSystemHandler.GetLogicalPath(f, SourceBase);
                    string logicalFullPath = Path.Combine(SourceBase, logicalRelPath);
                    string longLogicalPath = FileSystemHandler.ToLongPath(logicalFullPath);

                    if (!string.Equals(FileSystemHandler.ToLongPath(f), longLogicalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(longLogicalPath))
                        {
                            promotionTasks.Add((f, longLogicalPath));
                        }
                    }
                }

                if (promotionTasks.Any())
                {
                    WriteLog($"Found {promotionTasks.Count} files buried in nested folders. Promoting to shortest paths...", ConsoleColor.Yellow);
                    int promoted = 0;
                    foreach (var task in promotionTasks)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(task.target)!);
                            File.Move(task.current, task.target);
                            promoted++;
                            if (promoted % 1000 == 0 || promoted == promotionTasks.Count)
                            {
                                WriteLog($"Promotion Progress: {promoted}/{promotionTasks.Count} files moved.", ConsoleColor.DarkGray);
                            }
                        }
                        catch (Exception ex)
                        {
                            WriteLog($"[PROMOTION ERROR] Failed to move {Path.GetFileName(task.current)}: {ex.Message}", ConsoleColor.Red);
                        }
                    }
                    WriteLog($"Optimization Complete. Promoted {promoted} files to shorter paths.", ConsoleColor.Green);
                }
                else
                {
                    WriteLog("No files required path promotion.", ConsoleColor.Gray);
                }

                CurrentPhase = "PHASE 6: Deep Folder Sweep";
                TelemetryEngine.SendEvent(new { @event = "PHASE_CHANGE", phase = "Deep Folder Sweep" });
                WriteLog("Starting Recursive Folder Cleanup...", ConsoleColor.Cyan);
                
                // Refresh folder list after moves to ensure we see the current state
                var allFolders = Directory.EnumerateDirectories(longSourceBase, "*", enumOptions)
                                    .OrderByDescending(d => d.Length).ToList();
                int deleted = 0;
                int skipped = 0;
                foreach (var folder in allFolders)
                {
                    if (IsEffectivelyEmpty(folder))
                    {
                        try 
                        { 
                            Directory.Delete(FileSystemHandler.ToLongPath(folder), true); 
                            deleted++; 
                        } catch (Exception ex) { 
                            WriteLog($"[CLEANUP] Failed to delete {Path.GetFileName(folder)}: {ex.Message}", ConsoleColor.DarkGray);
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                }
                WriteLog($"Deep Sweep Complete. Removed {deleted} recursive/junk folders. {skipped} folders kept (contained unique data).", ConsoleColor.Green);

                CacheManager.SaveCache(_hashCache!, WriteLog);
                totalTimer.Stop();

                WriteLog("=======================================================", ConsoleColor.Cyan);
                WriteLog("  ALL PHASES COMPLETE. MASTER WORKFLOW FINISHED.", ConsoleColor.Cyan);
                WriteLog("=======================================================", ConsoleColor.Cyan);
                
                double gigabytesHashed = TotalBytesHashed / 1024.0 / 1024.0 / 1024.0;
                double minutesElapsed = totalTimer.Elapsed.TotalMinutes;
                double megabytesPerSecond = (TotalBytesHashed / 1024.0 / 1024.0) / (totalTimer.Elapsed.TotalSeconds > 0 ? totalTimer.Elapsed.TotalSeconds : 1);
                
                WriteLog($"Total Time Elapsed:   {minutesElapsed:F2} Minutes", ConsoleColor.Green);
                WriteLog($"Data Pushed (Hashing):{gigabytesHashed:F2} GB", ConsoleColor.Green);
                if (totalTimer.Elapsed.TotalSeconds > 0) WriteLog($"Average Hash Speed:   {megabytesPerSecond:F2} MB/s", ConsoleColor.Green);
                
                TelemetryEngine.SendEvent(new { 
                    @event = "WORKFLOW_COMPLETE", 
                    totalBytesHashed = TotalBytesHashed, 
                    successCount = SuccessCount, 
                    failCount = FailCount,
                    elapsedMinutes = totalTimer.Elapsed.TotalMinutes,
                    megabytesPerSecond = megabytesPerSecond
                });

                CurrentPhase = "Complete";
            }
            catch (Exception ex)
            {
                WriteLog($"Workflow Error: {ex.Message}", ConsoleColor.Red);
                TelemetryEngine.SendEvent(new { @event = "WORKFLOW_ERROR", message = ex.Message, stackTrace = ex.StackTrace });
                CurrentPhase = "Error";
            }
            finally
            {
                _isRunning = false;
            }
        }

        private void movePathsAdd(string path, HashSet<string> set) { lock(set) set.Add(path); }

        private bool IsEffectivelyEmpty(string path)
        {
            try
            {
                string longPath = FileSystemHandler.ToLongPath(path);
                if (Directory.EnumerateDirectories(longPath).Any()) return false;
                var files = Directory.EnumerateFiles(longPath).ToList();
                if (!files.Any()) return true;
                string[] junk = { "thumbs.db", ".ds_store", "desktop.ini" };
                return files.All(f => junk.Contains(Path.GetFileName(f).ToLowerInvariant()));
            }
            catch { return false; }
        }

        public void WriteLog(string message, ConsoleColor color = ConsoleColor.White, object? metadata = null)
        {
            string formattedMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            
            // Console Output
            Console.ForegroundColor = color;
            Console.WriteLine(formattedMsg);
            Console.ResetColor();

            // File Logging
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dedupe_master_log.txt"), formattedMsg + Environment.NewLine); } catch { }

            // Web Output (SignalR)
            _hubContext?.Clients.All.SendAsync("ReceiveLog", formattedMsg, color.ToString(), metadata);

            // Telemetry Output
            if (!string.IsNullOrEmpty(TelemetryEngine.TelemetryUrl))
            {
                TelemetryEngine.SendEvent(new { message = message, severity = color.ToString(), metadata = metadata });
            }
        }

        public async Task RunSafetyTestAsync()
        {
            if (_isRunning) return;
            
            await Task.Run(async () =>
            {
                _isRunning = true;
                string root = @"C:\Temp\DedupeSafetyTest";
                string source = Path.Combine(root, "Source");
                string archive = Path.Combine(root, "Archive");
                string suspect = Path.Combine(root, "Suspect");

                try
                {
                    WriteLog("--- INITIALIZING SAFETY VERIFICATION TEST ---", ConsoleColor.Cyan);
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    Directory.CreateDirectory(source);
                    Directory.CreateDirectory(archive);
                    Directory.CreateDirectory(suspect);

                    // 1. Create a mess
                    WriteLog("Creating collapsible 8-layer recursive mess...", ConsoleColor.Gray);
                    string oldContent = "I AM OLD";
                    string newContent = "I AM NEW AND UPDATED";
                    
                    // Create an OLD version at the root (shortest path)
                    string rootFile = Path.Combine(source, "master_file.txt");
                    File.WriteAllText(rootFile, oldContent);
                    File.SetLastWriteTime(rootFile, DateTime.Now.AddDays(-10));
                    
                    // Create 12 clones at source root
                    for (int f = 0; f < 12; f++)
                    {
                        string cloneFile = Path.Combine(source, $"clone_file_{f}.txt");
                        File.WriteAllText(cloneFile, oldContent);
                        File.SetLastWriteTime(cloneFile, DateTime.Now.AddDays(-10));
                    }

                    string currentPath = source;
                    string nestedFolderName = "Source";
                    for (int i = 0; i < 8; i++)
                    {
                        currentPath = Path.Combine(currentPath, nestedFolderName);
                        Directory.CreateDirectory(currentPath);
                        for (int f = 0; f < 12; f++) 
                        {
                            // Clones of the OLD version with SAME name across layers
                            string cloneFile = Path.Combine(currentPath, $"clone_file_{f}.txt");
                            File.WriteAllText(cloneFile, oldContent);
                            File.SetLastWriteTime(cloneFile, DateTime.Now.AddDays(-10));
                        }
                        // Add junk in every folder
                        File.WriteAllText(Path.Combine(currentPath, "Thumbs.db"), "Metadata");
                    }

                    // Create a NEW version buried deep (longest path)
                    string deepNewFile = Path.Combine(currentPath, "master_file.txt");
                    File.WriteAllText(deepNewFile, newContent);
                    File.SetLastWriteTime(deepNewFile, DateTime.Now); // Today

                    // 2. Set config to the test folders
                    string oldSrc = SourceBase; string oldArc = ArchiveBase; string oldSus = SuspectBase;
                    SourceBase = source; ArchiveBase = archive; SuspectBase = suspect;

                    // 3. Run Workflow
                    WriteLog("Starting workflow execution against test sandbox...", ConsoleColor.Yellow);
                    await RunWorkflow(CancellationToken.None);

                    // 4. Report results
                    int sourceFiles = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories).Count();
                    int archiveFiles = Directory.EnumerateFiles(archive, "*.*", SearchOption.AllDirectories).Count();
                    int sourceFolders = Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Count();
                    int suspectFiles = Directory.EnumerateFiles(suspect, "*.*", SearchOption.AllDirectories).Count();

                    // Verification of Content Promotion
                    string finalRootContent = File.ReadAllText(rootFile);
                    bool promotedCorrectly = (finalRootContent == newContent);

                    // Expected: 
                    // Uniques: 12
                    // Master Winner: 1
                    // Clone Winners: 12
                    // Total Source: 25
                    // Clones Archived: 12 names * 8 nested versions = 96
                    // Suspects: 1 (the old root master)
                    
                    WriteLog("--- TEST RESULTS ---", ConsoleColor.Cyan);
                    WriteLog($"Files left in Source:  {sourceFiles} (Target: 25)", sourceFiles == 25 ? ConsoleColor.Green : ConsoleColor.Red);
                    WriteLog($"Files in Archive:      {archiveFiles} (Target: 96)", archiveFiles == 96 ? ConsoleColor.Green : ConsoleColor.Red);
                    WriteLog($"Files in Suspect:      {suspectFiles} (Target: 1)", suspectFiles == 1 ? ConsoleColor.Green : ConsoleColor.Red);
                    WriteLog($"Empty Folders Left:    {sourceFolders} (Target: 0)", sourceFolders == 0 ? ConsoleColor.Green : ConsoleColor.Red);
                    WriteLog($"Latest Version Promoted: {promotedCorrectly}", promotedCorrectly ? ConsoleColor.Green : ConsoleColor.Red);
                    
                    if (sourceFolders == 0 && sourceFiles == 25 && archiveFiles == 96 && suspectFiles == 1 && promotedCorrectly) 
                        WriteLog("VERIFICATION PASSED: Application is safe for production use.", ConsoleColor.Green);
                    else
                        WriteLog("VERIFICATION FAILED: Logic or Math mismatch.", ConsoleColor.Red);

                    // Restore original config
                    SourceBase = oldSrc; ArchiveBase = oldArc; SuspectBase = oldSus;
                }
                catch (Exception ex) { WriteLog($"Safety Test Error: {ex.Message}", ConsoleColor.Red); }
                finally { _isRunning = false; }
            });
        }
    }
}
