namespace GRAR.Models;

public enum FileCategory
{
    Folder,
    Archive,
    Text,
    Image,
    Audio,
    Video,
    Code,
    Document,
    Application,
    Disk,
    Home,
    File,
}

/// <summary>Maps file extensions to the human readable type name and icon used in the list.</summary>
public static class FileTypes
{
    private static readonly Dictionary<string, (string Name, FileCategory Category)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = ("Text document", FileCategory.Text),
        [".log"] = ("Log file", FileCategory.Text),
        [".md"] = ("Markdown document", FileCategory.Text),
        [".csv"] = ("CSV document", FileCategory.Text),
        [".rtf"] = ("Rich text document", FileCategory.Text),
        [".nfo"] = ("Info file", FileCategory.Text),
        [".ini"] = ("Configuration file", FileCategory.Text),
        [".cfg"] = ("Configuration file", FileCategory.Text),
        [".conf"] = ("Configuration file", FileCategory.Text),
        [".yml"] = ("YAML document", FileCategory.Text),
        [".yaml"] = ("YAML document", FileCategory.Text),
        [".xml"] = ("XML document", FileCategory.Text),
        [".json"] = ("JSON document", FileCategory.Text),

        [".png"] = ("PNG image", FileCategory.Image),
        [".jpg"] = ("JPEG image", FileCategory.Image),
        [".jpeg"] = ("JPEG image", FileCategory.Image),
        [".gif"] = ("GIF image", FileCategory.Image),
        [".bmp"] = ("Bitmap image", FileCategory.Image),
        [".svg"] = ("SVG image", FileCategory.Image),
        [".webp"] = ("WebP image", FileCategory.Image),
        [".ico"] = ("Icon file", FileCategory.Image),
        [".tif"] = ("TIFF image", FileCategory.Image),
        [".tiff"] = ("TIFF image", FileCategory.Image),

        [".mp3"] = ("MP3 audio", FileCategory.Audio),
        [".wav"] = ("WAV audio", FileCategory.Audio),
        [".flac"] = ("FLAC audio", FileCategory.Audio),
        [".ogg"] = ("Ogg audio", FileCategory.Audio),
        [".m4a"] = ("M4A audio", FileCategory.Audio),
        [".opus"] = ("Opus audio", FileCategory.Audio),

        [".mp4"] = ("MP4 video", FileCategory.Video),
        [".mkv"] = ("Matroska video", FileCategory.Video),
        [".avi"] = ("AVI video", FileCategory.Video),
        [".mov"] = ("QuickTime video", FileCategory.Video),
        [".wmv"] = ("Windows video", FileCategory.Video),
        [".webm"] = ("WebM video", FileCategory.Video),

        [".cs"] = ("C# source file", FileCategory.Code),
        [".fs"] = ("F# source file", FileCategory.Code),
        [".vb"] = ("VB source file", FileCategory.Code),
        [".js"] = ("JavaScript file", FileCategory.Code),
        [".ts"] = ("TypeScript file", FileCategory.Code),
        [".jsx"] = ("JavaScript JSX file", FileCategory.Code),
        [".tsx"] = ("TypeScript TSX file", FileCategory.Code),
        [".py"] = ("Python script", FileCategory.Code),
        [".rb"] = ("Ruby script", FileCategory.Code),
        [".go"] = ("Go source file", FileCategory.Code),
        [".rs"] = ("Rust source file", FileCategory.Code),
        [".java"] = ("Java source file", FileCategory.Code),
        [".c"] = ("C source file", FileCategory.Code),
        [".h"] = ("C header file", FileCategory.Code),
        [".cpp"] = ("C++ source file", FileCategory.Code),
        [".hpp"] = ("C++ header file", FileCategory.Code),
        [".sh"] = ("Shell script", FileCategory.Code),
        [".ps1"] = ("PowerShell script", FileCategory.Code),
        [".bat"] = ("Batch file", FileCategory.Code),
        [".html"] = ("HTML document", FileCategory.Code),
        [".htm"] = ("HTML document", FileCategory.Code),
        [".css"] = ("Stylesheet", FileCategory.Code),
        [".sql"] = ("SQL script", FileCategory.Code),

        [".pdf"] = ("PDF document", FileCategory.Document),
        [".doc"] = ("Word document", FileCategory.Document),
        [".docx"] = ("Word document", FileCategory.Document),
        [".xls"] = ("Excel spreadsheet", FileCategory.Document),
        [".xlsx"] = ("Excel spreadsheet", FileCategory.Document),
        [".ppt"] = ("Presentation", FileCategory.Document),
        [".pptx"] = ("Presentation", FileCategory.Document),
        [".odt"] = ("OpenDocument text", FileCategory.Document),
        [".ods"] = ("OpenDocument spreadsheet", FileCategory.Document),

        [".exe"] = ("Application", FileCategory.Application),
        [".msi"] = ("Windows installer", FileCategory.Application),
        [".dll"] = ("Library", FileCategory.Application),
        [".so"] = ("Shared library", FileCategory.Application),
        [".bin"] = ("Binary file", FileCategory.Application),
        [".deb"] = ("Debian package", FileCategory.Application),
        [".rpm"] = ("RPM package", FileCategory.Application),
        [".apk"] = ("Android package", FileCategory.Application),

        [".zip"] = ("ZIP archive", FileCategory.Archive),
        [".rar"] = ("RAR archive", FileCategory.Archive),
        [".7z"] = ("7-Zip archive", FileCategory.Archive),
        [".tar"] = ("TAR archive", FileCategory.Archive),
        [".gz"] = ("GZip archive", FileCategory.Archive),
        [".tgz"] = ("GZip archive", FileCategory.Archive),
        [".bz2"] = ("BZip2 archive", FileCategory.Archive),
        [".xz"] = ("XZ archive", FileCategory.Archive),
        [".iso"] = ("Disc image", FileCategory.Archive),
        [".cab"] = ("Cabinet archive", FileCategory.Archive),
        [".z"] = ("Compressed file", FileCategory.Archive),
        [".lz"] = ("LZip archive", FileCategory.Archive),
        [".lzma"] = ("LZMA archive", FileCategory.Archive),
        [".zst"] = ("Zstandard archive", FileCategory.Archive),
    };

    public static (string Name, FileCategory Category) Describe(string extension)
    {
        if (Map.TryGetValue(extension, out var value))
            return value;

        var ext = extension.StartsWith('.') ? extension[1..] : extension;
        return ext.Length == 0
            ? ("File", FileCategory.File)
            : (ext.ToUpperInvariant() + " file", FileCategory.File);
    }

    public static bool IsArchiveExtension(string extension)
        => Map.TryGetValue(extension, out var v) && v.Category == FileCategory.Archive
           && !string.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase);

    public static string IconKey(FileCategory category) => category switch
    {
        FileCategory.Folder => "icon.folder",
        FileCategory.Archive => "icon.archive",
        FileCategory.Text => "icon.file.text",
        FileCategory.Image => "icon.file.image",
        FileCategory.Audio => "icon.file.audio",
        FileCategory.Video => "icon.file.video",
        FileCategory.Code => "icon.file.code",
        FileCategory.Document => "icon.file.document",
        FileCategory.Application => "icon.file.app",
        FileCategory.Disk => "icon.drive",
        FileCategory.Home => "icon.home",
        _ => "icon.file",
    };
}
