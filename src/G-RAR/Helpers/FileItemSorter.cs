using System.Collections;
using GRAR.Models;

namespace GRAR.Helpers;

/// <summary>Non generic comparer wrapper so the DataGrid sort descriptions can use it.</summary>
public sealed class FileItemComparer : IComparer
{
    private readonly Comparison<FileItem> _comparison;

    public FileItemComparer(string key, bool descending) => _comparison = FileItemSorter.Create(key, descending);

    public int Compare(object? x, object? y)
        => x is FileItem a && y is FileItem b ? _comparison(a, b) : 0;
}

/// <summary>Sorting of file list rows. Directories always come first, like WinRAR does it.</summary>
public static class FileItemSorter
{
    public static Comparison<FileItem> Create(string key, bool descending)
    {
        return (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            var result = Compare(a, b, key);
            return descending ? -result : result;
        };
    }

    private static int Compare(FileItem a, FileItem b, string key) => key switch
    {
        "Size" => a.Size.CompareTo(b.Size),
        "Type" => CompareNames(a.TypeName, b.TypeName),
        "Modified" => NullableCompare(a.Modified, b.Modified),
        "Packed" => a.CompressedSize.CompareTo(b.CompressedSize),
        "Ratio" => a.RatioValue.CompareTo(b.RatioValue),
        "Crc" => a.Crc.CompareTo(b.Crc),
        "Method" => CompareNames(a.Method, b.Method),
        "Attributes" => CompareNames(a.Attributes, b.Attributes),
        _ => CompareNames(a.Name, b.Name),
    };

    private static int CompareNames(string? a, string? b)
        => NaturalComparer.Instance.Compare(a ?? string.Empty, b ?? string.Empty);

    private static int NullableCompare(DateTime? a, DateTime? b)
        => (a ?? DateTime.MinValue).CompareTo(b ?? DateTime.MinValue);
}
