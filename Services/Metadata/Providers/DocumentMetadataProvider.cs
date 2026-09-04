using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;
using ClankerExplorer.Services.Preview;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Extracts document metadata: page count, author, title, revision, words, application for PDF and Office/OpenXML documents.
/// </summary>
public class DocumentMetadataProvider : IMetadataProvider
{
    public int Order => 10;

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".rtf"
    };

    public bool CanHandle(MetadataExtractionContext context)
    {
        return !context.IsDirectory && DocumentExtensions.Contains(context.Extension);
    }

    public async Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        string path = context.FilePath;
        if (!File.Exists(path)) return;

        string ext = context.Extension.ToLowerInvariant();

        if (ext == ".pdf")
        {
            await ExtractPdfMetadataAsync(context, path, cancellationToken).ConfigureAwait(false);
        }
        else if (ext is ".docx" or ".xlsx" or ".pptx")
        {
            await ExtractOfficeOpenXmlMetadataAsync(context, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExtractPdfMetadataAsync(MetadataExtractionContext context, string path, CancellationToken cancellationToken)
    {
        // 1. Page count via PdfPreviewService (Windows.Data.Pdf)
        try
        {
            var pdfInfo = await PdfPreviewService.Instance.GetPdfInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (pdfInfo.IsValid && pdfInfo.PageCount > 0)
            {
                context.AddItem("Document", "📄", "Pages", $"{pdfInfo.PageCount:N0} {(pdfInfo.PageCount == 1 ? "page" : "pages")}", isCopyable: true, isMonospace: true);
            }
        }
        catch { }

        // 2. Windows Storage Document Properties
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
                var docProps = await storageFile.Properties.GetDocumentPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

                if (docProps != null)
                {
                    if (!string.IsNullOrWhiteSpace(docProps.Title))
                    {
                        context.AddItem("Document", "📄", "Title", docProps.Title, isCopyable: true);
                    }
                    if (docProps.Author.Count > 0)
                    {
                        context.AddItem("Document", "📄", "Author", string.Join(", ", docProps.Author), isCopyable: true);
                    }
                    if (docProps.Keywords.Count > 0)
                    {
                        context.AddItem("Document", "📄", "Keywords", string.Join(", ", docProps.Keywords), isCopyable: true);
                    }
                }

                var extra = await storageFile.Properties.RetrievePropertiesAsync(new[]
                {
                    "System.Subject",
                    "System.ApplicationName",
                    "System.Document.Producer"
                }).AsTask(cancellationToken).ConfigureAwait(false);

                if (extra != null)
                {
                    if (extra.TryGetValue("System.Subject", out var subj) && subj is string subjStr && !string.IsNullOrWhiteSpace(subjStr))
                    {
                        context.AddItem("Document", "📄", "Subject", subjStr, isCopyable: true);
                    }
                    if (extra.TryGetValue("System.Document.Producer", out var prod) && prod is string prodStr && !string.IsNullOrWhiteSpace(prodStr))
                    {
                        context.AddItem("Document", "📄", "Producer", prodStr, isCopyable: true);
                    }
                    else if (extra.TryGetValue("System.ApplicationName", out var app) && app is string appStr && !string.IsNullOrWhiteSpace(appStr))
                    {
                        context.AddItem("Document", "📄", "Application", appStr, isCopyable: true);
                    }
                }
            }
            catch { }
        }
    }

    private Task ExtractOfficeOpenXmlMetadataAsync(MetadataExtractionContext context, string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

                // 1. docProps/core.xml (Dublin Core metadata)
                var coreEntry = zip.GetEntry("docProps/core.xml");
                if (coreEntry != null)
                {
                    using var coreStream = coreEntry.Open();
                    var doc = XDocument.Load(coreStream);
                    var root = doc.Root;
                    if (root != null)
                    {
                        XNamespace dc = "http://purl.org/dc/elements/1.1/";
                        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";

                        string? title = root.Element(dc + "title")?.Value;
                        string? creator = root.Element(dc + "creator")?.Value;
                        string? lastModifiedBy = root.Element(cp + "lastModifiedBy")?.Value;
                        string? revision = root.Element(cp + "revision")?.Value;

                        if (!string.IsNullOrWhiteSpace(title))
                            context.AddItem("Document", "📄", "Title", title, isCopyable: true);
                        if (!string.IsNullOrWhiteSpace(creator))
                            context.AddItem("Document", "📄", "Author", creator, isCopyable: true);
                        if (!string.IsNullOrWhiteSpace(lastModifiedBy) && !string.Equals(lastModifiedBy, creator, StringComparison.OrdinalIgnoreCase))
                            context.AddItem("Document", "📄", "Last Edited By", lastModifiedBy, isCopyable: true);
                        if (!string.IsNullOrWhiteSpace(revision))
                            context.AddItem("Document", "📄", "Revision", revision, isCopyable: true, isMonospace: true);
                    }
                }

                // 2. docProps/app.xml (Application metadata)
                var appEntry = zip.GetEntry("docProps/app.xml");
                if (appEntry != null)
                {
                    using var appStream = appEntry.Open();
                    var doc = XDocument.Load(appStream);
                    var root = doc.Root;
                    if (root != null)
                    {
                        XNamespace ns = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

                        string? app = root.Element(ns + "Application")?.Value;
                        string? pages = root.Element(ns + "Pages")?.Value;
                        string? words = root.Element(ns + "Words")?.Value;
                        string? lines = root.Element(ns + "Lines")?.Value;
                        string? paragraphs = root.Element(ns + "Paragraphs")?.Value;

                        if (!string.IsNullOrWhiteSpace(pages) && int.TryParse(pages, out var p) && p > 0)
                            context.AddItem("Document", "📄", "Pages", $"{p:N0}", isCopyable: true, isMonospace: true);
                        if (!string.IsNullOrWhiteSpace(words) && int.TryParse(words, out var w) && w > 0)
                            context.AddItem("Document", "📄", "Words", $"{w:N0}", isCopyable: true, isMonospace: true);
                        if (!string.IsNullOrWhiteSpace(lines) && int.TryParse(lines, out var l) && l > 0)
                            context.AddItem("Document", "📄", "Lines", $"{l:N0}", isCopyable: true, isMonospace: true);
                        if (!string.IsNullOrWhiteSpace(paragraphs) && int.TryParse(paragraphs, out var pg) && pg > 0)
                            context.AddItem("Document", "📄", "Paragraphs", $"{pg:N0}", isCopyable: true, isMonospace: true);
                        if (!string.IsNullOrWhiteSpace(app))
                            context.AddItem("Document", "📄", "Application", app, isCopyable: true);
                    }
                }
            }
            catch { }
        }, cancellationToken);
    }
}
