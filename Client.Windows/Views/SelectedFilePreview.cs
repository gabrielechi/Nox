using System.IO;
using Wpf.Ui.Controls;

namespace Client.Windows.Views;

internal sealed record SelectedFilePreview(string Name, string Size)
{
    public SymbolRegular Icon => Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".pdf" => SymbolRegular.DocumentPdf24,
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".svg" or ".bmp" or ".ico" => SymbolRegular.Image24,
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" => SymbolRegular.Video24,
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" => SymbolRegular.MusicNote124,
        ".zip" or ".rar" or ".7z" or ".gz" or ".tar" => SymbolRegular.Archive24,
        ".csv" or ".xlsx" or ".xls" or ".ods" => SymbolRegular.Table24,
        ".cs" or ".js" or ".py" or ".html" or ".json" or ".xml" => SymbolRegular.Code24,
        ".txt" or ".md" or ".doc" or ".docx" or ".odt" => SymbolRegular.DocumentText24,
        _ => SymbolRegular.Document24
    };
}
