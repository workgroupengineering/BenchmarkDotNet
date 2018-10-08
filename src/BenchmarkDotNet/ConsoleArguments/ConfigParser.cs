﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Exporters.Xml;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Horology;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Portability;
using BenchmarkDotNet.Toolchains.CoreRt;
using BenchmarkDotNet.Toolchains.CoreRun;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using BenchmarkDotNet.Toolchains.InProcess;
using CommandLine;

namespace BenchmarkDotNet.ConsoleArguments
{
    public static class ConfigParser
    {
        private const int MinimumDisplayWidth = 80;

        private static readonly IReadOnlyDictionary<string, Job> AvailableJobs = new Dictionary<string, Job>(StringComparer.InvariantCultureIgnoreCase)
        {
            { "default", Job.Default },
            { "dry", Job.Dry },
            { "short", Job.ShortRun },
            { "medium", Job.MediumRun },
            { "long", Job.LongRun },
            { "verylong", Job.VeryLongRun }
        };

        private static readonly ImmutableHashSet<string> AvailableRuntimes = ImmutableHashSet.Create(StringComparer.InvariantCultureIgnoreCase,
            "net46",
            "net461",
            "net462",
            "net47",
            "net471",
            "net472",
            "netcoreapp2.0",
            "netcoreapp2.1",
            "netcoreapp2.2",
            "netcoreapp3.0",
            "mono",
            "corert"
        );

        [SuppressMessage("ReSharper", "StringLiteralTypo")]
        [SuppressMessage("ReSharper", "CoVariantArrayConversion")]
        private static readonly IReadOnlyDictionary<string, IExporter[]> AvailableExporters =
            new Dictionary<string, IExporter[]>(StringComparer.InvariantCultureIgnoreCase)
            {
                { "csv", new[] { CsvExporter.Default } },
                { "csvmeasurements", new[] { CsvMeasurementsExporter.Default } },
                { "html", new[] { HtmlExporter.Default } },
                { "markdown", new[] { MarkdownExporter.Default } },
                { "atlassian", new[] { MarkdownExporter.Atlassian } },
                { "stackoverflow", new[] { MarkdownExporter.StackOverflow } },
                { "github", new[] { MarkdownExporter.GitHub } },
                { "plain", new[] { PlainExporter.Default } },
                { "rplot", new[] { CsvMeasurementsExporter.Default, RPlotExporter.Default } }, // R Plots depends on having the full measurements available
                { "json", new[] { JsonExporter.Default } },
                { "briefjson", new[] { JsonExporter.Brief } },
                { "fulljson", new[] { JsonExporter.Full } },
                { "asciidoc", new[] { AsciiDocExporter.Default } },
                { "xml", new[] { XmlExporter.Default } },
                { "briefxml", new[] { XmlExporter.Brief } },
                { "fullxml", new[] { XmlExporter.Full } }
            };

        public static (bool isSuccess, ReadOnlyConfig config) Parse(string[] args, ILogger logger, IConfig globalConfig = null)
        {
            (bool isSuccess, ReadOnlyConfig config) result = default;

            using (var parser = CreateParser(logger))
            {
                parser
                    .ParseArguments<CommandLineOptions>(args)
                    .WithParsed(options => result = Validate(options, logger) ? (true, CreateConfig(options, globalConfig)) : (false, default))
                    .WithNotParsed(errors => result = (false, default));
            }

            return result;
        }

        private static Parser CreateParser(ILogger logger)
            => new Parser(settings =>
            {
                settings.CaseInsensitiveEnumValues = true;
                settings.CaseSensitive = false;
                settings.EnableDashDash = true;
                settings.IgnoreUnknownArguments = false;
                settings.HelpWriter = new LoggerWrapper(logger);
                settings.MaximumDisplayWidth = Math.Max(MinimumDisplayWidth, GetMaximumDisplayWidth());
            });

        private static bool Validate(CommandLineOptions options, ILogger logger)
        {
            if (!AvailableJobs.ContainsKey(options.BaseJob))
            {
                logger.WriteLineError($"The provided base job \"{options.BaseJob}\" is invalid. Available options are: {string.Join(", ", AvailableJobs.Keys)}.");
                return false;
            }

            foreach (string runtime in options.Runtimes)
                if (!AvailableRuntimes.Contains(runtime))
                {
                    logger.WriteLineError($"The provided runtime \"{runtime}\" is invalid. Available options are: {string.Join(", ", AvailableRuntimes)}.");
                    return false;
                }

            foreach (string exporter in options.Exporters)
                if (!AvailableExporters.ContainsKey(exporter))
                {
                    logger.WriteLineError($"The provided exporter \"{exporter}\" is invalid. Available options are: {string.Join(", ", AvailableExporters.Keys)}.");
                    return false;
                }

            if (options.CliPath.IsNotNullButDoesNotExist())
            {
                logger.WriteLineError($"The provided {nameof(options.CliPath)} \"{options.CliPath}\" does NOT exist.");
                return false;
            }

            if (options.CoreRunPath.IsNotNullButDoesNotExist())
            {
                logger.WriteLineError($"The provided {nameof(options.CoreRunPath)} \"{options.CoreRunPath}\" does NOT exist.");
                return false;
            }
            
            if (options.MonoPath.IsNotNullButDoesNotExist())
            {
                logger.WriteLineError($"The provided {nameof(options.MonoPath)} \"{options.MonoPath}\" does NOT exist.");
                return false;
            }
            
            if (options.CoreRtPath.IsNotNullButDoesNotExist())
            {
                logger.WriteLineError($"The provided {nameof(options.CoreRtPath)} \"{options.CoreRtPath}\" does NOT exist.");
                return false;
            }

            return true;
        }

        private static ReadOnlyConfig CreateConfig(CommandLineOptions options, IConfig globalConfig)
        {
            var config = new ManualConfig();

            var baseJob = GetBaseJob(options, globalConfig);
            config.Add(Expand(baseJob, options).ToArray());
            if (config.GetJobs().IsEmpty() && baseJob != Job.Default)
                config.Add(baseJob);

            config.Add(options.Exporters.SelectMany(exporter => AvailableExporters[exporter]).ToArray());
            
            config.Add(options.HardwareCounters
                .Select(counterName => Enum.TryParse(counterName, ignoreCase: true, out HardwareCounter counter) ? counter : HardwareCounter.NotSet)
                .Where(counter => counter != HardwareCounter.NotSet)
                .ToArray());

            if (options.UseMemoryDiagnoser)
                config.Add(MemoryDiagnoser.Default);
            if (options.UseDisassemblyDiagnoser)
                config.Add(DisassemblyDiagnoser.Create(new DisassemblyDiagnoserConfig()));
            if (!string.IsNullOrEmpty(options.Profiler))
                config.Add(DiagnosersLoader.GetImplementation<IProfiler>(profiler => profiler.ShortName.EqualsWithIgnoreCase(options.Profiler)));

            if (options.DisplayAllStatistics)
                config.Add(StatisticColumn.AllStatistics);

            if (options.ArtifactsDirectory != null)
                config.ArtifactsPath = options.ArtifactsDirectory.FullName;

            var filters = GetFilters(options).ToArray();
            if (filters.Length > 1)
                config.Add(new UnionFilter(filters));
            else
                config.Add(filters);

            config.SummaryPerType = !options.Join;

            config.KeepBenchmarkFiles = options.KeepBenchmarkFiles;

            return config.AsReadOnly();
        }

        private static Job GetBaseJob(CommandLineOptions options, IConfig globalConfig)
        {
            var baseJob = 
                globalConfig?.GetJobs().SingleOrDefault(job => job.Meta.IsDefault) // global config might define single custom Default job
                ?? AvailableJobs[options.BaseJob.ToLowerInvariant()];

            if (baseJob != Job.Dry && options.Outliers != OutlierMode.OnlyUpper)
                baseJob = baseJob.WithOutlierMode(options.Outliers);

            if (options.Affinity.HasValue)
                baseJob = baseJob.WithAffinity((IntPtr) options.Affinity.Value);

            if (options.LaunchCount.HasValue)
                baseJob = baseJob.WithLaunchCount(options.LaunchCount.Value);
            if (options.WarmupIterationCount.HasValue)
                baseJob = baseJob.WithWarmupCount(options.WarmupIterationCount.Value);
            if (options.MinWarmupIterationCount.HasValue)
                baseJob = baseJob.WithMinWarmupCount(options.MinWarmupIterationCount.Value);
            if (options.MaxWarmupIterationCount.HasValue)
                baseJob = baseJob.WithMaxWarmupCount(options.MaxWarmupIterationCount.Value);
            if (options.IterationTimeInMiliseconds.HasValue)
                baseJob = baseJob.WithIterationTime(TimeInterval.FromMilliseconds(options.IterationTimeInMiliseconds.Value));
            if (options.IterationCount.HasValue)
                baseJob = baseJob.WithIterationCount(options.IterationCount.Value);
            if (options.MinIterationCount.HasValue)
                baseJob = baseJob.WithMinIterationCount(options.MinIterationCount.Value);
            if (options.MaxIterationCount.HasValue)
                baseJob = baseJob.WithMaxIterationCount(options.MaxIterationCount.Value);
            if (options.InvocationCount.HasValue)
                baseJob = baseJob.WithInvocationCount(options.InvocationCount.Value);
            if (options.UnrollFactor.HasValue)
                baseJob = baseJob.WithUnrollFactor(options.UnrollFactor.Value);
            if (options.RunOncePerIteration)
                baseJob = baseJob.RunOncePerIteration();

            if (AvailableJobs.Values.Contains(baseJob)) // no custom settings
                return baseJob;

            return baseJob
                .AsDefault(false) // after applying all settings from console args the base job is not default anymore
                .AsMutator(); // we mark it as mutator so it will be applied to other jobs defined via attributes and merged later in GetRunnableJobs method
        }

        private static IEnumerable<Job> Expand(Job baseJob, CommandLineOptions options)
        {
            if (options.RunInProcess)
                yield return baseJob.With(InProcessToolchain.Instance);

            foreach (string runtime in options.Runtimes) // known runtimes
                yield return CreateJobForGivenRuntime(baseJob, runtime.ToLowerInvariant(), options);

            if (options.CoreRunPath != null)
                yield return CreateCoreRunJob(baseJob, options); // local CoreFX and CoreCLR builds
            else if (!string.IsNullOrEmpty(options.ClrVersion))
                yield return baseJob.With(new ClrRuntime(options.ClrVersion)); // local builds of .NET Runtime
        }

        private static Job CreateJobForGivenRuntime(Job baseJob, string runtime, CommandLineOptions options)
        {
            switch (runtime)
            {
                case "net46":
                case "net461":
                case "net462":
                case "net47":
                case "net471":
                case "net472":
                    return baseJob.With(Runtime.Clr).With(CsProjClassicNetToolchain.From(runtime));
                case "netcoreapp2.0":
                case "netcoreapp2.1":
                case "netcoreapp2.2":
                case "netcoreapp3.0":
                    if (options.CliPath != null)
                        return baseJob.With(Runtime.Core).With(CsProjCoreToolchain.From(new NetCoreAppSettings(runtime, null, runtime, options.CliPath.FullName)));
                    else
                        return baseJob.With(Runtime.Core).With(CsProjCoreToolchain.From(new NetCoreAppSettings(runtime, null, runtime)));
                case "mono":
                    if (options.MonoPath != null)
                        return baseJob.With(new MonoRuntime("Mono", options.MonoPath.FullName));
                    else
                        return baseJob.With(Runtime.Mono);
                case "corert":
                    if (options.CoreRtPath != null)
                        return baseJob.With(Runtime.CoreRT).With(CoreRtToolchain.CreateBuilder().UseCoreRtLocal(options.CoreRtPath.FullName).ToToolchain());
                    else if (!string.IsNullOrEmpty(options.CoreRtVersion))
                        return baseJob.With(Runtime.CoreRT).With(CoreRtToolchain.CreateBuilder().UseCoreRtNuGet(options.CoreRtVersion).ToToolchain());
                    else
                        return baseJob.With(Runtime.CoreRT).With(CoreRtToolchain.LatestMyGetBuild);
                default:
                    throw new NotSupportedException($"Runtime {runtime} is not supported");
            }
        }

        private static IEnumerable<IFilter> GetFilters(CommandLineOptions options)
        {
            if (options.Filters.Any())
                yield return new GlobFilter(options.Filters.ToArray());
            if (options.AllCategories.Any())
                yield return new AllCategoriesFilter(options.AllCategories.ToArray());
            if (options.AnyCategories.Any())
                yield return new AnyCategoriesFilter(options.AnyCategories.ToArray());
            if (options.AttributeNames.Any())
                yield return new AttributesFilter(options.AttributeNames.ToArray());
        }

        private static int GetMaximumDisplayWidth()
        {
            try
            {
                return Console.WindowWidth;
            }
            catch (IOException)
            {
                return MinimumDisplayWidth;
            }
        }

        private static Job CreateCoreRunJob(Job baseJob, CommandLineOptions options)
            => baseJob
                .With(Runtime.Core)
                .With(new CoreRunToolchain(
                    options.CoreRunPath,
                    createCopy: true,
                    targetFrameworkMoniker: NetCoreAppSettings.GetCurrentVersion().TargetFrameworkMoniker,
                    customDotNetCliPath: options.CliPath));
    }
}