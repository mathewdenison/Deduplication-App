using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NASDeduplicator
{
    public static class NetworkShareManager
    {
        public static async Task<(bool success, string message)> MountShare(string path, string username, string password, Action<string, ConsoleColor, object?> logAction)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            
            try
            {
                if (isWindows)
                {
                    return await MountWindows(path, username, password, logAction);
                }
                else
                {
                    return await MountLinux(path, username, password, logAction);
                }
            }
            catch (Exception ex)
            {
                return (false, $"Critical Error during mounting: {ex.Message}");
            }
        }

        private static async Task<(bool success, string message)> MountWindows(string path, string username, string password, Action<string, ConsoleColor, object?> logAction)
        {
            logAction($"[NETWORK] Attempting to connect to Windows share: {path}", ConsoleColor.Cyan, null);
            
            // net use \\Server\Share password /user:username
            string args = $"use \"{path}\" {password} /user:{username}";
            var (exitCode, output, error) = await RunProcess("net.exe", args);

            if (exitCode == 0)
            {
                logAction($"[NETWORK] Successfully authenticated to {path}", ConsoleColor.Green, null);
                return (true, "Connected successfully.");
            }
            
            return (false, $"Windows mount failed: {error} {output}");
        }

        private static async Task<(bool success, string message)> MountLinux(string path, string username, string password, Action<string, ConsoleColor, object?> logAction)
        {
            logAction($"[NETWORK] Attempting to mount Linux CIFS share: {path} to /mnt/remote_nas", ConsoleColor.Cyan, null);
            
            // 1. Ensure mount point exists
            await RunProcess("mkdir", "-p /mnt/remote_nas");

            // 2. Unmount if already mounted
            await RunProcess("umount", "-l /mnt/remote_nas");

            // 3. Mount
            // mount -t cifs //Server/Share /mnt/remote_nas -o username=u,password=p,vers=3.0,sec=ntlmssp
            string linuxPath = path.Replace("\\", "/");
            if (!linuxPath.StartsWith("//")) linuxPath = "//" + linuxPath.TrimStart('/');

            string args = $"-t cifs \"{linuxPath}\" \"/mnt/remote_nas\" -o username={username},password={password},vers=3.0,sec=ntlmssp,file_mode=0777,dir_mode=0777";
            var (exitCode, output, error) = await RunProcess("mount", args);

            if (exitCode == 0)
            {
                logAction($"[NETWORK] Successfully mounted {path} to /mnt/remote_nas", ConsoleColor.Green, null);
                return (true, "/mnt/remote_nas"); // Return the local mount point
            }

            return (false, $"Linux mount failed: {error} {output}. Ensure container is running in --privileged mode.");
        }

        private static Task<(int exitCode, string stdout, string stderr)> RunProcess(string fileName, string arguments)
        {
            var tcs = new TaskCompletionSource<(int, string, string)>();
            
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            
            process.Exited += (s, e) =>
            {
                string outStr = process.StandardOutput.ReadToEnd();
                string errStr = process.StandardError.ReadToEnd();
                tcs.SetResult((process.ExitCode, outStr, errStr));
                process.Dispose();
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                tcs.SetResult((-1, "", ex.Message));
            }

            return tcs.Task;
        }
    }
}
