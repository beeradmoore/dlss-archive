using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DLSSArchiveBuilder
{
    internal class LocalDll
    {
        [JsonIgnore]
        public string Filename { get; private set; }

        [JsonPropertyName("version")]
        public string Version { get; private set; }

        [JsonPropertyName("version_number")]
        public ulong VersionNumber { get; private set; }

        [JsonPropertyName("md5_hash")]
        public string MD5Hash { get; private set; }

        [JsonPropertyName("zip_md5_hash")]
        public string ZipMD5Hash { get; private set; }

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; private set; } = "";

        [JsonIgnore]
        public DateTime SignedDateTime { get; private set; } = DateTime.MinValue;

        public LocalDll(string filename)
        {
            Filename = filename;
            var isValidSigned = WinTrust.VerifyEmbeddedSignature(filename);
            if (isValidSigned == false)
            {
                throw new Exception($"Error processing dll: Invalid signature found, {filename}");
            }


            SignedDateTime = GetSignedDateTime();

                        // Set the creation time of the zip and the dll to the signed time.
            File.SetCreationTime(filename, SignedDateTime);
            //File.SetLastAccessTime(filename, DateTime.Now);
            //File.SetLastWriteTimeUtc(filename, SignedDateTime);


            var versionInfo = FileVersionInfo.GetVersionInfo(filename);
            
            Version = $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}.{versionInfo.FilePrivatePart}";


            DownloadUrl = $"https://github.com/beeradmoore/dlss-archive/releases/download/v{Version}/nvngx_dlss_{Version}.zip";
            
            // VersionNumber is used for ordering dlls in the case where 2.1.18.0 would order below 2.1.2.0.
            // VersionNumber is calculated by putting the each part into a 16bit section of a 64bit number
            // VersionNumber = [AAAAAAAAAAAAAAAA][BBBBBBBBBBBBBBBB][CCCCCCCCCCCCCCCC][DDDDDDDDDDDDDDDD]
            // where AAAAAAAAAAAAAAAA = FileMajorPart
            //       BBBBBBBBBBBBBBBB = FileMinorPart
            //       CCCCCCCCCCCCCCCC = FileBuildPart
            //       DDDDDDDDDDDDDDDD = FilePrivatePart
            // https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.fileversioninfo?view=net-5.0#remarks
            VersionNumber = ((ulong)versionInfo.FileMajorPart << 48) +
                         ((ulong)versionInfo.FileMinorPart << 32) +
                         ((ulong)versionInfo.FileBuildPart << 16) +
                         ((ulong)versionInfo.FilePrivatePart);

            // MD5 should never be used to check if a file has been tampered with.
            // We are simply using it to check the integrity of the downloaded/extracted file.
            using (var stream = File.OpenRead(filename))
            {
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(stream);
                    MD5Hash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                }
            }
        }

        internal string CreateZip(string outputDirectory)
        {
            var outputFile = Path.Combine(outputDirectory, $"nvngx_dlss_{Version}.zip");

            using (var zipFile = File.Open(outputFile, FileMode.Create))
            {
                using (var zipArchive = new ZipArchive(zipFile, ZipArchiveMode.Create, true))
                {
                    zipArchive.CreateEntryFromFile(Filename, Path.GetFileName(Filename));
                }

                zipFile.Position = 0;

                // Once again, MD5 should never be used to check if a file has been tampered with.
                // We are simply using it to check the integrity of the downloaded/extracted file.
                using (var md5 = MD5.Create())
                {
                    var hash = md5.ComputeHash(zipFile);
                    ZipMD5Hash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                }
            }

            return outputFile;
        }


        // Via https://stackoverflow.com/a/4927876
        private DateTime GetSignedDateTime()
        {
            int encodingType;
            int contentType;
            int formatType;
            IntPtr certStore = IntPtr.Zero;
            IntPtr cryptMsg = IntPtr.Zero;
            IntPtr context = IntPtr.Zero;

            if (!WinCrypt.CryptQueryObject(
                WinCrypt.CERT_QUERY_OBJECT_FILE,
                Marshal.StringToHGlobalUni(Filename),
                WinCrypt.CERT_QUERY_CONTENT_FLAG_ALL,
                WinCrypt.CERT_QUERY_FORMAT_FLAG_ALL,
                0,
                out encodingType,
                out contentType,
                out formatType,
                ref certStore,
                ref cryptMsg,
                ref context))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            //expecting contentType=10; CERT_QUERY_CONTENT_PKCS7_SIGNED_EMBED 
            //Logger.LogInfo(string.Format("Querying file '{0}':", filename));
            //Logger.LogInfo(string.Format("  Encoding Type: {0}", encodingType));
            //Logger.LogInfo(string.Format("  Content Type: {0}", contentType));
            //Logger.LogInfo(string.Format("  Format Type: {0}", formatType));
            //Logger.LogInfo(string.Format("  Cert Store: {0}", certStore.ToInt32()));
            //Logger.LogInfo(string.Format("  Crypt Msg: {0}", cryptMsg.ToInt32()));
            //Logger.LogInfo(string.Format("  Context: {0}", context.ToInt32()));
            // Get size of the encoded message.
            int cbData = 0;
            if (!WinCrypt.CryptMsgGetParam(
                cryptMsg,
                WinCrypt.CMSG_ENCODED_MESSAGE,//Crypt32.CMSG_SIGNER_INFO_PARAM,
                0,
                IntPtr.Zero,
                ref cbData))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var vData = new byte[cbData];

            // Get the encoded message.
            if (!WinCrypt.CryptMsgGetParam(
                cryptMsg,
                WinCrypt.CMSG_ENCODED_MESSAGE,//Crypt32.CMSG_SIGNER_INFO_PARAM,
                0,
                vData,
                ref cbData))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var signedCms = new SignedCms();
            signedCms.Decode(vData);

            foreach (var signerInfo in signedCms.SignerInfos)
            {
                foreach (var unsignedAttribute in signerInfo.UnsignedAttributes)
                {
                    if (unsignedAttribute.Oid.Value == WinCrypt.szOID_RSA_counterSign)
                    {
                        foreach (var counterSignInfo in signerInfo.CounterSignerInfos)
                        {
                            foreach (var signedAttribute in counterSignInfo.SignedAttributes)
                            {
                                if (signedAttribute.Oid.Value == WinCrypt.szOID_RSA_signingTime)
                                {
                                    var signingTime = (Pkcs9SigningTime)signedAttribute.Values[0];
                                    return signingTime.SigningTime;
                                }
                            }
                        }
                    }
                    else if (unsignedAttribute.Oid.Value == "1.3.6.1.4.1.311.3.3.1")
                    {
                        var pkcs9AttributeObject = (Pkcs9AttributeObject)unsignedAttribute.Values[0];

                        SignedCms rfcTimestampMessage = new SignedCms();
                        rfcTimestampMessage.Decode(pkcs9AttributeObject.RawData);

                        foreach (var internalSignerInfo in rfcTimestampMessage.SignerInfos)
                        {
                            foreach (var signedAttribute in internalSignerInfo.SignedAttributes)
                            {
                                if (signedAttribute.Oid.Value == WinCrypt.szOID_RSA_signingTime)
                                {
                                    var signingTime = (Pkcs9SigningTime)signedAttribute.Values[0];
                                    return signingTime.SigningTime;
                                }
                            }
                        }
                    }
                    else if (unsignedAttribute.Oid.Value == "1.3.6.1.4.1.311.3.2.1") //SPC_TIME_STAMP_REQUEST_OBJID 
                    {
                        int x = 0;
                    }
                    else if (unsignedAttribute.Oid.Value == "1.3.6.1.4.1.311.10.3.2") // szOID_KP_TIME_STAMP_SIGNING 
                    {
                        int x = 0;
                    }
                }

                foreach (var signedAttribute in signerInfo.SignedAttributes)
                {
                    if (signedAttribute.Oid.Value == "1.3.6.1.4.1.311.3.2.1") //SPC_TIME_STAMP_REQUEST_OBJID 
                    {
                        int x = 0;
                    }
                    else if (signedAttribute.Oid.Value == "1.3.6.1.4.1.311.10.3.2") // szOID_KP_TIME_STAMP_SIGNING 
                    {
                        int x = 0;
                    }
                }
            }

            return DateTime.MinValue;
        }
    }
}
