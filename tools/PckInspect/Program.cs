using STS2SkinChanger.Pck;

if (args.Length == 3 && args[0] == "--read")
{
    using var archive = PckArchive.Open(args[1]);
    Console.OpenStandardOutput().Write(archive.ReadFile(args[2]));
    return;
}

if (args.Length >= 4 && args[0] == "--copy")
{
    using var archive = PckArchive.Open(args[1]);
    var files = args.Skip(3).ToDictionary(path => path, path => (archive, path), StringComparer.OrdinalIgnoreCase);
    PckArchive.WriteFromArchives(args[2], files);
    using var copy = PckArchive.Open(args[2]);
    Console.WriteLine($"{copy.Paths.Count} files copied to {args[2]}");
    foreach (var resourcePath in copy.Paths.Order(StringComparer.Ordinal))
    {
        Console.WriteLine($"{resourcePath}\t{copy.GetFileSize(resourcePath)}");
    }
    return;
}

foreach (var path in args)
{
    using var archive = PckArchive.Open(path);
    Console.WriteLine($"{path}\t{archive.Paths.Count} files");
    foreach (var resourcePath in archive.Paths.Order(StringComparer.Ordinal))
    {
        Console.WriteLine(resourcePath);
    }
}
