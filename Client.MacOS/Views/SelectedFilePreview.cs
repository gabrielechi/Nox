using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Client.MacOS.Views;

/// <summary>A selected file as shown in the drop area: name, size and a vector icon by type.</summary>
internal sealed record SelectedFilePreview(string Name, string Size)
{
    public Geometry? Icon => Application.Current?.TryFindResource(IconKey, out object? value) == true ? value as Geometry : null;

    private string IconKey => Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".pdf" => "NoxIconFilePdf",
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".svg" or ".bmp" or ".ico" or ".heic" => "NoxIconImage",
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" => "NoxIconVideo",
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" => "NoxIconMusic",
        ".zip" or ".rar" or ".7z" or ".gz" or ".tar" or ".dmg" => "NoxIconArchive",
        ".csv" or ".xlsx" or ".xls" or ".ods" or ".numbers" => "NoxIconTable",
        ".cs" or ".js" or ".py" or ".html" or ".json" or ".xml" => "NoxIconCode",
        ".txt" or ".md" or ".doc" or ".docx" or ".odt" or ".pages" or ".rtf" => "NoxIconFileText",
        _ => "NoxIconFile"
    };
}
