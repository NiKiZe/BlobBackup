﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
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
        private string _localPath;

        public Backup(string localPath)
        {
            _localPath = localPath;
        }

        private string GetLocalFileName(string localPath, Uri uri)
        {
            var fileName = uri.AbsolutePath.Replace("//", "/").Replace(@"/", @"\").Replace(":", "--COLON--").Substring(1);
            return Path.Combine(localPath, fileName);
        }

        public BackupJob PrepareJob(string containerName, string accountName, string accountKey, IProgress<int> progress)
        {

            Directory.CreateDirectory(Path.Combine(_localPath, containerName));

            var job = new BackupJob();
            var allRemoteFiles = new HashSet<string>();

            try
            {
                var account = CloudStorageAccount.Parse($"DefaultEndpointsProtocol=https;AccountName={accountName};AccountKey={accountKey};EndpointSuffix=core.windows.net");
                var client = account.CreateCloudBlobClient();
                var container = client.GetContainerReference(containerName);

                foreach (IListBlobItem blobItem in container.ListBlobs(null, true, BlobListingDetails.None))
                {
                    try
                    {
                        job.ScannedItems++;
                        if (job.ScannedItems % 50000 == 0)
                        {
                            progress.Report(job.ScannedItems);
                        }

                        if (blobItem is CloudBlockBlob)
                        {
                            var localFileName = GetLocalFileName(_localPath, blobItem.Uri);
                            allRemoteFiles.Add(localFileName);
                            CloudBlockBlob blob = blobItem as CloudBlockBlob;

                            var file = new FileInfo(localFileName);

                            if (file.Exists)
                            {
                                if (file.LastWriteTime < blob.Properties.LastModified || file.Length != blob.Properties.Length)
                                {
                                    job.ModifiedFiles.Add(blob);
                                    job.ModifiedFilesSize += blob.Properties.Length;
                                }
                                else
                                {
                                    job.UpToDateItems++;
                                }
                            }
                            else
                            {
                                job.NewFiles.Add(blob);
                                job.NewFilesSize += blob.Properties.Length;
                            }
                        }
                        else
                        {
                            job.IgnoredItems++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"INSIDE LOOP EXCEPTION while scanning {containerName}. Item: {blobItem.Uri} Scanned Items: #{job.ScannedItems}. Ex message:" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OUTER EXCEPTION ({containerName}) #{job.ScannedItems}: " + ex.Message);
            }

            // scan for deleted files by checking if we have a file in the local file system that we did not find remotely
            foreach (var fileName in Directory.GetFiles(Path.Combine(_localPath, containerName), "*", SearchOption.AllDirectories))
            {
                if (!fileName.Contains("[MODIFIED ") && !fileName.Contains("[DELETED "))
                {
                    if (!allRemoteFiles.Contains(fileName))
                    {
                        job.DeletedFiles.Add(@fileName);
                    }
                }
            }

            return job;
        }

        public async Task ProcessJob(BackupJob job, int simultaniousDownloads)
        {
            var tasks = new List<Task>();

            var throttler = new SemaphoreSlim(initialCount: simultaniousDownloads);

            if (job.NewFiles.Count > 0)
            {
                Console.Write("Downloading new files: ");

                foreach (var item in job.NewFiles)
                {
                    await throttler.WaitAsync();
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var localFileName = GetLocalFileName(_localPath, item.Uri);
                            Directory.CreateDirectory(Path.GetDirectoryName(localFileName));
                            await item.DownloadToFileAsync(localFileName, FileMode.Create);
                            Console.Write(".");
                        }
                        catch (StorageException ex)
                        {
                            // Swallow 404 exceptions.
                            // This will happen if the file has been deleted in the temporary period from listing blobs and downloading
                            Console.Write("Swallowed Ex: " + ex.Message);
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }));
                }

                Console.WriteLine();
            }

            if (job.ModifiedFiles.Count > 0)
            {
                Console.Write("Downloading modified files: ");

                foreach (var item in job.ModifiedFiles)
                {
                    await throttler.WaitAsync();
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var localFileName = GetLocalFileName(_localPath, item.Uri);
                            File.Move(localFileName, localFileName + $"[MODIFIED {item.Properties.LastModified.Value.ToString("yyyyMMddHmm")}]");
                            await item.DownloadToFileAsync(localFileName, FileMode.Create);
                            Console.Write("-");
                        }
                        catch (StorageException ex)
                        {
                            // Swallow 404 exceptions.
                            // This will happen if the file has been deleted in the temporary period from listing blobs and downloading
                            Console.Write("Swallowed Ex: " + ex.Message);
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }));
                }

                Console.WriteLine();
            }

            if (job.DeletedFiles.Count > 0)
            {
                Console.Write("Deleting files: ");
                foreach (var localFileName in job.DeletedFiles)
                {
                    File.Move(localFileName, localFileName + $"[DELETED {DateTime.Now.ToString("yyyyMMddHmm")}]");
                    Console.Write(".");
                }
                Console.WriteLine();
            }

            await Task.WhenAll(tasks);
        }

        public class BackupJob
        {
            public BackupJob()
            {
                ScannedItems = 0;
                IgnoredItems = 0;
                UpToDateItems = 0;
                NewFiles = new List<CloudBlockBlob>();
                ModifiedFiles = new List<CloudBlockBlob>();
                DeletedFiles = new List<string>();
                NewFilesSize = 0;
                ModifiedFilesSize = 0;
            }

            public int ScannedItems;
            public int IgnoredItems;
            public int UpToDateItems;
            public List<CloudBlockBlob> NewFiles;
            public List<CloudBlockBlob> ModifiedFiles;
            public List<string> DeletedFiles;
            public long NewFilesSize;
            public long ModifiedFilesSize;
        }
    }
}
