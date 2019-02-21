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
        public readonly List<string> DeletedFiles = new List<string>();
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

        private string GetLocalFileName(Uri uri)
        {
            var fileName = uri.AbsolutePath.Replace("//", "/").Replace(@"/", @"\").Replace(":", "--COLON--").Substring(1);
            return Path.Combine(_localPath, fileName);
        }

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

                        var bJob = new BlobJob(blob, GetLocalFileName(blob.Uri));
                        ExpectedLocalFiles.Add(bJob.LocalFileName);

                        ILocalFileInfo file = new LocalFileInfoDisk(bJob.LocalFileName);
                        if (!file.Exists)
                        {
                            bJob.NeedsJob = JobType.New;
                            BlobJobQueue.AddDone(bJob);
                            NewFiles.Add(bJob);
                        }
                        else if (file.LastWriteTimeUtc < blob.LastModifiedUtc.UtcDateTime || file.Size != blob.Size)
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
                // scan for deleted files by checking if we have a file in the local file system that we did not find remotely
                foreach (var fileName in Directory.GetFiles(localContainerPath, "*", SearchOption.AllDirectories))
                {
                    if (fileName.Contains(FLAG_MODIFIED) || fileName.Contains(FLAG_DELETED))
                        continue;
                    if (!ExpectedLocalFiles.Contains(fileName))
                    {
                        Console.Write("D");
                        File.Move(fileName, fileName + FLAG_DELETED + DateTime.Now.ToString(FLAG_DATEFORMAT) + FLAG_END);
                        DeletedFiles.Add(fileName);
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
            internal readonly string LocalFileName;
            internal JobType NeedsJob = JobType.None;
            internal Action JobFinally;

            public BlobJob(BlobItem blob, string localFileName)
            {
                Blob = blob;
                LocalFileName = localFileName;
            }

            public async Task<bool> DoJob()
            {
                try
                {
                    if (NeedsJob == JobType.New)
                    {
                        Console.Write("N");
                        Directory.CreateDirectory(Path.GetDirectoryName(LocalFileName));
                    }
                    else if (NeedsJob == JobType.Modified)
                    {
                        Console.Write("m");
                        File.Move(LocalFileName, LocalFileName + FLAG_MODIFIED + Blob.LastModifiedUtc.ToString(FLAG_DATEFORMAT) + FLAG_END);
                    }
                    else
                    {
                        return true;
                    }

                    await Blob.DownloadToFileAsync(LocalFileName, FileMode.Create);
                    File.SetLastWriteTimeUtc(LocalFileName, Blob.LastModifiedUtc.UtcDateTime);
                    NeedsJob = JobType.None;
                    return true;
                }
                catch (StorageException ex)
                {
                    // Swallow 404 exceptions.
                    // This will happen if the file has been deleted in the temporary period from listing blobs and downloading
                    Console.Write("Swallowed Ex: " + LocalFileName + " " + ex.GetType().Name + " " + ex.Message);
                }
                catch (System.IO.IOException ex)
                {
                    Console.Write("Swallowed Ex: " + LocalFileName + " " + ex.GetType().Name + " " + ex.Message);
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
