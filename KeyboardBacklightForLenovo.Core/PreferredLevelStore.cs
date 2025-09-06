using System;
using System.IO;

namespace KeyboardBacklightForLenovo
{
  public static class PreferredLevelStore
  {
    private static readonly string DirPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "KeyboardBacklightForLenovo");
    private static readonly string FilePath = Path.Combine(DirPath, "PreferredLevel.txt");

    /// <summary>
    /// Full path to the on-disk preference file.
    /// </summary>
    public static string StorePath => FilePath;

    /// <summary>
    /// Returns 0,1,2 with default of 2 (High) if missing/invalid.
    /// </summary>
    public static int ReadPreferredLevel()
    {
      try
      {
        if (File.Exists(FilePath))
        {
          string text = File.ReadAllText(FilePath).Trim();
          if (int.TryParse(text, out int i) && i >= 0 && i <= 2)
            return i;
        }
      }
      catch
      {
        // ignore, fall back to default
      }
      return 2; // default High
    }

    /// <summary>
    /// Saves 0,1,2 (clamped) to ProgramData file.
    /// </summary>
    public static void SavePreferredLevel(int level)
    {
      if (level < 0) level = 0;
      if (level > 2) level = 2;

      try
      {
        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath, level.ToString());
      }
      catch
      {
        // swallow errors; preference persistence is not critical
      }
    }
  }
}
