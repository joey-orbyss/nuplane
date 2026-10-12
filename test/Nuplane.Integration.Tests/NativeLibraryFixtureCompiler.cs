using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nuplane.Integration.Tests;

/// <summary>Compiles the tiny package-owned native export used by the deferred-load lifetime proof.</summary>
internal static class NativeLibraryFixtureCompiler
{
    internal const string LibraryName = "NuplaneDeferredNativeProbe";
    internal const string ExportName = "NuplaneDeferredNativeProbe";
    internal const string MethodName = "InvokeDeferredNativeProbe";
    internal const int InputValue = 137;
    internal const int ExpectedValue = (InputValue * 37) + 0x51A3;

    private static readonly TimeSpan CompilerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessExitGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    private const string Source = """
        #if defined(_WIN32)
        #define NUPLANE_EXPORT __declspec(dllexport)
        #define NUPLANE_CALL __cdecl
        #else
        #define NUPLANE_EXPORT __attribute__((visibility("default")))
        #define NUPLANE_CALL
        #endif

        NUPLANE_EXPORT int NUPLANE_CALL NuplaneDeferredNativeProbe(int value)
        {
            return (value * 37) + 0x51A3;
        }
        """;

    internal static string CompileInto(string nativeDirectory)
    {
        Directory.CreateDirectory(nativeDirectory);
        var output = Path.Combine(nativeDirectory, OutputFileName());
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nuplane-t086-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var sourcePath = Path.Combine(temporaryDirectory, "nuplane_deferred_native_probe.c");
            File.WriteAllText(sourcePath, Source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (OperatingSystem.IsWindows())
                CompileWithMsvc(sourcePath, output, temporaryDirectory);
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                CompileWithPosixCompiler(sourcePath, output, temporaryDirectory);
            else
                throw new PlatformNotSupportedException(
                    $"The T086 native-library lifetime fixture has no compiler setup for {RuntimeInformation.OSDescription}.");

            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new Xunit.Sdk.XunitException($"Native compiler returned success without creating '{output}'.");
            return output;
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CompileWithPosixCompiler(string sourcePath, string output, string workingDirectory)
    {
        var compiler = FindCompiler();
        var machine = Run(compiler, ["-dumpmachine"], workingDirectory);
        EnsureCompilerArchitecture(compiler, machine.StandardOutput + machine.StandardError);

        var arguments = new List<string> { "-O2", "-fPIC" };
        if (OperatingSystem.IsMacOS())
        {
            arguments.Add("-dynamiclib");
            arguments.Add("-arch");
            arguments.Add(RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x86_64",
                var architecture => throw new PlatformNotSupportedException(
                    $"Native fixture compilation is unsupported for macOS process architecture {architecture}.")
            });
        }
        else
        {
            arguments.Add("-shared");
        }

        arguments.Add(sourcePath);
        arguments.Add("-o");
        arguments.Add(output);
        Run(compiler, arguments, workingDirectory);
    }

    private static string FindCompiler()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("CC"), "clang", "cc", "gcc" }
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate!) && File.Exists(candidate))
                return candidate!;
            if (FindOnPath(candidate!) is { } found)
                return found;
        }

        throw new Xunit.Sdk.XunitException(
            $"Could not find a POSIX C compiler for the T086 native-library lifetime proof. Checked: {string.Join(", ", candidates)}.");
    }

    private static void EnsureCompilerArchitecture(string compiler, string machineOutput)
    {
        var machine = machineOutput.Trim();
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => new[] { "aarch64", "arm64" },
            Architecture.X64 => new[] { "x86_64", "amd64" },
            Architecture.X86 => new[] { "i386", "i486", "i586", "i686" },
            _ => throw new PlatformNotSupportedException(
                $"Native fixture compilation is unsupported for process architecture {RuntimeInformation.ProcessArchitecture}.")
        };
        if (!expected.Any(token => machine.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            throw new Xunit.Sdk.XunitException(
                $"C compiler '{compiler}' reports target '{machine}', which does not match executing process architecture " +
                $"'{RuntimeInformation.ProcessArchitecture}'. Refusing to compile a mismatched native fixture.");
        }
    }

    private static void CompileWithMsvc(string sourcePath, string output, string workingDirectory)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                $"The Windows native fixture is configured for the current x64 runner; process architecture is {RuntimeInformation.ProcessArchitecture}.");

        var vswhere = LocateVsWhere();
        var installation = Run(vswhere,
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            workingDirectory).StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(installation) || !Directory.Exists(installation))
            throw new Xunit.Sdk.XunitException(
                $"vswhere '{vswhere}' did not find a Visual Studio installation with the x64 C++ tools required by the T086 native proof.");

        var vsDevCmd = Path.Combine(installation, "Common7", "Tools", "VsDevCmd.bat");
        if (!File.Exists(vsDevCmd))
            throw new Xunit.Sdk.XunitException($"Visual Studio developer environment script was not found: '{vsDevCmd}'.");

        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        File.WriteAllText(Path.Combine(workingDirectory, "initialize-vs-environment.cmd"),
            $"@echo off\r\ncall \"{vsDevCmd}\" -arch=x64 -host_arch=x64 >nul\r\n" +
            "if errorlevel 1 exit /b %errorlevel%\r\nset\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var environmentOutput = Run(commandInterpreter,
            ["/d", "/c", "initialize-vs-environment.cmd"], workingDirectory);
        var environment = ParseEnvironment(environmentOutput.StandardOutput);
        var compiler = FindOnPath("cl.exe", environment["PATH"]);
        if (compiler is null)
            throw new Xunit.Sdk.XunitException("VsDevCmd's initialized PATH does not contain cl.exe.");
        var arguments = new[] { "/nologo", "/LD", "/O2", "/TC", sourcePath, "/link", $"/OUT:{output}" };
        Run(compiler, arguments, workingDirectory, environment);
    }

    private static string LocateVsWhere()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidate = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(candidate))
            return candidate;

        throw new Xunit.Sdk.XunitException(
            $"Could not locate vswhere.exe at the standard Visual Studio Installer path '{candidate}'.");
    }

    private static Dictionary<string, string> ParseEnvironment(string output)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || line[0] == '=')
                continue;
            environment[line[..separator]] = line[(separator + 1)..];
        }

        if (!environment.TryGetValue("PATH", out var path) || string.IsNullOrWhiteSpace(path))
            throw new Xunit.Sdk.XunitException("VsDevCmd did not provide PATH for cl.exe.");
        return environment;
    }

    private static string OutputFileName() => OperatingSystem.IsWindows()
        ? LibraryName + ".dll"
        : OperatingSystem.IsMacOS()
            ? "lib" + LibraryName + ".dylib"
            : "lib" + LibraryName + ".so";

    private static string? FindOnPath(string executable, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static ProcessResult Run(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var item in environment)
                startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        var display = FormatCommand(executable, argumentList);
        try
        {
            if (!process.Start())
                throw new Xunit.Sdk.XunitException($"Could not start native compiler command: {display}");
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Could not start native compiler command '{display}': {exception.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            process.WaitForExitAsync().WaitAsync(CompilerTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try
            {
                process.WaitForExitAsync().WaitAsync(ProcessExitGracePeriod).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Native compiler exceeded {CompilerTimeout.TotalSeconds:0}-second limit and did not exit within " +
                    $"{ProcessExitGracePeriod.TotalSeconds:0} seconds after termination: {display}");
            }

            var (stdout, stderr) = ReadOutput(stdoutTask, stderrTask, OutputDrainTimeout, display);
            throw new Xunit.Sdk.XunitException(
                $"Native compiler exceeded {CompilerTimeout.TotalSeconds:0}-second limit: {display}\n{stdout}\n{stderr}");
        }

        var (standardOutput, standardError) = ReadOutput(stdoutTask, stderrTask, OutputDrainTimeout, display);
        if (process.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"Native compiler exited with code {process.ExitCode}: {display}\n{standardOutput}\n{standardError}");
        }

        return new ProcessResult(standardOutput, standardError);
    }

    private static (string StandardOutput, string StandardError) ReadOutput(
        Task<string> stdoutTask,
        Task<string> stderrTask,
        TimeSpan timeout,
        string display)
    {
        try
        {
            Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"Timed out draining native compiler output within {timeout.TotalSeconds:0} seconds: {display}");
        }

        return (stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static string FormatCommand(string executable, IReadOnlyList<string> arguments)
        => string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}
