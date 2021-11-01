using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine;
using Instances;

namespace Konsolidat
{
    class Program
    {
        private static readonly Regex ErrorRegex = new("([A-Z]:\\\\[^:]+) : error NU1605:  ([^[]*)", RegexOptions.Compiled);
        private static readonly Regex PackageReferenceRegex = new("<ItemGroup>(\\s+)<PackageReference", RegexOptions.Compiled);
        private static readonly Regex PackageRegex = new("([a-zA-Z.]+) \\(>= ([0-9.]+)\\)", RegexOptions.Compiled);

        static async Task Main(string[] args)
        {
            await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(Action);
        }

        private static async Task Action(Options options)
        {
            var (exitCode, output) = await RunDotnetBuild(options);
            if (exitCode != 0)
            {
                var detectedDowngrades = ParsePackageVersionConflicts(options, output);

                var solutionDirectory = Path.GetDirectoryName(options.Solution);
                foreach (var projectConflicts in detectedDowngrades.GroupBy(d => d.Project))
                {
                    var csprojFile = Directory.GetFiles(solutionDirectory!, $"{projectConflicts.Key}.csproj", SearchOption.AllDirectories).Single();
                    await ConsolidateProject(csprojFile, projectConflicts);
                }
            }
            else
            {
                Console.WriteLine("Solution built without errors");
            }
        }

        private static async Task ConsolidateProject(string csprojFilePath, IEnumerable<(string Project, string Package, string Version)> projectConflicts)
        {
            var csprojContent = await File.ReadAllTextAsync(csprojFilePath);

            foreach (var conflictPackages in projectConflicts.GroupBy(d => d.Package))
            {
                csprojContent = UpdateProjectDependencies(csprojContent, conflictPackages.ToArray());
            }

            await File.WriteAllTextAsync(csprojFilePath, csprojContent);
            Console.WriteLine($"Project file '{csprojFilePath}' has been updated");
        }

        private static string UpdateProjectDependencies(string csprojContent, ICollection<(string Project, string Package, string Version)> packageConflicts)
        {
            var packageName = packageConflicts.First().Package;
            var projectName = packageConflicts.First().Project;
            
            var newVersion = packageConflicts.Select(s => Version.Parse(s.Version)).OrderByDescending(s => s).First();
            var oldVersion = packageConflicts.Select(s => Version.Parse(s.Version)).OrderByDescending(s => s).Last();
            var oldDependency = $"<PackageReference Include=\"{packageName}\" Version=\"{oldVersion}\" />";
            var newDependency = $"<PackageReference Include=\"{packageName}\" Version=\"{newVersion}\" />";

            var match = PackageReferenceRegex.Match(csprojContent);
            var indentation = match.Groups[1].Value;
            var indentedReference = $"<ItemGroup>{indentation}{newDependency}{indentation}<PackageReference";

            if (csprojContent.Contains(oldDependency))
            {
                csprojContent = csprojContent.Replace(oldDependency, newDependency);
                Console.WriteLine($"Updated reference to package {packageName} from v.{oldVersion} to v.{newVersion} in project {projectName}");
            }
            else
            {
                csprojContent = PackageReferenceRegex.Replace(csprojContent, indentedReference);
                Console.WriteLine($"Added reference to the package {packageName} v.{newVersion} in project {projectName}");
            }

            return csprojContent;
        }

        private static HashSet<(string Project, string Package, string Version)> ParsePackageVersionConflicts(Options options, IEnumerable<string> output)
        {
            var detectedConflicts = new HashSet<(string Project, string Package, string Version)>();
            var namespaceRegex = new Regex(options.NamespaceRegex, RegexOptions.Compiled);
            foreach (var errorLine in output)
            {
                var match = ErrorRegex.Match(errorLine);

                if (!match.Success) continue;

                var chain = match.Groups[2].Value;
                var lastProjectInChain = chain.Split(" -> ").Last(s => namespaceRegex.IsMatch(s));
                var pkgMatch = PackageRegex.Match(chain);

                detectedConflicts.Add((lastProjectInChain, pkgMatch.Groups[1].Value, pkgMatch.Groups[2].Value));
            }

            return detectedConflicts;
        }

        private static async Task<(int exitCode, IReadOnlyList<string> output)> RunDotnetBuild(Options options)
        {
            using var instance = new Instance("dotnet", $"restore \"{options.Solution}\" -r {options.RuntimeIdentifier}");
            instance.DataBufferCapacity = int.MaxValue;
            var exitCode = await instance.FinishedRunning();
            return (exitCode, instance.OutputData);
        }
        
        private class Options
        {
            [Option('s', "sln", Required = true, HelpText = "Path to the solution file")]
            public string Solution { get; set; }

            [Option('r', "rid", Required = true, HelpText = "dotnet runtime identifier")]
            public string RuntimeIdentifier { get; set; }

            [Option('n', "namespaces", Required = true, HelpText = "Project namespace regex")]
            public string NamespaceRegex { get; set; }
        }
    }
}
