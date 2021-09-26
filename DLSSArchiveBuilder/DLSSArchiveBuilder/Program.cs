using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DLSSArchiveBuilder
{
    class Program
    {
        static string OutputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output");
        static void Main(string[] args)
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
            };

            var baseDllDirectory = @"C:\DLSS\zips";

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
