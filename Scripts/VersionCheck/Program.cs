// Calls AppUpdate.IsNewerThan in the real compiled Amuse.App.dll.
//
// The Python mirror in test-version-check.py covers the same table, but a mirror
// can drift from the code it mirrors. This one exercises the shipped assembly.
using System;
using System.IO;
using System.Reflection;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        string repo = FindRepoRoot();
        string dll = Path.Combine(repo, "Amuse.App", "bin", "Release",
                                  "net10.0-windows10.0.17763.0", "Amuse.dll");

        if (!File.Exists(dll))
        {
            Console.WriteLine($"  FAIL  Amuse.dll not found at {dll}");
            Console.WriteLine("        build Amuse.App -c Release first");
            return 1;
        }

        Assembly asm = Assembly.LoadFrom(dll);
        Type type = asm.GetType("Amuse.App.AppUpdate", throwOnError: false);
        if (type is null)
        {
            Console.WriteLine("  FAIL  Amuse.App.AppUpdate not found in " + dll);
            return 1;
        }

        MethodInfo method = type.GetMethod("IsNewerThan",
            BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            Console.WriteLine("  FAIL  AppUpdate.IsNewerThan not found");
            return 1;
        }

        Console.WriteLine($"=== Amuse.App.AppUpdate.IsNewerThan ({dll}) ===");

        // tag_name from saddam213/AmuseAI latest, and this tree's Directory.Build.props
        Check(method, "v3.8.1", "v3.8.2", false, "tree is ahead of the release: must not offer a downgrade");
        Check(method, "v3.8.2", "v3.8.2", false, "same version");
        Check(method, "v3.8.3", "v3.8.2", true, "genuinely newer patch");
        Check(method, "v4.0.0", "v3.8.2", true, "genuinely newer major");
        Check(method, "v3.9.0", "v3.8.2", true, "genuinely newer minor");
        Check(method, "3.8.1", "3.8.2", false, "tags without the v prefix");
        Check(method, "v3.8.1", "3.8.10", false, "3.8.1 is older than 3.8.10");
        Check(method, "v3.8.10", "v3.8.9", true, "3.8.10 is newer than 3.8.9");
        Check(method, "", "v3.8.2", false, "empty tag");
        Check(method, null, "v3.8.2", false, "null tag");
        Check(method, "garbage", "v3.8.2", false, "unparsable tag");
        Check(method, "v3.8.2", "garbage", false, "unparsable current version");

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(MethodInfo method, string candidate, string current,
                              bool expected, string why)
    {
        bool got;
        try
        {
            got = (bool)method.Invoke(null, new object[] { candidate, current });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  IsNewerThan({Show(candidate)}, {Show(current)}) threw {ex.GetType().Name}");
            _failures++;
            return;
        }

        bool ok = got == expected;
        if (!ok) _failures++;
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  IsNewerThan({Show(candidate)}, {Show(current)})" +
                          $" -> {got,-5}  {why}");
    }

    private static string Show(string s) => s is null ? "null" : $"\"{s}\"";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Amuse.App", "Amuse.App.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate the Amuse repository root");
    }
}
