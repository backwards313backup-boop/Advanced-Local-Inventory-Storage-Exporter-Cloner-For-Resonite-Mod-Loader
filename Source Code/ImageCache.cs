namespace LocalInventoryExport;

internal static class ImageCache
{
    internal const string Folder = ".imagecache";
    internal static readonly string[] Extensions = [".webp", ".png", ".jpg", ".jpeg"];

    internal static string Base(string root, string relativePackage)
    {
        string directory = PathNames.DirectoryOf(relativePackage);
        string name = Path.GetFileNameWithoutExtension(PathNames.Norm(relativePackage));
        return directory.Length == 0 ? Path.Combine(root, Folder, name) : Path.Combine(root, Folder, directory, name);
    }

    internal static string? Find(string root, string relativePackage)
    {
        string target = Base(root, relativePackage);
        foreach (string extension in Extensions)
        {
            if (Usable(target + extension))
                return target + extension;
        }
        return null;
    }

    private static bool Usable(string file)
    {
        try
        {
            FileInfo info = new(file);
            if (!info.Exists)
                return false;

            if (info.Length > 0)
                return true;

            info.Delete();
        }
        catch (Exception)
        {
        }
        return false;
    }

    internal static void CopyInto(string source, string target)
    {
        string part = target + ".part";
        try
        {
            File.Copy(source, part, true);
            File.Move(part, target, true);
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(part))
                    File.Delete(part);
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    internal static void Prepare(string root, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        HideFolder(root);
    }

    private static void HideFolder(string root)
    {
        try
        {
            string folder = Path.Combine(root, Folder);
            if (OperatingSystem.IsWindows() && Directory.Exists(folder))
            {
                FileAttributes attributes = File.GetAttributes(folder);
                if ((attributes & FileAttributes.Hidden) == 0)
                    File.SetAttributes(folder, attributes | FileAttributes.Hidden);
            }
        }
        catch (Exception)
        {
        }
    }
}
