﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using Raven.Client.Util;

namespace Raven.Client.ServerWide.Operations.Migration
{
    public class OfflineMigrationConfiguration
    {
        private OfflineMigrationConfiguration()
        {
            // for deserialization
        }

        public OfflineMigrationConfiguration(string dataDirectory, string dataExporterFullPath, DatabaseRecord databaseRecord)
        {
            DataDirectory = dataDirectory;
            DataExporterFullPath = dataExporterFullPath;
            DatabaseRecord = databaseRecord;
        }

        public string DataDirectory { get; set; }
        public string OutputFilePath { get; set; }
        public string DataExporterFullPath { get; set; }
        public int? BatchSize { get; set; }
        public bool IsRavenFs { get; set; }
        public bool IsCompressed { get; set; }
        public string JournalsPath { get; set; }
        public string EncryptionKey { get; set; }
        public string EncryptionAlgorithm { get; set; }
        public int? EncryptionKeyBitsSize { get; set; }
        public TimeSpan? Timeout { get; set; }
        public DatabaseRecord DatabaseRecord { get; set; }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(DataDirectory))
                throw new ArgumentNullException(nameof(DataDirectory));

            if (string.IsNullOrWhiteSpace(DataExporterFullPath))
                throw new ArgumentNullException(nameof(DataExporterFullPath));

            if (DatabaseRecord == null)
                throw new ArgumentNullException(nameof(DatabaseRecord));

            if (string.IsNullOrWhiteSpace(DatabaseRecord.DatabaseName))
                throw new ArgumentNullException(nameof(DatabaseRecord.DatabaseName));
        }

        internal (string Commandline, string TmpFile) GenerateExporterCommandLine()
        {
            Validate();

            string tmpFile = null;
            var sb = new StringBuilder();

            if (OutputFilePath == null)
            {
                var rempDir = Path.GetTempPath();
                
                OutputFilePath = tmpFile = Path.Combine(rempDir, $"export-{DatabaseRecord.DatabaseName}-{SystemTime.UtcNow:yyyyMMdd_HHmmss}.ravendump");
            }

            sb.Append($"{EnsureStringsAreQuoted(DataDirectory)} {EnsureStringsAreQuoted(OutputFilePath)}");

            if (BatchSize.HasValue)
            {
                sb.Append($" -BatchSize {BatchSize.Value}");
            }

            if (IsRavenFs)
            {
                sb.Append(" --RavenFS");
            }

            if (IsCompressed)
            {
                sb.Append(" --Compression");
            }

            if (string.IsNullOrEmpty(JournalsPath) == false)
            {
                sb.Append($" -JournalsPath {EnsureStringsAreQuoted(JournalsPath)}");
            }

            if (string.IsNullOrEmpty(EncryptionKey) == false && string.IsNullOrEmpty(EncryptionAlgorithm) == false && EncryptionKeyBitsSize.HasValue)
            {
                sb.Append($" -Encryption {EnsureStringsAreQuoted(EncryptionKey)} {EnsureStringsAreQuoted(EncryptionAlgorithm)} {EncryptionKeyBitsSize.Value}");
            }

            return (sb.ToString(), tmpFile);

            string EnsureStringsAreQuoted(string path)
            {
                //The path is already quoted
                if (path.First() == '\"' && path.Last() == '\"')
                    return path;
                return $"\"{path}\"";
            }
        }
    }
}
