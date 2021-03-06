﻿/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class FileDownloader
    {
        public const string FILES_DIRECTORY = "files";

        private static Dictionary<uint, Regex> Files = new Dictionary<uint, Regex>();

        private static CDNClient CDNClient;

        public static void SetCDNClient(CDNClient cdnClient)
        {
            CDNClient = cdnClient;

            try
            {
                string filesDir = Path.Combine(Application.Path, FILES_DIRECTORY);
                Directory.CreateDirectory(filesDir);
            }
            catch (Exception ex)
            {
                Log.WriteError("FileDownloader", "Unable to create files directory: {0}", ex.Message);
            }

            ReloadFileList();
        }

        public static void ReloadFileList()
        {
            string file = Path.Combine(Application.Path, FILES_DIRECTORY, "files.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn("FileDownloader", "files/files.json not found. No files will be downloaded.");
            }
            else
            {
                Files = new Dictionary<uint, Regex>();

                var files = JsonConvert.DeserializeObject<Dictionary<uint, List<string>>>(File.ReadAllText(file), new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });

                foreach(var depot in files)
                {
                    string pattern = string.Format("^({0})$", string.Join("|", depot.Value.Select(x => ConvertFileMatch(x))));

                    Files[depot.Key] = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
                }
            }
        }

        public static bool IsImportantDepot(uint depotID)
        {
            return Files.ContainsKey(depotID);
        }

        public static void DownloadFilesFromDepot(DepotProcessor.ManifestJob job, DepotManifest depotManifest)
        {
            var files = depotManifest.Files.Where(x => IsFileNameMatching(job.DepotID, x.FileName)).ToList();
            var filesUpdated = false;

            Log.WriteDebug("FileDownloader", "Will download {0} files from depot {1}", files.Count(), job.DepotID);

            foreach (var file in files)
            {
                string directory    = Path.Combine(Application.Path, FILES_DIRECTORY, job.DepotID.ToString(), Path.GetDirectoryName(file.FileName));
                string finalPath    = Path.Combine(directory, Path.GetFileName(file.FileName));
                string downloadPath = Path.GetTempFileName();

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                else if (File.Exists(finalPath))
                {
                    using (var fs = File.Open(finalPath, FileMode.Open))
                    {
                        using (var sha = new SHA1Managed())
                        {
                            if (file.FileHash.SequenceEqual(sha.ComputeHash(fs)))
                            {
                                Log.WriteDebug("FileDownloader", "{0} already matches the file we have", file.FileName);

                                continue;
                            }
                        }
                    }
                }

                Log.WriteInfo("FileDownloader", "Downloading {0} ({1} bytes, {2} chunks)", file.FileName, file.TotalSize, file.Chunks.Count);

                uint count = 0;
                byte[] checksum;
                string lastError = "or checksum failed";

                using (var fs = File.Open(downloadPath, FileMode.OpenOrCreate))
                {
                    fs.SetLength((long)file.TotalSize);

                    var lockObject = new object();

                    // TODO: We *could* verify each chunk and only download needed ones
                    Parallel.ForEach(file.Chunks, (chunk, state) =>
                    {
                        var downloaded = false;

                        for (var i = 0; i <= 5; i++)
                        {
                            try
                            {
                                var chunkData = CDNClient.DownloadDepotChunk(job.DepotID, chunk, job.Server, job.CDNToken, job.DepotKey);

                                lock (lockObject)
                                {
                                    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                                    fs.Write(chunkData.Data, 0, chunkData.Data.Length);

                                    Log.WriteDebug("FileDownloader", "Downloaded {0} ({1}/{2})", file.FileName, ++count, file.Chunks.Count);
                                }

                                downloaded = true;

                                break;
                            }
                            catch (Exception e)
                            {
                                lastError = e.Message;
                            }
                        }

                        if (!downloaded)
                        {
                            state.Stop();
                        }
                    });

                    fs.Seek(0, SeekOrigin.Begin);

                    using (var sha = new SHA1Managed())
                    {
                        checksum = sha.ComputeHash(fs);
                    }
                }

                if (file.Chunks.Count == 0 || file.FileHash.SequenceEqual(checksum))
                {
                    Log.WriteInfo("FileDownloader", "Downloaded {0} from {1}", file.FileName, Steam.GetAppName(job.ParentAppID));

                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }

                    File.Move(downloadPath, finalPath);

                    filesUpdated = true;
                }
                else
                {
                    IRC.Instance.SendOps("{0}[{1}]{2} Failed to download {3}: Only {4} out of {5} chunks downloaded ({6})",
                        Colors.OLIVE, Steam.GetAppName(job.ParentAppID), Colors.NORMAL, file.FileName, count, file.Chunks.Count, lastError);

                    Log.WriteError("FileDownloader", "Failed to download {0}: Only {1} out of {2} chunks downloaded from {3} ({4})",
                        file.FileName, count, file.Chunks.Count, job.Server, lastError);

                    File.Delete(downloadPath);
                }
            }

            if (filesUpdated)
            {
                var updateScript = Path.Combine(Application.Path, "files", "update.sh");

                if (File.Exists(updateScript))
                {
                    // YOLO
                    Process.Start(updateScript, job.DepotID.ToString());
                }
            }
        }

        private static bool IsFileNameMatching(uint depotID, string fileName)
        {
            return Files[depotID].IsMatch(fileName.Replace('\\', '/'));
        }

        private static string ConvertFileMatch(string input)
        {
            if (input.StartsWith("regex:", StringComparison.Ordinal))
            {
                return input.Substring(6);
            }

            return Regex.Escape(input);
        }
    }
}
