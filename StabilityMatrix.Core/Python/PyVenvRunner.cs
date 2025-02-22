﻿using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using Salaros.Configuration;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Processes;
using Yoh.Text.Json.NamingPolicies;

namespace StabilityMatrix.Core.Python;

/// <summary>
/// Python runner using a subprocess, mainly for venv support.
/// </summary>
public class PyVenvRunner : IDisposable, IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Relative path to the site-packages folder from the venv root.
    /// This is platform specific.
    /// </summary>
    public static string RelativeSitePackagesPath =>
        Compat.Switch(
            (PlatformKind.Windows, "Lib/site-packages"),
            (PlatformKind.Unix, "lib/python3.10/site-packages")
        );

    /// <summary>
    /// The process running the python executable.
    /// </summary>
    public AnsiProcess? Process { get; private set; }

    /// <summary>
    /// The path to the venv root directory.
    /// </summary>
    public DirectoryPath RootPath { get; }

    /// <summary>
    /// Optional working directory for the python process.
    /// </summary>
    public DirectoryPath? WorkingDirectory { get; set; }

    /// <summary>
    /// Optional environment variables for the python process.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Name of the python binary folder.
    /// 'Scripts' on Windows, 'bin' on Unix.
    /// </summary>
    public static string RelativeBinPath =>
        Compat.Switch((PlatformKind.Windows, "Scripts"), (PlatformKind.Unix, "bin"));

    /// <summary>
    /// The relative path to the python executable.
    /// </summary>
    public static string RelativePythonPath =>
        Compat.Switch(
            (PlatformKind.Windows, Path.Combine("Scripts", "python.exe")),
            (PlatformKind.Unix, Path.Combine("bin", "python3"))
        );

    /// <summary>
    /// The full path to the python executable.
    /// </summary>
    public FilePath PythonPath => RootPath.JoinFile(RelativePythonPath);

    /// <summary>
    /// The relative path to the pip executable.
    /// </summary>
    public static string RelativePipPath =>
        Compat.Switch(
            (PlatformKind.Windows, Path.Combine("Scripts", "pip.exe")),
            (PlatformKind.Unix, Path.Combine("bin", "pip3"))
        );

    /// <summary>
    /// The full path to the pip executable.
    /// </summary>
    public FilePath PipPath => RootPath.JoinFile(RelativePipPath);

    /// <summary>
    /// List of substrings to suppress from the output.
    /// When a line contains any of these substrings, it will not be forwarded to callbacks.
    /// A corresponding Info log will be written instead.
    /// </summary>
    public List<string> SuppressOutput { get; } = new() { "fatal: not a git repository" };

    public PyVenvRunner(DirectoryPath path)
    {
        RootPath = path;
    }

    /// <returns>True if the venv has a Scripts\python.exe file</returns>
    public bool Exists() => PythonPath.Exists;

    /// <summary>
    /// Creates a venv at the configured path.
    /// </summary>
    public async Task Setup(
        bool existsOk = false,
        Action<ProcessOutput>? onConsoleOutput = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!existsOk && Exists())
        {
            throw new InvalidOperationException("Venv already exists");
        }

        // Create RootPath if it doesn't exist
        RootPath.Create();

        // Create venv (copy mode if windows)
        var args = new string[]
        {
            "-m",
            "virtualenv",
            Compat.IsWindows ? "--always-copy" : "",
            RootPath
        };

        var venvProc = ProcessRunner.StartAnsiProcess(
            PyRunner.PythonExePath,
            args,
            WorkingDirectory?.FullPath,
            onConsoleOutput
        );

        try
        {
            await venvProc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Check return code
            if (venvProc.ExitCode != 0)
            {
                throw new ProcessException($"Venv creation failed with code {venvProc.ExitCode}");
            }
        }
        catch (OperationCanceledException)
        {
            venvProc.CancelStreamReaders();
        }
        finally
        {
            venvProc.Kill();
            venvProc.Dispose();
        }
    }

    /// <summary>
    /// Set current python path to pyvenv.cfg
    /// This should be called before using the venv, in case user moves the venv directory.
    /// </summary>
    private void SetPyvenvCfg(string pythonDirectory)
    {
        // Skip if we are not created yet
        if (!Exists())
            return;

        // Path to pyvenv.cfg
        var cfgPath = Path.Combine(RootPath, "pyvenv.cfg");
        if (!File.Exists(cfgPath))
        {
            throw new FileNotFoundException("pyvenv.cfg not found", cfgPath);
        }

        Logger.Info("Updating pyvenv.cfg with embedded Python directory {PyDir}", pythonDirectory);

        // Insert a top section
        var topSection = "[top]" + Environment.NewLine;
        var cfg = new ConfigParser(topSection + File.ReadAllText(cfgPath));

        // Need to set all path keys - home, base-prefix, base-exec-prefix, base-executable
        cfg.SetValue("top", "home", pythonDirectory);
        cfg.SetValue("top", "base-prefix", pythonDirectory);

        cfg.SetValue("top", "base-exec-prefix", pythonDirectory);

        cfg.SetValue(
            "top",
            "base-executable",
            Path.Combine(pythonDirectory, Compat.IsWindows ? "python.exe" : RelativePythonPath)
        );

        // Convert to string for writing, strip the top section
        var cfgString = cfg.ToString()!.Replace(topSection, "");
        File.WriteAllText(cfgPath, cfgString);
    }

    /// <summary>
    /// Run a pip install command. Waits for the process to exit.
    /// workingDirectory defaults to RootPath.
    /// </summary>
    public async Task PipInstall(ProcessArgs args, Action<ProcessOutput>? outputDataReceived = null)
    {
        if (!File.Exists(PipPath))
        {
            throw new FileNotFoundException("pip not found", PipPath);
        }

        // Record output for errors
        var output = new StringBuilder();

        var outputAction = new Action<ProcessOutput>(s =>
        {
            Logger.Debug($"Pip output: {s.Text}");
            // Record to output
            output.Append(s.Text);
            // Forward to callback
            outputDataReceived?.Invoke(s);
        });

        SetPyvenvCfg(PyRunner.PythonDir);
        RunDetached(args.Prepend("-m pip install"), outputAction);
        await Process.WaitForExitAsync().ConfigureAwait(false);

        // Check return code
        if (Process.ExitCode != 0)
        {
            throw new ProcessException(
                $"pip install failed with code {Process.ExitCode}: {output.ToString().ToRepr()}"
            );
        }
    }

    /// <summary>
    /// Run a pip uninstall command. Waits for the process to exit.
    /// workingDirectory defaults to RootPath.
    /// </summary>
    public async Task PipUninstall(string args, Action<ProcessOutput>? outputDataReceived = null)
    {
        if (!File.Exists(PipPath))
        {
            throw new FileNotFoundException("pip not found", PipPath);
        }

        // Record output for errors
        var output = new StringBuilder();

        var outputAction = new Action<ProcessOutput>(s =>
        {
            Logger.Debug($"Pip output: {s.Text}");
            // Record to output
            output.Append(s.Text);
            // Forward to callback
            outputDataReceived?.Invoke(s);
        });

        SetPyvenvCfg(PyRunner.PythonDir);
        RunDetached($"-m pip uninstall -y {args}", outputAction);
        await Process.WaitForExitAsync().ConfigureAwait(false);

        // Check return code
        if (Process.ExitCode != 0)
        {
            throw new ProcessException(
                $"pip install failed with code {Process.ExitCode}: {output.ToString().ToRepr()}"
            );
        }
    }

    /// <summary>
    /// Pip install from a requirements.txt file.
    /// </summary>
    public async Task PipInstallFromRequirements(
        FilePath file,
        Action<ProcessOutput>? outputDataReceived = null,
        [StringSyntax(StringSyntaxAttribute.Regex)] string? excludes = null
    )
    {
        var requirementsText = await file.ReadAllTextAsync().ConfigureAwait(false);
        var requirements = requirementsText
            .SplitLines(StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .AsEnumerable();

        if (excludes is not null)
        {
            var excludeRegex = new Regex($"^{excludes}$");

            requirements = requirements.Where(s => !excludeRegex.IsMatch(s));
        }

        var pipArgs = string.Join(' ', requirements);

        Logger.Info("Installing {FileName} ({PipArgs})", file.Name, pipArgs);
        await PipInstall(pipArgs, outputDataReceived).ConfigureAwait(false);
    }

    /// <summary>
    /// Run a pip list command, return results as PipPackageInfo objects.
    /// </summary>
    public async Task<IReadOnlyList<PipPackageInfo>> PipList()
    {
        if (!File.Exists(PipPath))
        {
            throw new FileNotFoundException("pip not found", PipPath);
        }

        SetPyvenvCfg(PyRunner.PythonDir);

        var result = await ProcessRunner
            .GetProcessResultAsync(
                PythonPath,
                "-m pip list --format=json",
                WorkingDirectory?.FullPath,
                EnvironmentVariables
            )
            .ConfigureAwait(false);

        // Check return code
        if (result.ExitCode != 0)
        {
            throw new ProcessException(
                $"pip list failed with code {result.ExitCode}: {result.StandardOutput}, {result.StandardError}"
            );
        }

        // Use only first line, since there might be pip update messages later
        if (
            result.StandardOutput
                ?.SplitLines(StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
            is not { } firstLine
        )
        {
            return new List<PipPackageInfo>();
        }

        return JsonSerializer.Deserialize<List<PipPackageInfo>>(
                firstLine,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicies.SnakeCaseLower
                }
            ) ?? new List<PipPackageInfo>();
    }

    /// <summary>
    /// Run a pip show command, return results as PipPackageInfo objects.
    /// </summary>
    public async Task<PipShowResult?> PipShow(string packageName)
    {
        if (!File.Exists(PipPath))
        {
            throw new FileNotFoundException("pip not found", PipPath);
        }

        SetPyvenvCfg(PyRunner.PythonDir);

        var result = await ProcessRunner
            .GetProcessResultAsync(
                PythonPath,
                new[] { "-m", "pip", "show", packageName },
                WorkingDirectory?.FullPath,
                EnvironmentVariables
            )
            .ConfigureAwait(false);

        // Check return code
        if (result.ExitCode != 0)
        {
            throw new ProcessException(
                $"pip show failed with code {result.ExitCode}: {result.StandardOutput}, {result.StandardError}"
            );
        }

        if (result.StandardOutput!.StartsWith("WARNING: Package(s) not found:"))
        {
            return null;
        }

        return PipShowResult.Parse(result.StandardOutput);
    }

    /// <summary>
    /// Run a pip index command, return result as PipIndexResult.
    /// </summary>
    public async Task<PipIndexResult?> PipIndex(string packageName, string? indexUrl = null)
    {
        if (!File.Exists(PipPath))
        {
            throw new FileNotFoundException("pip not found", PipPath);
        }

        SetPyvenvCfg(PyRunner.PythonDir);

        var args = new ProcessArgsBuilder(
            "-m",
            "pip",
            "index",
            "versions",
            packageName,
            "--no-color",
            "--disable-pip-version-check"
        );

        if (indexUrl is not null)
        {
            args = args.AddArg(("--index-url", indexUrl));
        }

        var result = await ProcessRunner
            .GetProcessResultAsync(
                PythonPath,
                args,
                WorkingDirectory?.FullPath,
                EnvironmentVariables
            )
            .ConfigureAwait(false);

        // Check return code
        if (result.ExitCode != 0)
        {
            throw new ProcessException(
                $"pip index failed with code {result.ExitCode}: {result.StandardOutput}, {result.StandardError}"
            );
        }

        if (
            string.IsNullOrEmpty(result.StandardOutput)
            || result.StandardOutput!
                .SplitLines()
                .Any(l => l.StartsWith("ERROR: No matching distribution found"))
        )
        {
            return null;
        }

        return PipIndexResult.Parse(result.StandardOutput);
    }

    /// <summary>
    /// Run a custom install command. Waits for the process to exit.
    /// workingDirectory defaults to RootPath.
    /// </summary>
    public async Task CustomInstall(string args, Action<ProcessOutput>? outputDataReceived = null)
    {
        // Record output for errors
        var output = new StringBuilder();

        var outputAction =
            outputDataReceived == null
                ? null
                : new Action<ProcessOutput>(s =>
                {
                    Logger.Debug($"Install output: {s.Text}");
                    // Record to output
                    output.Append(s.Text);
                    // Forward to callback
                    outputDataReceived(s);
                });

        SetPyvenvCfg(PyRunner.PythonDir);
        RunDetached(args, outputAction);
        await Process.WaitForExitAsync().ConfigureAwait(false);

        // Check return code
        if (Process.ExitCode != 0)
        {
            throw new ProcessException(
                $"install script failed with code {Process.ExitCode}: {output.ToString().ToRepr()}"
            );
        }
    }

    /// <summary>
    /// Run a command using the venv Python executable and return the result.
    /// </summary>
    /// <param name="arguments">Arguments to pass to the Python executable.</param>
    public async Task<ProcessResult> Run(ProcessArgs arguments)
    {
        // Record output for errors
        var output = new StringBuilder();

        var outputAction = new Action<string?>(s =>
        {
            if (s == null)
                return;
            Logger.Debug("Pip output: {Text}", s);
            output.Append(s);
        });

        SetPyvenvCfg(PyRunner.PythonDir);
        using var process = ProcessRunner.StartProcess(
            PythonPath,
            arguments,
            WorkingDirectory?.FullPath,
            outputAction,
            EnvironmentVariables
        );
        await process.WaitForExitAsync().ConfigureAwait(false);

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = output.ToString()
        };
    }

    [MemberNotNull(nameof(Process))]
    public void RunDetached(
        ProcessArgs args,
        Action<ProcessOutput>? outputDataReceived,
        Action<int>? onExit = null,
        bool unbuffered = true
    )
    {
        var arguments = args.ToString();

        if (!PythonPath.Exists)
        {
            throw new FileNotFoundException("Venv python not found", PythonPath);
        }
        SetPyvenvCfg(PyRunner.PythonDir);

        Logger.Info(
            "Launching venv process [{PythonPath}] "
                + "in working directory [{WorkingDirectory}] with args {Arguments}",
            PythonPath,
            WorkingDirectory,
            arguments
        );

        var filteredOutput =
            outputDataReceived == null
                ? null
                : new Action<ProcessOutput>(s =>
                {
                    if (SuppressOutput.Any(s.Text.Contains))
                    {
                        Logger.Info("Filtered output: {S}", s);
                        return;
                    }
                    outputDataReceived.Invoke(s);
                });

        var env = new Dictionary<string, string>();
        if (EnvironmentVariables != null)
        {
            env.Update(EnvironmentVariables);
        }

        // Disable pip caching - uses significant memory for large packages like torch
        // env["PIP_NO_CACHE_DIR"] = "true";

        // On windows, add portable git to PATH and binary as GIT
        if (Compat.IsWindows)
        {
            var portableGitBin = GlobalConfig.LibraryDir.JoinDir("PortableGit", "bin");
            env["PATH"] = Compat.GetEnvPathWithExtensions(portableGitBin);
            env["GIT"] = portableGitBin.JoinFile("git.exe");
        }

        if (unbuffered)
        {
            env["PYTHONUNBUFFERED"] = "1";

            // If arguments starts with -, it's a flag, insert `u` after it for unbuffered mode
            if (arguments.StartsWith('-'))
            {
                arguments = arguments.Insert(1, "u");
            }
            // Otherwise insert -u at the beginning
            else
            {
                arguments = "-u " + arguments;
            }
        }

        Process = ProcessRunner.StartAnsiProcess(
            PythonPath,
            arguments,
            workingDirectory: WorkingDirectory?.FullPath,
            outputDataReceived: filteredOutput,
            environmentVariables: env
        );

        if (onExit != null)
        {
            Process.EnableRaisingEvents = true;
            Process.Exited += (sender, _) =>
            {
                onExit((sender as AnsiProcess)?.ExitCode ?? -1);
            };
        }
    }

    /// <summary>
    /// Get entry points for a package.
    /// https://packaging.python.org/en/latest/specifications/entry-points/#entry-points
    /// </summary>
    public async Task<string?> GetEntryPoint(string entryPointName)
    {
        // ReSharper disable once StringLiteralTypo
        var code = $"""
                   from importlib.metadata import entry_points
                   
                   results = entry_points(group='console_scripts', name='{entryPointName}')
                   print(tuple(results)[0].value, end='')
                   """;

        var result = await Run($"-c \"{code}\"").ConfigureAwait(false);
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput;
        }

        return null;
    }

    /// <summary>
    /// Kills the running process and cancels stream readers, does not wait for exit.
    /// </summary>
    public void Dispose()
    {
        if (Process is not null)
        {
            Process.CancelStreamReaders();
            Process.Kill(true);
            Process.Dispose();
        }

        Process = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Kills the running process, waits for exit.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Process is { HasExited: false })
        {
            Process.Kill(true);
            try
            {
                await Process
                    .WaitForExitAsync(new CancellationTokenSource(5000).Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException e)
            {
                Logger.Warn(e, "Venv Process did not exit in time in DisposeAsync");

                Process.CancelStreamReaders();
            }
        }

        Process = null;
        GC.SuppressFinalize(this);
    }

    ~PyVenvRunner()
    {
        Dispose();
    }
}
