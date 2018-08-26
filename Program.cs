using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using qfind.Interfaces;
using qfind.Implementations;
using qfind.Utils;
using qfind.Entities;
using qfind_index_service.Entities;

namespace qfind_index_service
{
    class Program
    {
        const string CONFIG_FILE_NAME = "config.json";
        const string DB_CONFIG_FILE_NAME = "dbconfig.json";
        static object _lockObj = new object();
        static void Main(string[] args)
        {
            var changeQueue = new Queue<ChangeInfo>();
            var config = ConfigurationsUtils.LoadConfigFile<WatcherConfigSection>(CONFIG_FILE_NAME);
            var dbConfig = ConfigurationsUtils.LoadConfigFile<DbConfigSection>(DB_CONFIG_FILE_NAME);
            var watcherTask = Task.WhenAll(CreateWatcherTasks(config, changeQueue));

            var dbAdapter = new SqliteAdapter(dbConfig);

            var indexTask = Index(changeQueue, dbAdapter);
            Task.WhenAll(indexTask, watcherTask).GetAwaiter().GetResult();
        }

        private static List<Task> CreateWatcherTasks(WatcherConfigSection config, Queue<ChangeInfo> changeQueue)
        {
            if (config != null)
            {
                return config.WatchedDirectories.Select(dir =>
                {
                    return Watch(dir, config.ExcludeDirectories, (ChangeInfo info) =>
                    {
                        lock (_lockObj)
                        {
                            changeQueue.Enqueue(info);
                        }
                    });
                }).ToList();
            }
            return new List<Task> { };
        }

        static async Task Index(Queue<ChangeInfo> changeQueue, IDbAdapter dbAdapter)
        {
            while (true)
            {
                if (changeQueue.Count > 0)
                {
                    var change = changeQueue.Dequeue();
                    System.Console.WriteLine(change.EventType+" "+change.Path);
                    switch (change.EventType)
                    {
                        case "Renamed":
                            await dbAdapter.UpdateIndexes(IndexUtils.CreateUpdateIndex(change.Path.ToLower(), change.NewPath.ToLower()));
                            break;
                        case "Created":
                            var newIdx = new Index
                            {
                                Name = Path.GetFileNameWithoutExtension(change.Path),
                                Extention = Path.GetExtension(change.Path),
                                Folder = Path.GetDirectoryName(change.Path),
                                SearchKey = change.Path,
                            };
                            dbAdapter.SetIndexes(new List<Index> { newIdx }, false);
                            break;
                        case "Deleted":
                            await dbAdapter.UpdateIndexes(IndexUtils.CreateUpdateIndex(change.Path.ToLower(), null));
                            break;
                        default:
                            break;
                    }
                }
                await Task.Delay(100);
            }
        }

        static async Task Watch(string folder, string[] excluded, Action<ChangeInfo> onChange)
        {
            await Task.WhenAll(
            Task.Delay(-1),
            Task.Run(() =>
                        {
                            System.Console.WriteLine($"watching {folder}");
                            FileSystemWatcher fsw = new FileSystemWatcher(folder);
                            fsw.IncludeSubdirectories = true;
                            var createDeleteHandler = new FileSystemEventHandler((object obj, FileSystemEventArgs fsArgs) =>
                        {
                            if (!excluded.Any(e => fsArgs.FullPath.StartsWith(e)))
                            {
                                onChange(new ChangeInfo
                                {
                                    EventType = fsArgs.ChangeType.ToString(),
                                    Path = fsArgs.FullPath
                                });
                            }
                        });

                            var renameHandler = new RenamedEventHandler((object obj, RenamedEventArgs fsArgs) =>
                           {

                               if (!excluded.Any(e => fsArgs.OldFullPath.StartsWith(e)))
                               {
                                   onChange(new ChangeInfo
                                   {
                                       EventType = fsArgs.ChangeType.ToString(),
                                       Path = fsArgs.OldFullPath,
                                       NewPath = fsArgs.FullPath
                                   });
                               }
                           });

                            fsw.Created += createDeleteHandler;
                            fsw.Deleted += createDeleteHandler;
                            fsw.Renamed += renameHandler;

                            fsw.EnableRaisingEvents = true;
                            var res = fsw.WaitForChanged(WatcherChangeTypes.All);
                        }));
        }
    }

}
