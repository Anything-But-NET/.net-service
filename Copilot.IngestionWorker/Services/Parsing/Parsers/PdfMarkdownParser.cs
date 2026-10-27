using Copilot.IngestionWorker.Models;
using System;
using System.Collections.Generic;
using System.Text;
using UglyToad.PdfPig;

namespace Copilot.IngestionWorker.Services.Parsing.Parsers
{
    public class PdfMarkdownParser : IDocumentParser
    {
        public bool CanParse(string contentType, string fileName)
        {
            return string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
                   (fileName != null && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<ParsedDocumentResult> ParseAsync(Stream fileStream, string documentId, CancellationToken cancellationToken)
        {
            try
            {
                if (fileStream == null || fileStream.Length == 0)
                    return ParsedDocumentResult.Failure(documentId, "PDF file stream is empty.");

                using var memoryStream = new MemoryStream();
                await fileStream.CopyToAsync(memoryStream, cancellationToken);
                memoryStream.Position = 0;

                using var pdfDocument = PdfDocument.Open(memoryStream);
                var markdownBuilder = new StringBuilder();

                foreach (var page in pdfDocument.GetPages())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var words = page.GetWords().ToList();

                    if (!words.Any()) continue;

                    var baseFontSize = words.GroupBy(w => Math.Round(w.Letters.First().PointSize))
                                            .OrderByDescending(g => g.Count())
                                            .Select(g => g.Key)
                                            .FirstOrDefault();

                    var lines = words.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 5) * 5)
                                     .OrderByDescending(g => g.Key);

                    foreach (var line in lines)
                    {
                        var lineWords = line.OrderBy(w => w.BoundingBox.Left).ToList();
                        var lineText = string.Join(" ", lineWords.Select(w => w.Text));

                        var firstWord = lineWords.First();
                        var fontSize = Math.Round(firstWord.Letters.First().PointSize);
                        var isBold = firstWord.FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase);

                        if (fontSize >= baseFontSize + 6)
                            markdownBuilder.AppendLine($"\n# {lineText}\n");
                        else if (fontSize >= baseFontSize + 3)
                            markdownBuilder.AppendLine($"\n## {lineText}\n");
                        else if (isBold)
                            markdownBuilder.AppendLine($"**{lineText}**");
                        else
                            markdownBuilder.AppendLine(lineText);
                    }
                    markdownBuilder.AppendLine("\n---"); 
                }

                return ParsedDocumentResult.Success(documentId, markdownBuilder.ToString());
            }
            catch (Exception ex)
            {
                return ParsedDocumentResult.Failure(documentId, $"Failed to parse PDF to Markdown: {ex.Message}");
            }
        }
    }
}
