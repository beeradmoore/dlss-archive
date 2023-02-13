﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DLSSArchiveBuilder
{
    class Program
    {
        static string OutputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output");
        static async Task Main(string[] args)
        {
            // Deleting directory is not instant, moving is :|
            if (Directory.Exists(OutputDirectory))
            {
                var tempDirectory = OutputDirectory + "_" + (new Random().Next(0, 9999));
                Directory.Move(OutputDirectory, tempDirectory);
                Directory.Delete(tempDirectory, true);
            }

            Directory.CreateDirectory(OutputDirectory);

            var validFileDescriptions = new List<string>()
            {
                "NGX DLSS",
                "NGX DLSS - DVS PRODUCTION",
                "NGX DLSS - DVS VIRTUAL",
                "NVIDIA DLSSv2 - DVS PRODUCTION",
                "NVIDIA DLSSv3 - DVS PRODUCTION",
            };

            var baseDllDirectory = @"base_dlss";
            if (Directory.Exists(baseDllDirectory) == false)
            {
                Directory.CreateDirectory(baseDllDirectory);
            }

            Console.WriteLine("Checking against DLSS-Archive");
            using (var httpClient = new HttpClient())
            {
                var text = await httpClient.GetStringAsync("https://raw.githubusercontent.com/beeradmoore/dlss-archive/main/dlss_records.json");
                var dlssRecrods = await httpClient.GetFromJsonAsync<DLSSRecords>("https://raw.githubusercontent.com/beeradmoore/dlss-archive/main/dlss_records.json");

                var dlssToDownload = new List<(string OutputPath, DLSSRecord DLSSRecord)>();
                foreach (var dlssRecord in dlssRecrods.Stable)
                {
                    var expectedPath = Path.Combine(baseDllDirectory, Path.GetFileName(dlssRecord.DownloadUrl));
                    if (File.Exists(expectedPath))
                    {
                        var zipHash = String.Empty;
                        using (var fileStream = File.Open(expectedPath, FileMode.Open))
                        {
                            using (var md5 = MD5.Create())
                            {
                                var hash = md5.ComputeHash(fileStream);
                                zipHash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                            }
                        }

                        if (zipHash != dlssRecord.ZipMD5Hash)
                        {
                            System.Diagnostics.Debug.WriteLine($"Invalid MD5 for {dlssRecord.DownloadUrl}. Expected {dlssRecord.ZipMD5Hash}, got {zipHash}");
                            File.Delete(expectedPath);
                            dlssToDownload.Add(new(expectedPath, dlssRecord));
                        }
                    }
                    else
                    {
                        dlssToDownload.Add(new(expectedPath, dlssRecord));
                    }
                }
                foreach (var dlssRecord in dlssRecrods.Experimental)
                {
                    var expectedPath = Path.Combine(baseDllDirectory, Path.GetFileName(dlssRecord.DownloadUrl));
                    if (File.Exists(expectedPath))
                    {
                        var zipHash = String.Empty;
                        using (var fileStream = File.Open(expectedPath, FileMode.Open))
                        {
                            using (var md5 = MD5.Create())
                            {
                                var hash = md5.ComputeHash(fileStream);
                                zipHash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                            }
                        }

                        if (zipHash != dlssRecord.ZipMD5Hash)
                        {
                            System.Diagnostics.Debug.WriteLine($"Invalid MD5 for {dlssRecord.DownloadUrl}. Expected {dlssRecord.ZipMD5Hash}, got {zipHash}");
                            File.Delete(expectedPath);
                            dlssToDownload.Add(new(expectedPath, dlssRecord));
                        }
                    }
                    else
                    {
                        dlssToDownload.Add(new(expectedPath, dlssRecord));
                    }
                }

                // Download all required DLSS files.
                if (dlssToDownload.Any())
                {
                    var parallelOptions = new ParallelOptions()
                    {
                        //MaxDegreeOfParallelism = 3
                    };

                    await Parallel.ForEachAsync(dlssToDownload, parallelOptions, async (item, token) =>
                    {
                        System.Diagnostics.Debug.WriteLine($"Downloading {item.DLSSRecord.DownloadUrl}");
                        using (var stream = await httpClient.GetStreamAsync(item.DLSSRecord.DownloadUrl))
                        {
                            using (var fileStream = File.Create(item.OutputPath))
                            {
                                await stream.CopyToAsync(fileStream);
                            }
                        }
                    });
                }
            }

            var files = Directory.GetFiles(baseDllDirectory, "*.zip", SearchOption.AllDirectories);


            var additionalNotes = new StringBuilder();

            var dlssRecords = new DLSSRecords();

            var localDlls = new List<DLSSRecord>();
            foreach (var file in files)
            {
                var dllOutputPath = Path.Combine(OutputDirectory, Path.GetFileNameWithoutExtension(file));
                Directory.CreateDirectory(dllOutputPath);
                using (var archive = ZipFile.OpenRead(file))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name == "nvngx_dlss.dll")
                        {
                            var dllOutputName = Path.Combine(dllOutputPath, "nvngx_dlss.dll");
                            entry.ExtractToFile(dllOutputName);

                            var creationTime = entry.LastWriteTime.DateTime;

                            try
                            {
                                var dlssRecord = new DLSSRecord(dllOutputName, true);

                                // Change creation time back to when it was created. 
                                // Makes organising in a folder interesting. Also makes sure creation date never changes.
                                File.SetCreationTime(dllOutputName, dlssRecord.SignedDateTime);

                                var zipFilename = $"nvngx_dlss_{dlssRecord.Version}.zip";

                                if (validFileDescriptions.Contains(dlssRecord.FileDescription))
                                {
                                    // Special case for DLSS 2.4.12 v2
                                    if (dlssRecord.MD5Hash == "0A71EFBA8DAFF9C284CE6010923C01F1")
                                    {
                                        zipFilename = $"nvngx_dlss_{dlssRecord.Version}_v2.zip";
                                        dlssRecord.AdditionalLabel = "v2";
                                    }
                                    else if (dlssRecord.MD5Hash == "31BFD8F750F87E5040557D95C2345080")
                                    {
                                        zipFilename = $"nvngx_dlss_{dlssRecord.Version}_v3.zip";
                                        dlssRecord.AdditionalLabel = "v3";
                                    }


                                    dlssRecords.Stable.Add(dlssRecord);
                                }
                                else
                                {
                                    dlssRecords.Experimental.Add(dlssRecord);

                                    // What follows is weird jank to get some sort of automated filename out of the file description which hopefully always explains the beta
                                    var experimentalName = dlssRecord.FileDescription;
                                    foreach (var validFileDescription in validFileDescriptions)
                                    {
                                        experimentalName = experimentalName.Replace(validFileDescription, String.Empty);

                                        var validFileDescriptionParts = validFileDescription.Split(" - ");
                                        foreach (var validFileDescriptionPart in validFileDescriptionParts)
                                        {
                                            experimentalName = experimentalName.Replace(validFileDescriptionPart, String.Empty);
                                        }
                                    }

                                    experimentalName = experimentalName.Trim(new char[] { ' ', '-' });

                                    dlssRecord.AdditionalLabel = experimentalName;

                                    experimentalName = experimentalName.Replace(" - ", "_");
                                    experimentalName = experimentalName.Replace("-", "_");
                                    experimentalName = experimentalName.Replace(" ", "_");

                                    zipFilename = $"nvngx_dlss_{dlssRecord.Version}_{experimentalName}.zip";
                                }

                                var tagName = "v" + Path.GetFileNameWithoutExtension(zipFilename).Replace("nvngx_dlss_", String.Empty);

                                dlssRecord.DownloadUrl = $"https://github.com/beeradmoore/dlss-archive/releases/download/{tagName}/{zipFilename}";

                                additionalNotes.AppendLine(zipFilename);
                                additionalNotes.AppendLine($"version: {dlssRecord.Version}");
                                additionalNotes.AppendLine($"versionNumber: {dlssRecord.VersionNumber}");
                                additionalNotes.AppendLine($"tag: {tagName}");
                                additionalNotes.AppendLine($"SignedDate: {dlssRecord.SignedDateTime.ToLongDateString()} {dlssRecord.SignedDateTime.ToLongTimeString()} UTC");
                                additionalNotes.AppendLine($"Signed UnixTimestamp: {(new DateTimeOffset(dlssRecord.SignedDateTime)).ToUnixTimeSeconds()}");
                                additionalNotes.AppendLine();


                                CreateZip(dlssRecord, Path.Combine(OutputDirectory, zipFilename));
                            }
                            catch (Exception err)
                            {
                                Console.WriteLine($"ERROR: Could not validate zip or create archive, {err.Message}");
                            }
                        }
                    }
                }
            }

            dlssRecords.Stable.Sort((x, y) => x.VersionNumber.CompareTo(y.VersionNumber));
            dlssRecords.Experimental.Sort((x, y) => x.VersionNumber.CompareTo(y.VersionNumber));

            var json = JsonSerializer.Serialize(dlssRecords, new JsonSerializerOptions() { WriteIndented = true });
            File.WriteAllText(Path.Combine(OutputDirectory, "dlss_records.json"), json);
            File.WriteAllText(Path.Combine(OutputDirectory, "additional_notes.txt"), additionalNotes.ToString());

            // This was used to backfill.
            /*
            var allVersionNumbers = new List<ulong>();
            allVersionNumbers.AddRange(dlssRecords.Stable.Select(x => x.VersionNumber));
            allVersionNumbers.AddRange(dlssRecords.Experimental.Select(x => x.VersionNumber));
            allVersionNumbers.Sort((x, y) => y.CompareTo(x)); // reverse order
            allVersionNumbers = allVersionNumbers.Distinct().ToList();

            foreach (var version in allVersionNumbers)
            {
                var dlssRecordsTemp = new DLSSRecords();
                dlssRecordsTemp.Stable = dlssRecords.Stable.Where(x => x.VersionNumber <= version).ToList();
                dlssRecordsTemp.Experimental = dlssRecords.Experimental.Where(x => x.VersionNumber <= version).ToList();
                json = JsonSerializer.Serialize(dlssRecordsTemp, new JsonSerializerOptions() { WriteIndented = true });
                File.WriteAllText(Path.Combine(OutputDirectory, $"dlss_records_{version}.json"), json);
            }
            */
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dlssRecord">The dll record to zip</param>
        /// <param name="outputZipName">Path to where the final zip will be created</param>
        static void CreateZip(DLSSRecord dlssRecord, string outputZipName)
        {

            using (var zipFile = File.Open(outputZipName, FileMode.Create))
            {
                using (var zipArchive = new ZipArchive(zipFile, ZipArchiveMode.Create, true))
                {
                    zipArchive.CreateEntryFromFile(dlssRecord.Filename, Path.GetFileName(dlssRecord.Filename));
                }

                zipFile.Position = 0;

                // Once again, MD5 should never be used to check if a file has been tampered with.
                // We are simply using it to check the integrity of the downloaded/extracted file.
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(zipFile);
                    dlssRecord.ZipMD5Hash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                }
            }

            var fileInfo = new FileInfo(outputZipName);
            dlssRecord.ZipFileSize = fileInfo.Length;

            // Set the creation time of the zip to the original of the dll. This may not be when it was actually created.
            var creationTime = File.GetCreationTime(dlssRecord.Filename);
            File.SetCreationTime(outputZipName, creationTime);

        }
    }
}
