// ManageComingSoon - Migration Analyzer
// Pre-flight safety analysis shown to the user BEFORE any filesystem operation.

namespace ManageComingSoon.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using MediaBrowser.Model.Logging;

    public class MigrationAnalysisResult
    {
        public string SourceDirectory      { get; set; } = string.Empty;
        public string DestinationDirectory { get; set; } = string.Empty;
        public int    TotalFileCount       { get; set; }
        public long   TotalBytes          { get; set; }
        public string FriendlySize        { get; set; } = string.Empty;
        public List<string> FilesToMove   { get; set; } = new List<string>();
        public List<string> Warnings      { get; set; } = new List<string>();
        public bool   IsSafeToProceed     { get; set; } = true;
        public string WorstCaseScenarioText { get; set; } = string.Empty;

        /// <summary>Summary suitable for display in a StatusItem or LabelItem.</summary>
        public string ToDisplaySummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat("Source:       {0}", SourceDirectory).AppendLine();
            sb.AppendFormat("Destination:  {0}", DestinationDirectory).AppendLine();
            sb.AppendFormat("Files to move: {0} ({1})", TotalFileCount, FriendlySize).AppendLine();

            if (FilesToMove.Count > 0)
            {
                sb.AppendLine("File list:");
                foreach (var f in FilesToMove)
                    sb.AppendFormat("  • {0}", f).AppendLine();
            }

            if (Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                foreach (var w in Warnings)
                    sb.AppendFormat("  ⚠ {0}", w).AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("Worst case:");
            sb.AppendLine(WorstCaseScenarioText);

            return sb.ToString().TrimEnd();
        }
    }

    public class MigrationAnalyzer
    {
        private readonly ILogger logger;

        public MigrationAnalyzer(ILogger logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// Analyses the scope and risk of moving a Coming Soon movie folder.
        /// Call this before any filesystem operation and surface the result to the user.
        /// </summary>
        public MigrationAnalysisResult Analyze(string sourceFolderPath, string targetLibraryPath)
        {
            string folderName = Path.GetFileName(sourceFolderPath);
            string destinationFolder = Path.Combine(targetLibraryPath, folderName);

            var result = new MigrationAnalysisResult
            {
                SourceDirectory      = sourceFolderPath,
                DestinationDirectory = destinationFolder,
                IsSafeToProceed      = true,
            };

            // --- Structural check ---
            if (!Directory.Exists(sourceFolderPath))
            {
                result.Warnings.Add("Source folder does not exist: " + sourceFolderPath);
                result.IsSafeToProceed    = false;
                result.WorstCaseScenarioText =
                    "Operation will fail immediately. No files will be altered.";
                return result;
            }

            if (Directory.Exists(destinationFolder))
            {
                result.Warnings.Add(
                    "Destination folder already exists: " + destinationFolder +
                    " — move would fail. Delete or rename the destination first.");
                result.IsSafeToProceed    = false;
                result.WorstCaseScenarioText =
                    "Operation blocked. Destination conflict detected. No files will be altered.";
                return result;
            }

            // --- Discover all files ---
            try
            {
                var files = Directory.GetFiles(sourceFolderPath, "*.*",
                    SearchOption.AllDirectories);

                result.TotalFileCount = files.Length;

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    result.TotalBytes += info.Length;
                    result.FilesToMove.Add(Path.GetFileName(file));

                    // Check for exclusive file locks (e.g. active transcode by Emby/ffmpeg).
                    // Use FileAccess.Read + FileShare.None: fails only if another process
                    // holds an exclusive write lock. Read-sharing by the OS is normal and
                    // does not block a Directory.Move on Windows/Linux.
                    if (IsFileLocked(info))
                    {
                        result.Warnings.Add(
                            "File is locked by an active process (possible active transcode): " +
                            Path.GetFileName(file));
                        // Soft gate — warn but don't block. Directory.Move may still succeed
                        // on most OS configurations. User sees the warning and decides.
                    }
                }

                result.FriendlySize = FormatBytes(result.TotalBytes);

                // --- Worst-case text ---
                if (result.IsSafeToProceed)
                {
                    result.WorstCaseScenarioText =
                        "If the move fails mid-operation, some files may remain at the source " +
                        "while others have reached the destination. The plugin's automatic DB " +
                        "rollback will attempt to restore the database pointer to the original " +
                        "path immediately. If rollback also fails, Emby will log a critical " +
                        "warning. In all cases, NO files are deleted — data loss cannot occur " +
                        "from a failed move. Emby will re-discover files from whichever path " +
                        "they end up at on the next library scan.";
                }
                else
                {
                    result.WorstCaseScenarioText =
                        "Safety checks failed. The operation is blocked to prevent data fragmentation.";
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: MigrationAnalyzer error for {0}", ex, sourceFolderPath);
                result.Warnings.Add("Could not read filesystem: " + ex.Message);
                result.IsSafeToProceed    = false;
                result.WorstCaseScenarioText =
                    "Unknown system state. Do not proceed.";
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static bool IsFileLocked(FileInfo file)
        {
            try
            {
                // FileAccess.Read + FileShare.None: detects exclusive locks without
                // requiring write permission ourselves (important on media files)
                using (var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                    stream.Close();
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double d = bytes;
            while (i < suffix.Length - 1 && bytes >= 1024)
            {
                bytes /= 1024;
                d = bytes / 1024.0;
                i++;
            }
            return string.Format("{0:0.##} {1}", d, suffix[i]);
        }
    }
}
