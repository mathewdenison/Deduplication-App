using Xunit;
using System.IO;
using System.Text;
using NASDeduplicator;

namespace NASdeduplication.Tests
{
    public class DedupeEngineTests
    {
        [Theory]
        [InlineData(@"C:\Temp\file.txt", @"\\?\C:\Temp\file.txt")]
        [InlineData(@"\\Pwlaw-nas\Clients\file.txt", @"\\?\UNC\Pwlaw-nas\Clients\file.txt")]
        public void ToLongPath_ConvertsCorrectly(string input, string expected)
        {
            var result = FileSystemHandler.ToLongPath(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void IsNetworkError_IdentifiesKnownErrors()
        {
            var ex = new IOException("Network name not found", unchecked((int)0x80070040));
            Assert.True(FileSystemHandler.IsNetworkError(ex));

            var normalEx = new IOException("File not found", unchecked((int)0x80070002));
            Assert.False(FileSystemHandler.IsNetworkError(normalEx));
        }

        [Fact]
        public void CalculateFullHash_ReturnsConsistentHash()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                string content = "Hello World Deduplication Test Content";
                File.WriteAllText(tempFile, content);

                string hash1 = DedupeEngine.CalculateFullHash(tempFile);
                string hash2 = DedupeEngine.CalculateFullHash(tempFile);

                Assert.Equal(hash1, hash2);
                Assert.False(string.IsNullOrEmpty(hash1));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void CalculateSamplingHash_ReadsOnlyParts()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                // Create a 10MB file
                byte[] data = new byte[10 * 1024 * 1024];
                new Random().NextBytes(data);
                File.WriteAllBytes(tempFile, data);

                string hash = DedupeEngine.CalculateSamplingHash(tempFile, out long bytesRead);

                // Should read roughly 3MB (start, middle, end)
                Assert.Equal(3145728, bytesRead);
                Assert.StartsWith("SAMPLED_", hash);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void DeepRecursion_CollapsesCorrectly()
        {
            string root = Path.Combine(Path.GetTempPath(), "DedupeTest_" + Guid.NewGuid());
            try
            {
                // 1. Setup 8-layer deep structure
                string currentPath = root;
                Directory.CreateDirectory(root);
                for (int i = 0; i < 8; i++)
                {
                    currentPath = Path.Combine(currentPath, "Layer" + i);
                    Directory.CreateDirectory(currentPath);
                    
                    // Add a clone in every layer
                    File.WriteAllText(Path.Combine(currentPath, "clone.txt"), "IDENTICAL_CONTENT");
                    // Add some junk
                    File.WriteAllText(Path.Combine(currentPath, "Thumbs.db"), "JUNK");
                }

                // 2. Add a master at the top
                File.WriteAllText(Path.Combine(root, "master.txt"), "IDENTICAL_CONTENT");

                // 3. Verify folders exist (checking a deep path)
                string deepPath = Path.Combine(root, "Layer0", "Layer1", "Layer2", "Layer3", "Layer4", "Layer5", "Layer6", "Layer7");
                Assert.True(Directory.Exists(deepPath));

                // 4. Simulate the Cleanup Logic (The user's reported problem)
                // In a real run, clones are moved/deleted first.
                var allFiles = Directory.EnumerateFiles(root, "clone.txt", SearchOption.AllDirectories).ToList();
                foreach (var f in allFiles) File.Delete(f);

                // Now run the Phase 4 logic
                var allFolders = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                    .OrderByDescending(d => d.Length).ToList();

                foreach (var folder in allFolders)
                {
                    if (IsEffectivelyEmptyLocal(folder))
                    {
                        try { Directory.Delete(folder, true); } catch { }
                    }
                }

                // 5. Final Assertion: All layers should be gone, root should only have master.txt
                var remainingDirs = Directory.GetDirectories(root);
                Assert.Empty(remainingDirs);
                Assert.True(File.Exists(Path.Combine(root, "master.txt")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private bool IsEffectivelyEmptyLocal(string path)
        {
            var entries = Directory.EnumerateFileSystemEntries(path).ToList();
            if (!entries.Any()) return true;
            string[] junk = { "thumbs.db", ".ds_store", "desktop.ini" };
            return entries.All(e => junk.Contains(Path.GetFileName(e).ToLowerInvariant()));
        }

        [Fact]
        public void IntensiveMultiDirectory_StressTest()
        {
            string testBase = Path.Combine(Path.GetTempPath(), "DedupeStress_" + Guid.NewGuid());
            string source = Path.Combine(testBase, "Source");
            string archive = Path.Combine(testBase, "Archive");
            string suspect = Path.Combine(testBase, "Suspect");

            try
            {
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(archive);
                Directory.CreateDirectory(suspect);

                // 1. Create 5 distinct root folders (e.g. Clients A-E)
                string[] roots = { "ClientA", "ClientB", "ClientC", "ClientD", "ClientE" };
                int totalClonesCreated = 0;
                int totalUniqueCreated = 0;

                foreach (var r in roots)
                {
                    string currentPath = Path.Combine(source, r);
                    Directory.CreateDirectory(currentPath);

                    // Create an 8-layer deep recursive structure for EACH root
                    for (int layer = 0; layer < 8; layer++)
                    {
                        currentPath = Path.Combine(currentPath, "Layer" + layer);
                        Directory.CreateDirectory(currentPath);

                        // Add 20 unique files per layer
                        for (int f = 0; f < 20; f++)
                        {
                            File.WriteAllText(Path.Combine(currentPath, $"unique_{f}.txt"), $"Unique Content {r} {layer} {f}");
                            totalUniqueCreated++;
                        }

                        // Add 20 cloned files per layer (cloning a master at source root)
                        string masterContent = "GLOBAL_CLONE_CONTENT";
                        File.WriteAllText(Path.Combine(source, "global_master.txt"), masterContent);
                        for (int f = 0; f < 20; f++)
                        {
                            File.WriteAllText(Path.Combine(currentPath, $"clone_{f}.txt"), masterContent);
                            totalClonesCreated++;
                        }
                        
                        // Add junk
                        File.WriteAllText(Path.Combine(currentPath, "Thumbs.db"), "JUNK");
                    }
                }

                // 2. Initialize the real Service (using a Mock Hub Context if possible, or just null for this test)
                // Since we refactored to be modular, we can test the workflow.
                // For this test, we'll manually invoke the logic parts to verify the "Move + Cleanup" specifically.
                
                // We need to simulate the "SuccessfulHashes" result from Phase 1
                var successfulHashes = new List<FileRecord>();
                var allFiles = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                                .Select(f => new FileInfo(f)).ToList();

                // Mock Hashing
                var masterFile = new FileInfo(Path.Combine(source, "global_master.txt"));
                string masterHash = "HASH_A";

                foreach(var fi in allFiles)
                {
                    string content = File.ReadAllText(fi.FullName);
                    string hash = content == "GLOBAL_CLONE_CONTENT" ? masterHash : "HASH_UNIQUE_" + Guid.NewGuid();
                    successfulHashes.Add(new FileRecord { File = fi, BaseName = fi.Name, Hash = hash, LastWriteTime = fi.LastWriteTime, PathLength = fi.FullName.Length });
                }

                // 3. Run the Decision & Move Logic (Same as DedupeService Phase 1E / 2 / 3)
                var identicalReport = new List<ActionRecord>();
                var hashGroups = successfulHashes.GroupBy(f => f.Hash).Where(g => g.Count() > 1).ToList();
                var movePaths = new HashSet<string>();

                foreach (var hGroup in hashGroups)
                {
                    var sorted = hGroup.OrderBy(f => f.PathLength).ToList();
                    foreach (var m in sorted.Skip(1)) 
                    { 
                        identicalReport.Add(new ActionRecord(m.File, "MOVE_IDENTICAL", "Clone", m.LastWriteTime)); 
                        movePaths.Add(m.File.FullName);
                    }
                }

                // Execute Moves
                var moveResult = FileSystemHandler.SafeMove(identicalReport, archive, source, false, (m, c, o) => { });

                // Execute Cleanup
                var allFolders = Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                                    .OrderByDescending(d => d.Length).ToList();
                foreach (var folder in allFolders)
                {
                    if (IsEffectivelyEmptyLocal(folder))
                    {
                        try { Directory.Delete(folder, true); } catch { }
                    }
                }

                // 4. VERIFICATION
                // A. All clones should be in Archive
                int archivedCount = Directory.EnumerateFiles(archive, "clone_*", SearchOption.AllDirectories).Count();
                Assert.Equal(totalClonesCreated, archivedCount);

                // B. Archive structure should match Source
                // (Picking one deep clone to check)
                string expectedArchiveFile = Path.Combine(archive, "ClientA", "Layer0", "Layer1", "Layer2", "Layer3", "Layer4", "Layer5", "Layer6", "Layer7", "clone_19.txt");
                Assert.True(File.Exists(expectedArchiveFile), "Deep archive file missing!");

                // C. Source should have NO clones left
                int sourceClonesLeft = Directory.EnumerateFiles(source, "clone_*", SearchOption.AllDirectories).Count();
                Assert.Equal(0, sourceClonesLeft);

                // D. Source should have ALL unique files left
                int sourceUniqueLeft = Directory.EnumerateFiles(source, "unique_*", SearchOption.AllDirectories).Count();
                Assert.Equal(totalUniqueCreated, sourceUniqueLeft);

                // E. Source should have NO empty recursive folders left
                // Only the root Client folders should remain if they contain unique files
                var remainingDirs = Directory.GetDirectories(source);
                Assert.Equal(5, remainingDirs.Length); // ClientA-E
            }
            finally
            {
                if (Directory.Exists(testBase)) Directory.Delete(testBase, true);
            }
        }
    }
}