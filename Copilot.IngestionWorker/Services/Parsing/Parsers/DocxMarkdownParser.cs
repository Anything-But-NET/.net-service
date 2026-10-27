using Confluent.Kafka;
using Copilot.IngestionWorker.Models;
using System.Text;
using Mammoth;
using ReverseMarkdown;

namespace Copilot.IngestionWorker.Services.Parsing.Parsers
{
    public class DocxMarkdownParser : IDocumentParser
    {
        public bool CanParse(string contentType, string fileName)
        {
            return string.Equals(contentType, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase) ||
                   (fileName != null && fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<ParsedDocumentResult> ParseAsync(Stream fileStream, string documentId, CancellationToken cancellationToken)
        {
            try
            {
                if (fileStream == null || fileStream.Length == 0)
                    return ParsedDocumentResult.Failure(documentId, "DOCX file stream is empty.");

                // Mammoth вимагає можливості читати потік з початку, тому копіюємо в пам'ять
                using var memoryStream = new MemoryStream();
                await fileStream.CopyToAsync(memoryStream, cancellationToken);
                memoryStream.Position = 0;

                // Крок 1: Конвертуємо DOCX у чистий HTML за допомогою Mammoth
                var mammothConverter = new DocumentConverter();
                var htmlResult = mammothConverter.ConvertToHtml(memoryStream);
                var htmlContent = htmlResult.Value; // Тут збережуться параграфи, <h1>, <b> тощо

                if (string.IsNullOrWhiteSpace(htmlContent))
                {
                    return ParsedDocumentResult.Success(documentId, string.Empty);
                }

                // Крок 2: Перетворюємо HTML у Markdown за допомогою ReverseMarkdown
                var mdConfig = new ReverseMarkdown.Config
                {
                    UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Drop, // Викидаємо сміттєві теги
                    GithubFlavored = true // Вмикаємо підтримку таблиць та списків у стилі GitHub
                };
                var mdConverter = new Converter(mdConfig);
                var markdownContent = mdConverter.Convert(htmlContent);

                return ParsedDocumentResult.Success(documentId, markdownContent);
            }
            catch (Exception ex)
            {
                return ParsedDocumentResult.Failure(documentId, $"Failed to parse DOCX to Markdown: {ex.Message}");
            }
        }
    }
}
