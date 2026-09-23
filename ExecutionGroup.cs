using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DBMigragtionTool
{
    public class ExecutionGroup
    {
        public int Order { get; set; }

        [JsonPropertyName("files")]
        public List<string> Files { get; set; } = new();

        public static string GenerateArtifactExecutionConfig(string inputJson)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };

            var executionConfig =
                JsonSerializer.Deserialize<Dictionary<string, ExecutionGroup>>(
                    inputJson,
                    options)
                ?? throw new InvalidOperationException(
                    "Unable to deserialize execution configuration.");

            foreach (var section in executionConfig)
            {
                var folder = GetArtifactFolder(section.Key);

                section.Value.Files = section.Value.Files
                    .Select(file => AddFolderPrefix(folder, file))
                    .ToList();
            }

            return JsonSerializer.Serialize(executionConfig, options);
        }

        private static string GetArtifactFolder(string sectionName)
        {
            return sectionName switch
            {
                "Scripts" => "scripts",
                "CreatedSp" => "sp",
                "AlteredSp" => "sp",

                _ => throw new InvalidOperationException(
                    $"Unknown execution section '{sectionName}'.")
            };
        }

        private static string AddFolderPrefix(string folder, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidOperationException(
                    "Execution configuration contains an empty file name.");

            // Keep JSON paths consistent across Windows/Linux.
            fileName = fileName.Replace("\\", "/").TrimStart('/');

            // Avoid adding the same folder twice.
            if (fileName.StartsWith(
                    folder + "/",
                    StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            return $"{folder}/{fileName}";
        }
    }
}
