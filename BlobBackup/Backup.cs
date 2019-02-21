﻿using Microsoft.WindowsAzure.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlobBackup
{
    internal class Backup
    {
        private readonly string _localPath;
        private readonly string _containerName;

        public int ScannedItems = 0;
        public int IgnoredItems = 0;
        public int UpToDateItems = 0;
        public readonly List<BlobJob> NewFiles = new List<BlobJob>();
        public readonly List<BlobJob> ModifiedFiles = new List<BlobJob>();
        public int DeletedItems = 0;
        public long NewFilesSize => NewFiles.Sum(b => b.Blob.Size);
        public long ModifiedFilesSize => ModifiedFiles.Sum(b => b.Blob.Size);

        private HashSet<string> ExpectedLocalFiles = new HashSet<string>();
        private RunQueue<BlobJob> BlobJobQueue = new RunQueue<BlobJob>();
        internal List<Task> Tasks = new List<Task>();

        public Backup(string localPath, string containerName)
        {
            _localPath = localPath;
            _containerName = containerName;
        }

        private const string FLAG_MODIFIED = "[MODIFIED ";
        private const string FLAG_DELETED = "[DELETED ";
        private const string FLAG_DATEFORMAT = "yyyyMMddHmm";
        private const string FLAG_END = "]";

        public Backup PrepareJob(string accountName, string accountKey, IProgress<int> progress)
        {
            var localContainerPath = Path.Combine(_localPath, _containerName);
            Directory.CreateDirectory(localContainerPath);

            try
            {
                foreach (var blob in BlobItem.BlobEnumerator(_containerName, accountName, accountKey))
                {
                    ScannedItems++;
                    try
                    {
                        if (ScannedItems % 10000 == 0)
                        {
                            progress.Report(ScannedItems);
                        }

                        if (blob == null)
                        {
                            IgnoredItems++;
                            continue;
                        }

                        var bJob = new BlobJob(blob, Path.Combine(_localPath, blob.GetLocalFileName()));
                        ExpectedLocalFiles.Add(bJob.LocalFilePath);

                        ILocalFileInfo file = new LocalFileInfoDisk(bJob.LocalFilePath);
                        bJob.FileInfo = file;
                        if (!file.Exists)
                        {
                            bJob.NeedsJob = JobType.New;
                            BlobJobQueue.AddDone(bJob);
                            NewFiles.Add(bJob);
                        }
                        else if (file.Size != blob.Size || file.LastWriteTimeUtc < blob.LastModifiedUtc.UtcDateTime ||
                            (file.MD5 != null && !string.IsNullOrEmpty(blob.MD5) && file.MD5 != blob.MD5))
                        {
                            bJob.NeedsJob = JobType.Modified;
                            BlobJobQueue.AddDone(bJob);
                            ModifiedFiles.Add(bJob);
                        }
                        else
                        {
                            UpToDateItems++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"INSIDE LOOP EXCEPTION while scanning {_containerName}. Item: {blob.Uri} Scanned Items: #{ScannedItems}. Ex message:" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OUTER EXCEPTION ({_containerName}) #{ScannedItems}: " + ex.Message);
            }
            BlobJobQueue.RunnerDone();

            Tasks.Add(Task.Run(() =>
            {
                // scan for deleted files by checking if we have a file locally that we did not find remotely
                foreach (var fileName in Directory.GetFiles(localContainerPath, "*", SearchOption.AllDirectories))
                {
                    if (fileName.Contains(FLAG_MODIFIED) || fileName.Contains(FLAG_DELETED))
                        continue;
                    if (!ExpectedLocalFiles.Contains(fileName))
                    {
                        Console.Write("D");
                        var nowUtc = DateTime.UtcNow;
                        File.Move(fileName, fileName + FLAG_DELETED + nowUtc.ToString(FLAG_DATEFORMAT) + FLAG_END);
                        DeletedItems++;
                    }
                    ExpectedLocalFiles.Remove(fileName);
                }
            }));

            return this;
        }

        public async Task ProcessJob(int simultaniousDownloads)
        {
            var throttler = new SemaphoreSlim(initialCount: simultaniousDownloads);
            void releaseThrottler() => throttler.Release();

            foreach (var item in BlobJobQueue.GetDoneEnumerable())
            {
                item.JobFinally = releaseThrottler;
                await throttler.WaitAsync();
                Tasks.Add(Task.Run(item.DoJob));
            }

            await Task.WhenAll(Tasks);
        }

        internal enum JobType
        {
            None = 0,
            New = 1,
            Modified = 2,
        }

        public class BlobJob
        {
            internal readonly BlobItem Blob;
            internal readonly string LocalFilePath;
            internal ILocalFileInfo FileInfo;
            internal JobType NeedsJob = JobType.None;
            internal Action JobFinally;

            public BlobJob(BlobItem blob, string localFilePath)
            {
                Blob = blob;
                LocalFilePath = localFilePath;
            }

            public async Task<bool> DoJob()
            {
                try
                {
                    if (NeedsJob == JobType.New)
                    {
                        Console.Write("N");
                        Directory.CreateDirectory(Path.GetDirectoryName(LocalFilePath));
                    }
                    else if (NeedsJob == JobType.Modified)
                    {
                        if (FileInfo.Size == Blob.Size &&
                            (FileInfo.MD5 != null && !string.IsNullOrEmpty(Blob.MD5) && FileInfo.MD5 == Blob.MD5))
                        {
                            // since size and hash is the same as last, we just ignore this and don't update
                            return true;
                        }
                        Console.Write("m");
                        File.Move(LocalFilePath, LocalFilePath + FLAG_MODIFIED + Blob.LastModifiedUtc.ToString(FLAG_DATEFORMAT) + FLAG_END);
                    }
                    else
                    {
                        return true;
                    }

                    await Blob.DownloadToFileAsync(LocalFilePath, FileMode.Create);
                    var lfi = new LocalFileInfoDisk(LocalFilePath);
                    if (lfi.Exists && lfi.LastWriteTimeUtc != Blob.LastModifiedUtc.UtcDateTime)
                        File.SetLastWriteTimeUtc(LocalFilePath, Blob.LastModifiedUtc.UtcDateTime);

                    NeedsJob = JobType.None;
                    return true;
                }
                catch (StorageException ex)
                {
                    // Swallow 404 exceptions.
                    // This will happen if the file has been deleted in the temporary period from listing blobs and downloading
                    Console.Write("Swallowed Ex: " + LocalFilePath + " " + ex.GetType().Name + " " + ex.Message);
                }
                catch (System.IO.IOException ex)
                {
                    Console.Write("Swallowed Ex: " + LocalFilePath + " " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    JobFinally();
                }
                return false;
            }
        }
    }
}
