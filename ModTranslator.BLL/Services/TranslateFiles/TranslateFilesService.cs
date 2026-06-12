using ModTranslator.BO.Constants;
using ModTranslator.BO.Objects.API_DTO;
using ModTranslator.BO.Objects.Requests;
using ModTranslator.BO.Objects.Settings;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.RegularExpressions;

namespace ModTranslator.BLL.Services.TranslateFiles
{
    public class TranslateFilesService : ITranslateFilesService
    {
        private readonly SemaphoreSlim _concurrentRequestsLimiter;
        private readonly AppSettings _appSettings;
        private readonly HttpClient _httpClient;
        private string LanguageCode = "";
        private string LanguageName = "";
        private bool HasErrors = false;

        public TranslateFilesService(
            HttpClient httpClient,
            AppSettings appSettings)
        {
            _httpClient = httpClient;
            _appSettings = appSettings;
            _concurrentRequestsLimiter = new(appSettings.RequestsSettings.MaxConcurrentRequests);
        }

        public async Task<(bool isSuccess, string message)> TranslateFiles(TranslationRequest request)
        {
            if (_appSettings.APISettings.ApiKey == "Put your API key here and edit Url and Model to match yours.")
                return (false, "Go into the appsettings.json file and input your API settings.");

            foreach (string filePath in request.Files)
            {
                List<string>? fileLines = await GetFileContent(filePath);
                if (fileLines == null) return (false, ConstantStrings.ErrorNullUploadedFile);
                if (!SetLanguageCode(fileLines[0])) return (false, ConstantStrings.InvalidLanguageValueInFirstLine);

                string fileName = Path.GetFileName(filePath);
                string outputFilePath = await CreateTranslatedFileIfDontExist(request.CurrentPath, fileName, fileLines);

                if (File.Exists(outputFilePath))
                {
                    string[] existingLines = await File.ReadAllLinesAsync(outputFilePath);
                    HashSet<string> existingKeys = [];
                    Regex keyExtractPattern = new(@"^[ \t]*([\w\.\-]+):\d*[ \t]*");

                    foreach (string? line in existingLines.Skip(1))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
                        Match m = keyExtractPattern.Match(line);
                        if (m.Success) existingKeys.Add(m.Groups[1].Value);
                    }

                    List<string> filteredFileLines = [];
                    bool skipCurrentMultiline = false;

                    foreach (string line in fileLines)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                        {
                            filteredFileLines.Add(line);
                            continue;
                        }

                        Match m = keyExtractPattern.Match(line);
                        if (m.Success)
                        {
                            skipCurrentMultiline = existingKeys.Contains(m.Groups[1].Value);
                            if (!skipCurrentMultiline) filteredFileLines.Add(line);
                        }
                        else if (!skipCurrentMultiline)
                        {
                            filteredFileLines.Add(line);
                        }
                    }
                    fileLines = filteredFileLines;
                }

                using SemaphoreSlim fileWriteLock = new(1, 1);
                var workChannel = Channel.CreateUnbounded<(int index, string line)>();
                HashSet<int> processedIndices = [];

                _ = Task.Run(async () =>
                {
                    foreach (var (line, index) in fileLines.Select((l, i) => (l, i)))
                    {
                        if (!line.Trim().StartsWith(LanguageCode) && !processedIndices.Contains(index))
                        {
                            _ = processedIndices.Add(index);
                            await workChannel.Writer.WriteAsync((index, line));
                        }
                    }
                    workChannel.Writer.Complete();
                });

                List<Task> workers = [];
                for (int i = 0; i < _appSettings.RequestsSettings.MaxConcurrentRequests; i++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        (int index, string line)? leftoverTask = null;

                        while (leftoverTask != null || await workChannel.Reader.WaitToReadAsync())
                        {
                            List<string> originalLinesBatch = [];
                            List<string> linesToSendToApi = [];
                            int currentCharacterCount = 0;
                            int maxCharacterLimit = 1500;

                            if (leftoverTask != null)
                            {
                                originalLinesBatch.Add(leftoverTask.Value.line);
                                linesToSendToApi.Add(leftoverTask.Value.line.Trim());
                                currentCharacterCount += leftoverTask.Value.line.Length;
                                leftoverTask = null;
                            }

                            while (originalLinesBatch.Count < _appSettings.RequestsSettings.MaxLengthOfRequests &&
                                   currentCharacterCount < maxCharacterLimit)
                            {
                                if (workChannel.Reader.TryRead(out var task))
                                {
                                    if (originalLinesBatch.Count > 0 && currentCharacterCount + task.line.Length > maxCharacterLimit)
                                    {
                                        leftoverTask = task;
                                        break;
                                    }

                                    originalLinesBatch.Add(task.line);
                                    linesToSendToApi.Add(task.line.Trim());
                                    currentCharacterCount += task.line.Length;
                                }
                                else break;
                            }

                            if (originalLinesBatch.Count == 0) continue;

                            List<string> outputLines = await SafeTranslateBatch(originalLinesBatch, linesToSendToApi);

                            if (outputLines.Count > 0)
                            {
                                await fileWriteLock.WaitAsync();
                                try
                                {
                                    await File.AppendAllTextAsync(outputFilePath, string.Join('\n', outputLines) + Environment.NewLine);
                                }
                                finally { fileWriteLock.Release(); }
                            }
                        }
                    }));
                }

                await Task.WhenAll(workers);
                await File.AppendAllTextAsync(outputFilePath, "#File translation finished" + Environment.NewLine);
            }

            string result = "Translation finished.";
            if (HasErrors) result += "\nThere could be errors in the translation, please review the files.";
            return (!HasErrors, result);
        }

        private async Task<List<string>> SafeTranslateBatch(List<string> originalLinesBatch, List<string> linesToSendToApi)
        {
            string? batchTranslation = await GetTranslationFromAPI(linesToSendToApi, isStrict: false);
            if (batchTranslation != null)
            {
                List<string> processedBatch = ProcessTranslation(originalLinesBatch, batchTranslation);
                if (!processedBatch.Any(line => Regex.IsMatch(line, @"[\p{IsCJKUnifiedIdeographs}]")))
                    return processedBatch;

                Console.WriteLine("⚠️ Language bleed detected in batch! Falling back to line-by-line translation...");
            }

            List<string> finalSafeLines = new();
            for (int i = 0; i < originalLinesBatch.Count; i++)
            {
                string origLine = originalLinesBatch[i];
                string apiLine = linesToSendToApi[i];

                string? singleTrans = await GetTranslationFromAPI(new List<string> { apiLine }, isStrict: false);
                List<string> singleProcessed = singleTrans != null
                    ? ProcessTranslation(new List<string> { origLine }, singleTrans)
                    : new List<string> { origLine };

                if (singleTrans != null && singleProcessed.Any(line => Regex.IsMatch(line, @"[\p{IsCJKUnifiedIdeographs}]")))
                {
                    Console.WriteLine($"🔥 Extreme Language Bleed detected. Engaging STRICT mode for line...");
                    string? strictTrans = await GetTranslationFromAPI(new List<string> { apiLine }, isStrict: true);
                    if (strictTrans != null)
                        singleProcessed = ProcessTranslation(new List<string> { origLine }, strictTrans);
                }

                finalSafeLines.AddRange(singleProcessed);
            }
            return finalSafeLines;
        }

        private async Task<string?> GetTranslationFromAPI(List<string> lines, bool isStrict)
        {
            string batchedText = string.Join('\n', lines);
            string systemPrompt = isStrict ? GenerateStrictSystemPrompt() : GenerateSystemPrompt();
            string userPrompt = isStrict
                ? $"Translate the following text to English.\n{batchedText}\n[WARNING: YOU MUST NOT OUTPUT ANY CHINESE CHARACTERS. ONLY ENGLISH.]"
                : $"Translate this:\n{batchedText}\n[End of text. You MUST reply ONLY with the English translation. Do not output Chinese. Do not output conversational filler. Do not use markdown.]";

            APIRequest.Data requestBody = new()
            {
                Model = _appSettings.APISettings.Model,
                Messages =
                [
                    new APIRequest.Message { Role = "system", Content = systemPrompt },
                    new APIRequest.Message { Role = "user", Content = userPrompt }
                ]
            };

            StringContent requestContent = new(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using HttpRequestMessage apiRequest = new(HttpMethod.Post, _appSettings.APISettings.Url)
            {
                Content = requestContent,
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _appSettings.APISettings.ApiKey) }
            };

            try
            {
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(_appSettings.RequestsSettings.TimeoutSeconds));
                HttpResponseMessage response = await _httpClient.SendAsync(apiRequest, cts.Token);
                string responseString = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<APIResponse>(responseString)?.Choices?.FirstOrDefault()?.Message?.Content;
            }
            catch (Exception ex)
            {
                HasErrors = true;
                Console.WriteLine($"Error in API request: {ex.Message}");
                return null;
            }
        }

        private string GenerateSystemPrompt()
        {
            return $"""
            You are an expert game localization translator for Paradox Interactive. You are translating a Stellaris sci-fi mod from Simplified Chinese to {LanguagesManager.GetLanguageKey(LanguageCode)}.
            
            CRITICAL DIRECTIVES:
            1. 1-TO-1 LINE MATCHING: Return EXACTLY the same number of lines. 
            2. NO MARKDOWN: Do NOT use ``` blocks. Do NOT number the lines (1. 2. 3.).
            3. PRESERVE THE IDENTIFIER: Keep the original localization identifier exactly as it was.
            4. ENGINE CODES (CRITICAL): Stellaris uses strict formatting codes. You MUST NOT translate the text inside these codes, and you MUST keep them exactly where they are:
               - Bracket Variables: [Root.GetName], [This.Owner]
               - Dollar Sign Macros: $NAME_KEY$, $VALUE|*x$
               - Icon Pound Codes: £energy£, £minerals£
               - Color Codes: §R, §G, §H, §Y, etc. AND their terminator §!
               - Escape Characters: \n (newline) and \t (tab).
            5. VARIABLE PRESERVATION: 
               - NEVER modify the symbols around variables. $MACRO$ must remain $MACRO$. Do NOT change it to §MACRO$ or alter the brackets.
               - If you open a bracket [, you MUST close it with ].
               - NEVER use $ for money (e.g., do NOT write "50$"). Stellaris uses £energy£ instead.
            6. ZERO ADDITIONS: Output ONLY the translated strings. Do not output conversational filler.
            """;
        }
        private string GenerateStrictSystemPrompt()
        {
            return $"""
            You are a strict translation machine for the game Stellaris. 
            Your ONLY purpose is to translate text into {LanguagesManager.GetLanguageKey(LanguageCode)}.
            
            FATAL ERROR PROTOCOL: 
            - If you output even ONE Chinese character, the system will crash.
            - If you translate the english words inside £icons£, $macros$, or [Variables], the system will crash.
            - If you alter §Color codes or \n line breaks, the system will crash.
            
            Do not explain. Do not apologize. Keep all formatting variables strictly intact.
            TRANSLATE THE TEXT INTO ENGLISH NOW:
            """;
        }

        private List<string> ProcessTranslation(List<string> originalLines, string translation)
        {
            List<string> result = [];

            translation = Regex.Replace(translation, @"(?i)^Here is the translation:\s*", "");

            List<string> rawTranslatedLines = translation
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("```") && !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (originalLines.Count != rawTranslatedLines.Count) HasErrors = true;

            int transIndex = 0;
            Regex origKeyPattern = new(@"^([ \t]*[\w\.\-]+):(\d*)[ \t]*");
            Regex aiGarbagePattern = new(@"^(\d+\.[ \t]*|\-[ \t]*|\*?\*?[\w\.\-]+:\d*[ \t]*)");

            for (int i = 0; i < originalLines.Count; i++)
            {
                string original = originalLines[i];
                string translated = transIndex < rawTranslatedLines.Count ? rawTranslatedLines[transIndex] : original.TrimStart();
                transIndex++;

                Match origMatch = origKeyPattern.Match(original);

                if (origMatch.Success)
                {
                    string keyName = origMatch.Groups[1].Value;
                    string versionNum = origMatch.Groups[2].Value;
                    if (string.IsNullOrEmpty(versionNum)) versionNum = "0";

                    string forcedPrefix = $"{keyName}:{versionNum} ";

                    string cleanTranslatedText = translated;
                    Match transMatch = aiGarbagePattern.Match(translated);
                    if (transMatch.Success)
                        cleanTranslatedText = translated.Substring(transMatch.Length).TrimStart(' ', '\t', '*');

                    cleanTranslatedText = SanitizeTranslationText(cleanTranslatedText);
                    result.Add($"{forcedPrefix}\"{cleanTranslatedText}\"");
                }
                else
                {
                    result.Add(SanitizeTranslationText(translated));
                }
            }
            return result;
        }

        /// <summary>
        /// Phase 1 & 2 of the Bulletproof Vault. Purges illegal characters and mathematically Auto-Heals orphaned variables.
        /// </summary>
        /// <summary>
        /// Phase 1 & 2 of the Bulletproof Vault. Purges illegal characters and mathematically Auto-Heals orphaned variables.
        /// </summary>
        private string SanitizeTranslationText(string text)
        {
            string clean = text;

            // 0. PURGE ILLEGAL UNICODE / CJK PUNCTUATION
            // 0. PURGE ILLEGAL UNICODE / CJK PUNCTUATION
            clean = clean.Replace("“", "\"").Replace("”", "\"").Replace("„", "\"")
                         .Replace("‘", "'").Replace("’", "'").Replace("‚", "'")
                         .Replace("–", "-").Replace("—", "-").Replace("…", "...")
                         .Replace("、", ", ").Replace("。", ". ")
                         .Replace("！", "!").Replace("？", "?")
                         .Replace("：", ":").Replace("；", ";")
                         .Replace("（", "(").Replace("）", ")")
                         .Replace("【", "[").Replace("】", "]"); // <- NEW: Safely converts CJK brackets to standard brackets

            // A. Strip outer wrapping quotes
            if (clean.StartsWith("\"") && clean.EndsWith("\"") && clean.Length >= 2)
                clean = clean.Substring(1, clean.Length - 2);
            else if (clean.StartsWith("\"")) clean = clean.Substring(1);
            else if (clean.EndsWith("\"")) clean = clean.Substring(0, clean.Length - 1);

            // B. Escape all inner quotes
            clean = clean.Replace("\\\"", "\"").Replace("\"", "\\\"");

            // C. Eject possessive 's from Variables & Strip remaining inner apostrophes
            clean = Regex.Replace(clean, @"\[([^\]]+)'s\]", "[$1]'s");
            clean = Regex.Replace(clean, @"\$([^$]+)'s\$", "$$$1$$'s");
            clean = Regex.Replace(clean, @"\[(.*?)\]", m => m.Value.Replace("'", ""));
            clean = Regex.Replace(clean, @"\$(.*?)\$", m => m.Value.Replace("'", ""));

            // D. Purge rogue backslashes (Allows \[ and \] safely)
            clean = Regex.Replace(clean, @"\\(?![n""t\[\]])", "");

            // E. Auto-Heal specific AI Hallucinations
            clean = clean.Replace("§{", "$").Replace("}$", "$");     // Reverts the §{MACRO}$ hallucination
            clean = clean.Replace(":$", "§").Replace(": $", "§");    // Reverts the :$Y hallucination

            // ---> YOUR NEW INVERSE MACRO FIXES <---
            // 1. Converts §MACRO$ into $MACRO$
            clean = Regex.Replace(clean, @"§(?=[\w\|\*\+\-]+\$)", "$$");

            // 2. Converts $MACRO§ into $MACRO$ (But uses (?!!) to ensure it doesn't accidentally destroy a §! terminator)
            clean = Regex.Replace(clean, @"(?<=\$[\w\|\*\+\-]+)§(?!!)", "$$");

            // ---> YOUR TERMINATOR LOOKAHEAD FIX <---
            // Converts $ to § ONLY IF there is a valid color letter AND a §! terminator waiting for it later.
            clean = Regex.Replace(clean, @"\$([WTgLPRSHKYIGVECBM_cvdrl!])(?=[^§\$]*§!)", "§$1");

            // General fallback: Fixes $YTrade (Converts $ to § if followed by a Color letter AND a lowercase word)
            clean = Regex.Replace(clean, @"(?<!\w)\$([WTgLPRSHKYIGVECBM_cvdrl!])(?=[a-z]|\s|[A-Z][a-z])", "§$1");

            clean = Regex.Replace(clean, @"§\s+", "§");              // Fixes "§ W" spacing issues

            // Converts invalid/orphaned § into a terminator (e.g., "Trade Value§" securely becomes "Trade Value§!")
            clean = Regex.Replace(clean, @"§(?![WTgLPRSHKYIGVECBM_cvdrl!])", "§!");

            // F. FORCE TAG PARITY (Mathematical Orphan Purger)

            // Fix '$' Orphans (e.g. 50$)
            if (clean.Count(c => c == '$') % 2 != 0)
            {
                clean = Regex.Replace(clean, @"(?<=\d)\$|\$(?=\d|\s|$)", ""); // Delete $ touching numbers or empty space
                if (clean.Count(c => c == '$') % 2 != 0)
                {
                    int lastIdx = clean.LastIndexOf('$');
                    if (lastIdx >= 0) clean = clean.Remove(lastIdx, 1); // Delete the absolute last one if still unbalanced
                }
            }

            // Fix '£' Orphans
            if (clean.Count(c => c == '£') % 2 != 0)
            {
                int lastIdx = clean.LastIndexOf('£');
                if (lastIdx >= 0) clean = clean.Remove(lastIdx, 1);
            }

            // Fix Bracket '[' ']' Orphans
            int openBrackets = clean.Count(c => c == '[');
            int closeBrackets = clean.Count(c => c == ']');
            while (openBrackets > closeBrackets)
            {
                int idx = clean.LastIndexOf('[');
                if (idx >= 0) clean = clean.Remove(idx, 1);
                openBrackets--;
            }
            while (closeBrackets > openBrackets)
            {
                int idx = clean.LastIndexOf(']');
                if (idx >= 0) clean = clean.Remove(idx, 1);
                closeBrackets--;
            }

            return clean;
        }
        private async Task<string> CreateTranslatedFileIfDontExist(string currentPath, string fileName, List<string> fileLines)
        {
            string outputFolder = Path.Combine(currentPath, "TranslatedFiles", "localisation", "replace", LanguageCode);
            _ = Directory.CreateDirectory(outputFolder);
            string cleanFileName = fileName.Replace("ModTranslator_ToBeTranslated_", "");
            string outputFilePath = Path.Combine(outputFolder, cleanFileName);

            if (!File.Exists(outputFilePath))
                await File.WriteAllBytesAsync(outputFilePath, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(fileLines[0] + Environment.NewLine)]);

            return outputFilePath;
        }

        private bool SetLanguageCode(string firstFileLine)
        {
            LanguageCode = firstFileLine.Replace(":", "");
            LanguageName = LanguagesManager.GetLanguageKey(LanguageCode);
            return !string.IsNullOrEmpty(LanguageName);
        }

        private static async Task<List<string>?> GetFileContent(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            try
            {
                return await File.ReadAllLinesAsync(filePath).ContinueWith(t => t.Result.Where(line => !string.IsNullOrWhiteSpace(line)).ToList());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading file: {ex.Message}");
                return null;
            }
        }
    }
}