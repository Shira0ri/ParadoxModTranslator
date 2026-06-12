using ModTranslator.BO.Objects.Requests;
using System.Text;
using System.Text.RegularExpressions;

namespace ModTranslator.BLL.Services.GenerateFilesForTranslation
{
    public class GenerateFileToTranslateService : IGenerateFileToTranslateService
    {
        private string OriginalLanguageCode = "";
        private string NewLanguageCode = "";

        public async Task<string> GenerateFile(GenerateFileToTranslateRequest request)
        {
            string outputMessage = "";
            int amountOfLinesToTranslate = 0;

            OriginalLanguageCode = request.FromLanguage.ToString();
            NewLanguageCode = request.ToLanguage.ToString();

            string filesFolder = request.FolderPath;
            List<string> filesFromOriginalLanguage = request.Files.Where(f => Path.GetFileNameWithoutExtension(f).Contains(OriginalLanguageCode)).ToList();
            List<string> filesFromNewLanguage = request.Files.Where(f => Path.GetFileNameWithoutExtension(f).Contains(NewLanguageCode)).ToList();

            Regex keyPattern = new(@"^[ \t]*([\w\.\-]+):\d*[ \t]*");

            foreach (string file in filesFromOriginalLanguage)
            {
                string baseName = Path.GetFileNameWithoutExtension(file)
                    .Replace(OriginalLanguageCode, "")
                    .Replace("ModTranslator_Translated_", "")
                    .Replace("ModTranslator_", "");

                string? matchingFileFromNewLanguage = filesFromNewLanguage.FirstOrDefault(x =>
                    Path.GetFileNameWithoutExtension(x)
                    .Replace(NewLanguageCode, "")
                    .Replace("ModTranslator_Translated_", "")
                    .Replace("ModTranslator_", "") == baseName);

                HashSet<string> existingKeys = [];
                int newFileLinesCount = 0;

                if (matchingFileFromNewLanguage != null)
                {
                    string[] newLines = await File.ReadAllLinesAsync(matchingFileFromNewLanguage);
                    newFileLinesCount = newLines.Length;
                    foreach (string line in newLines)
                    {
                        Match m = keyPattern.Match(line);
                        if (m.Success)
                        {
                            existingKeys.Add(m.Groups[1].Value);
                        }
                    }
                }

                List<string> outputLines = [];
                string[] origLines = await File.ReadAllLinesAsync(file);

                if (matchingFileFromNewLanguage != null && newFileLinesCount > origLines.Length)
                {
                    outputMessage += $"Warning: file {Path.GetFileName(matchingFileFromNewLanguage)} has more lines than {Path.GetFileName(file)}.\n";
                }

                bool skipCurrentEntry = false;
                int keysAdded = 0;

                foreach (string line in origLines)
                {
                    if (line.Trim().StartsWith(OriginalLanguageCode + ":") || line.Trim().StartsWith(OriginalLanguageCode))
                        continue;

                    if (line.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(line))
                        continue;

                    Match m = keyPattern.Match(line);
                    if (m.Success)
                    {
                        string key = m.Groups[1].Value;
                        if (existingKeys.Contains(key))
                        {
                            skipCurrentEntry = true;
                        }
                        else
                        {
                            skipCurrentEntry = false;
                            outputLines.Add(line);
                            keysAdded++;
                        }
                    }
                    else
                    {
                        if (!skipCurrentEntry)
                        {
                            outputLines.Add(line);
                        }
                    }
                }

                if (outputLines.Any())
                {
                    amountOfLinesToTranslate += keysAdded;

                    StringBuilder sb = new();
                    sb.AppendLine(NewLanguageCode + ":");
                    foreach (string outLine in outputLines)
                    {
                        if (!outLine.StartsWith(' ') && !outLine.StartsWith('\t'))
                            sb.AppendLine("  " + outLine);
                        else
                            sb.AppendLine(outLine);
                    }

                    string fileName = Path.GetFileNameWithoutExtension(file).Replace(OriginalLanguageCode, "");
                    string folderPath = Path.Combine(filesFolder, "ToBeTranslated");
                    string outputFilePath = Path.Combine(folderPath, $"{fileName}ModTranslator_ToBeTranslated_{NewLanguageCode}.yml");

                    _ = Directory.CreateDirectory(folderPath);
                    byte[] utf8WithBom = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
                    await File.WriteAllBytesAsync(outputFilePath, utf8WithBom);
                }
            }

            return $"{outputMessage}Amount of lines to translate: {amountOfLinesToTranslate}";
        }
    }
}