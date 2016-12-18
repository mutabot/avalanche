﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amazon.Glacier.Transfer;
using Avalanche.Glacier;
using Avalanche.Lightroom;
using Avalanche.Models;
using log4net;

namespace Avalanche
{
    public class GlacierUploader
    {
        private class UploadBag
        {
            public UploadResult Result { get; set; }
            public PictureModel PictureModel { get; set; }
        }

        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));
        private static readonly BlockingCollection<UploadBag> UploadQueue = new BlockingCollection<UploadBag>();
        private readonly LightroomRepository _lightroomRepository;
        private readonly GlacierGateway _glacierGateway;

        public GlacierUploader(LightroomRepository lightroomRepository, GlacierGateway glacierGateway)
        {
            _lightroomRepository = lightroomRepository;
            _glacierGateway = glacierGateway;
        }

        public void RunUploader(ICollection<PictureModel> pictures)
        {
            var catalogId = _lightroomRepository.GetUniqueId();            

            Log.InfoFormat("Backing up {0} images", pictures.Count);

            Task.Factory.StartNew(RunUploadCompleteQueue);

            var tasks = new List<Task>();
            var index = 0;
            foreach (var f in pictures)
            {
                Log.InfoFormat("Archiving {0}/{1}: {2}", ++index, pictures.Count, Path.Combine(f.AbsolutePath, f.FileName));

                try
                {
                    var saveTask = _glacierGateway
                        .SaveImageAsync(f)
                        .ContinueWith(task =>
                        {
                            UploadQueue.Add(new UploadBag
                            {
                                PictureModel = f,
                                Result = task.Result
                            });
                        });

                    lock (tasks)
                    {
                        Log.DebugFormat("Adding task, id: {0}", saveTask.Id);
                        tasks.Add(saveTask);
                    }                    
                }
                catch (Exception ex)
                {
                    Log.ErrorFormat("Error!!! {0}", ex);
                    continue;
                }

                if (tasks.Count > 5)
                {
                    Log.DebugFormat("Waiting for any task to complete, count: {0}", tasks.Count);
                    Task.WhenAny(tasks).Wait();
                   
                    // purge completed tasks
                    lock (tasks)
                    {
                        tasks.RemoveAll(task => task.IsCompleted);
                    }
                    Log.DebugFormat("After purge, count: {0}", tasks.Count);
                }
            }
            Task.WhenAll(tasks).Wait();
            Log.InfoFormat("All upload tasks have completed...");
        }

        private void RunUploadCompleteQueue()
        {
            while (!UploadQueue.IsCompleted)
            {
                var bag = UploadQueue.Take();

                var result = bag.Result;
                var archive = new ArchiveModel
                {
                    ArchiveId = result.ArchiveId,
                    PostedTimestamp = DateTime.UtcNow
                };
                Log.InfoFormat("Upload completed: {0}", bag.PictureModel.FileName);

                if (_lightroomRepository.MarkAsArchived(archive, bag.PictureModel) < 1)
                {
                    Log.ErrorFormat("Failed to mark image as archived: {0}, archive Id: {1}", bag.PictureModel.FileName, bag.Result.ArchiveId);
                }
            }
        }
    }
}