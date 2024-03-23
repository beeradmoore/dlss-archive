using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DLSSArchiveBuilder
{
    internal static class Extensions
    {
        internal static string GetMD5Hash(this FileStream fileStream)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(fileStream);
                return BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
            }
        }
    }
}
