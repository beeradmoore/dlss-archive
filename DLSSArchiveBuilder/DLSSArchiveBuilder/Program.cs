using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DLSSArchiveBuilder
{
    class Program
    {
        static string OutputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output");
        static void Main(string[] args)
        {
            if (Directory.Exists(OutputDirectory) == false)
            {
                Directory.CreateDirectory(OutputDirectory);
            }

            var baseDllDirectory = @"C:\DLSS\dlls";

            var files = Directory.GetFiles(baseDllDirectory, "*.dll", SearchOption.AllDirectories);
            var localDlls = new List<LocalDll>();
            foreach (var file in files)
            {
                LocalDll localDll;
                try
                {
                    localDll = new LocalDll(file);
                    string zip = localDll.CreateZip(OutputDirectory);
                    localDlls.Add(localDll);

                    Console.WriteLine($"{localDll.Version} is valid.");

                    // Set the creation time of the zip and the dll to the signed time.
                    File.SetCreationTime(zip, localDll.SignedDateTime);
                    //File.SetLastAccessTime(zip, DateTime.Now);
                    //File.SetLastWriteTimeUtc(zip, localDll.SignedDateTime);
                }
                catch (Exception err)
                {
                    Console.WriteLine($"ERROR: Could not validate zip or create archive, {err.Message}");
                }
            }

            localDlls.Sort((x, y) => x.VersionNumber.CompareTo(y.VersionNumber));

            var json = JsonSerializer.Serialize(localDlls, new JsonSerializerOptions() { WriteIndented = true });
            File.WriteAllText(Path.Combine(OutputDirectory, "dlss_versions.json"), json);
        }
    }
}
