﻿namespace SFP
{
    public class LocalFileModel
    {
        public const string PATCHED_TEXT = "/*patched*/\n";

        public static async Task<bool> Patch(FileInfo? file, string? overrideName = null)
        {
            if (file is null)
            {
                LogModel.Logger.Error($"Library file does not exist. Start Steam and try again");
                return false;
            }
            LogModel.Logger.Info($"Patching file {file.Name}");

            var state = false;
            var watcher = FSWModel.GetFileSystemWatcher(Path.Join(file.DirectoryName, "*.css"));
            if (watcher != null)
            {
                state = watcher.EnableRaisingEvents;
                watcher.EnableRaisingEvents = false;
            }

            var contents = await File.ReadAllTextAsync(file.FullName);

            if (contents.StartsWith(PATCHED_TEXT))
            {
                // File is already patched
                LogModel.Logger.Info($"{file.Name} is already patched.");
                return false;
            }

            var originalFile = new FileInfo($"{Path.Join(file.DirectoryName, Path.GetFileNameWithoutExtension(file.FullName))}.original{Path.GetExtension(file.FullName)}");
            FileInfo customFile;
            if (overrideName != null)
            {
                customFile = new FileInfo(Path.Join(file.DirectoryName, overrideName));
            }
            else
            {
                customFile = new FileInfo($"{Path.Join(file.DirectoryName, Path.GetFileNameWithoutExtension(file.FullName))}.custom{Path.GetExtension(file.FullName)}");
            }

            File.WriteAllText(originalFile.FullName, contents);

            contents = $"{PATCHED_TEXT}@import url(\"https://steamloopback.host/{originalFile.Directory.Name}/{originalFile.Name}\");\n@import url(\"https://steamloopback.host/{customFile.Name}\");\n";
            contents = string.Concat(contents, new string('\t', (int)(file.Length - contents.Length)));

            File.WriteAllText(file.FullName, contents);

            var customFileName = Path.Join(SteamModel.SteamUIDir, customFile.Name);
            if (!File.Exists(customFileName))
            {
                File.Create(customFileName).Dispose();
            }
            LogModel.Logger.Info($"Patched {file.Name}.\nPut your custom css in {customFileName}");
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = state;
            }
            return true;
        }

        public static async Task WatchLibrary(string directoryName)
        {
            await Task.Run(() => FSWModel.AddFileSystemWatcher(directoryName, "*.css", OnLibraryWatcherEvent));
        }

        private static async void OnLibraryWatcherEvent(object sender, FileSystemEventArgs e)
        {
            var file = new FileInfo(e.FullPath);
            if (file.Directory != null)
            {
                await Patch(file.Directory.EnumerateFiles().OrderByDescending(f => f.Length).FirstOrDefault(), "libraryroot.custom.css");
            }
        }

        public static async Task WatchLocal(string fileFullPath)
        {
            await Task.Run(() => FSWModel.AddFileSystemWatcher(Path.GetPathRoot(fileFullPath), Path.GetFileName(fileFullPath), OnLocalWatcherEvent));
        }

        private static async void OnLocalWatcherEvent(object sender, FileSystemEventArgs e)
        {
            var file = new FileInfo(e.FullPath);
            if (file.Directory != null)
            {
                await Patch(file);
            }
        }
    }
}
