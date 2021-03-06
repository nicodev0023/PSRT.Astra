﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PSRT.Astra.Models.Phases
{
    public class ComparePhase
    {
        public CompareProgress Progress { get; } = new CompareProgress();

        private struct UpdateInfo
        {
            public PatchInfo PatchInfo;
            public bool ShouldUpdate;
        }

        private struct PreProcessInfo
        {
            public long LastWriteTime;
            public bool ShouldUpdate;
        }

        private InstallConfiguration _InstallConfiguration;

        public ComparePhase(InstallConfiguration installConfiguration)
        {
            _InstallConfiguration = installConfiguration;
        }

        public async Task<PatchInfo[]> RunAsync(DownloadConfiguration downloadConfiguration, PatchCache patchCache, CancellationToken ct = default)
        {
            Progress.Progress = 0;
            Progress.IsIndeterminate = true;

            App.Current.Logger.Info(nameof(ComparePhase), "Fetching patches");
            var patches = await PatchInfo.FetchPatchInfosAsync(_InstallConfiguration, downloadConfiguration, ct);

            App.Current.Logger.Info(nameof(ComparePhase), "Fetching cache data");
            var cacheData = await patchCache.SelectAllAsync();

            var preProcessData = new Dictionary<string, PreProcessInfo>();
            await Task.Run(() =>
            {
                foreach (var p in patches)
                    if (cacheData.ContainsKey(p.Name))
                        preProcessData[p.Name] = new PreProcessInfo
                        {
                            LastWriteTime = cacheData[p.Name].LastWriteTime,
                            ShouldUpdate = true
                        };
            });

            await Task.Run(() => _PreProcessPatches(preProcessData));

            var internalPatchesShouldUpdateKeys = preProcessData
                .Where(p => p.Value.ShouldUpdate)
                .Select(p => p.Key)
                .ToArray();

            var workingPatches = patches
                .Where(p => internalPatchesShouldUpdateKeys.Any(k => k == p.Name))
                .Select(p => new UpdateInfo
                {
                    PatchInfo = p,
                    ShouldUpdate = false
                })
                .ToArray();

            var nextIndexAtomic = 0;
            var progressValueAtomic = 0;

            var errorTokenSource = new CancellationTokenSource();
            var combinedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct, errorTokenSource.Token);

            App.Current.Logger.Info(nameof(ComparePhase), "Comparing files");
            Progress.IsIndeterminate = false;

            void ProcessLoop()
            {
                try
                {
                    var pso2ExecutableFileName = Path.GetFileName(_InstallConfiguration.PSO2Executable);

                    while (true)
                    {
                        var index = Interlocked.Increment(ref nextIndexAtomic);
                        if (index >= workingPatches.Length)
                            return;

                        combinedTokenSource.Token.ThrowIfCancellationRequested();

                        try
                        {
                            ref var patch = ref workingPatches[index];

                            var relativeFilePath = patch.PatchInfo.Name
                                        .Substring(0, patch.PatchInfo.Name.Length - 4)
                                        .Replace('/', Path.DirectorySeparatorChar)
                                        .ToLower();
                            var filePath = Path.Combine(_InstallConfiguration.PSO2BinDirectory, relativeFilePath);
                            var fileName = Path.GetFileName(relativeFilePath);

                            // skip this if mod files are not enabled so they are marked as invalid
                            if (Properties.Settings.Default.ModFilesEnabled)
                            {
                                // skip file if a file with the same name exists in the mods folder
                                if (Path.GetDirectoryName(filePath).ToLower() == _InstallConfiguration.DataWin32Directory.ToLower())
                                {
                                    var modFilePath = Path.Combine(_InstallConfiguration.ModsDirectory, fileName);
                                    if (File.Exists(modFilePath))
                                        continue;
                                }
                            }

                            if (!cacheData.ContainsKey(patch.PatchInfo.Name))
                            {
                                patch.ShouldUpdate = true;
                                continue;
                            }

                            var cacheEntry = cacheData[patch.PatchInfo.Name];

                            if (patch.PatchInfo.Hash != cacheEntry.Hash)
                            {
                                patch.ShouldUpdate = true;
                                continue;
                            }

                            // skip pso2.exe if the file is large address aware patched
                            if (fileName == pso2ExecutableFileName
                                && File.Exists(filePath)
                                && Properties.Settings.Default.LargeAddressAwareEnabled
                                && LargeAddressAware.IsLargeAddressAwarePactchApplied(_InstallConfiguration, patch.PatchInfo.Hash))
                            {
                                continue;
                            }

                            var fileInfo = new FileInfo(filePath);
                            patch.ShouldUpdate = fileInfo.LastWriteTimeUtc.ToFileTimeUtc() != cacheEntry.LastWriteTime;
                        }
                        finally
                        {
                            Interlocked.Increment(ref progressValueAtomic);
                        }
                    }
                }
                catch
                {
                    errorTokenSource.Cancel();
                    throw;
                }
            }

            var tasks = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(i => ConcurrencyUtils.RunOnDedicatedThreadAsync(ProcessLoop));
            var processTask = Task.WhenAll(tasks);

            while (await Task.WhenAny(processTask, Task.Delay(200)) != processTask)
                Progress.Progress = (progressValueAtomic / (double)workingPatches.Length);
            Progress.Progress = 1;

            await processTask;

            return workingPatches
                .Where(p => p.ShouldUpdate)
                .Select(p => p.PatchInfo)
                .ToArray();
        }

        private void _PreProcessPatches(Dictionary<string, PreProcessInfo> patches)
        {
            var findData = new Win32Bindings.WIN32_FIND_DATA();
            var handle = Win32Bindings.FindFirstFile(Path.Combine(_InstallConfiguration.PSO2BinDirectory, @"data\win32\*"), out findData);
            if (handle.ToInt64() == -1)
                return;

            try
            {
                do
                {
                    if ((findData.dwFileAttributes & (FileAttributes.Directory | FileAttributes.Hidden)) != 0)
                        continue;

                    var fileName = $"data/win32/{findData.cFileName}.pat";
                    if (!patches.ContainsKey(fileName))
                        continue;

                    var lastWriteTimeLong = (long)(((ulong)findData.ftLastWriteTime.dwHighDateTime) << 32) | (uint)findData.ftLastWriteTime.dwLowDateTime;

                    var patch = patches[fileName];
                    if (patch.LastWriteTime == lastWriteTimeLong)
                    {
                        patch.ShouldUpdate = false;
                        patches[fileName] = patch;
                    }
                } while (Win32Bindings.FindNextFile(handle, out findData));
            }
            finally
            {
                Win32Bindings.FindClose(handle);
            }
        }
    }
}
