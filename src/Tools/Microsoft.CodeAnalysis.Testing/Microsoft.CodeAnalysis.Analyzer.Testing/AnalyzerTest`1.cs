﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Composition;

namespace Microsoft.CodeAnalysis.Testing
{
    public abstract class AnalyzerTest<TVerifier>
        where TVerifier : IVerifier, new()
    {
        private static readonly Lazy<IExportProviderFactory> ExportProviderFactory;

        static AnalyzerTest()
        {
            ExportProviderFactory = new Lazy<IExportProviderFactory>(
                () =>
                {
                    var discovery = new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true);
                    var parts = Task.Run(() => discovery.CreatePartsAsync(MefHostServices.DefaultAssemblies)).GetAwaiter().GetResult();
                    var catalog = ComposableCatalog.Create(Resolver.DefaultInstance).AddParts(parts).WithDocumentTextDifferencingService();

                    var configuration = CompositionConfiguration.Create(catalog);
                    var runtimeComposition = RuntimeComposition.CreateRuntimeComposition(configuration);
                    return runtimeComposition.CreateExportProviderFactory();
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        protected static TVerifier Verify { get; } = new TVerifier();

        protected virtual string DefaultFilePathPrefix { get; } = "Test";

        protected virtual string DefaultTestProjectName { get; } = "TestProject";

        protected virtual string DefaultFilePath => DefaultFilePathPrefix + 0 + "." + DefaultFileExt;

        protected abstract string DefaultFileExt { get; }

        protected AnalyzerTest()
        {
            TestState = new SolutionState(DefaultFilePathPrefix, DefaultFileExt, MarkupMode.Allow) { InheritanceMode = StateInheritanceMode.Explicit };
        }

        /// <summary>
        /// Gets the language name used for the test.
        /// </summary>
        /// <value>
        /// The language name used for the test.
        /// </value>
        public abstract string Language { get; }

        /// <summary>
        /// Sets the input source file for analyzer or code fix testing.
        /// </summary>
        /// <seealso cref="TestState"/>
        public string TestCode
        {
            set
            {
                if (value != null)
                {
                    TestState.Sources.Add(value);
                }
            }
        }

        /// <summary>
        /// Gets the list of diagnostics expected in the source(s) and/or additonal files.
        /// </summary>
        public List<DiagnosticResult> ExpectedDiagnostics => TestState.ExpectedDiagnostics;

        /// <summary>
        /// Gets or sets the behavior of compiler diagnostics in validation scenarios. The default value is
        /// <see cref="CompilerDiagnostics.Errors"/>.
        /// </summary>
        public CompilerDiagnostics CompilerDiagnostics { get; set; } = CompilerDiagnostics.Errors;

        public SolutionState TestState { get; }

        /// <summary>
        /// Gets the collection of inputs to provide to the XML documentation resolver.
        /// </summary>
        /// <remarks>
        /// <para>Files in this collection may be referenced via <c>&lt;include&gt;</c> elements in documentation
        /// comments.</para>
        /// </remarks>
        public Dictionary<string, string> XmlReferences { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Gets or sets a value indicating whether exclusions for generated code should be tested automatically. The
        /// default value is <see langword="true"/>.
        /// </summary>
        public bool VerifyExclusions { get; set; } = true;

        /// <summary>
        /// Gets a collection of diagnostics to explicitly disable in the <see cref="CompilationOptions"/> for projects.
        /// </summary>
        public List<string> DisabledDiagnostics { get; } = new List<string>();

        /// <summary>
        /// Gets a collection of transformation functions to apply to <see cref="Workspace.Options"/> during diagnostic
        /// or code fix test setup.
        /// </summary>
        public List<Func<OptionSet, OptionSet>> OptionsTransforms { get; } = new List<Func<OptionSet, OptionSet>>();

        /// <summary>
        /// Gets a collection of transformation functions to apply to a <see cref="Solution"/> during diagnostic or code
        /// fix test setup.
        /// </summary>
        public List<Func<Solution, ProjectId, Solution>> SolutionTransforms { get; } = new List<Func<Solution, ProjectId, Solution>>();

        public virtual async Task RunAsync(CancellationToken cancellationToken = default)
        {
            Verify.NotEmpty($"{nameof(TestState)}.{nameof(SolutionState.Sources)}", TestState.Sources);

            var analyzers = GetDiagnosticAnalyzers().ToArray();
            var defaultDiagnostic = analyzers.Length > 0 && analyzers[0].SupportedDiagnostics.Length == 1 ? analyzers[0].SupportedDiagnostics[0] : null;
            var supportedDiagnostics = analyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics).ToImmutableArray();
            var fixableDiagnostics = ImmutableArray<string>.Empty;
            var testState = TestState.WithInheritedValuesApplied(null, fixableDiagnostics).WithProcessedMarkup(defaultDiagnostic, supportedDiagnostics, fixableDiagnostics, DefaultFilePath);

            await VerifyDiagnosticsAsync(testState.Sources.ToArray(), testState.AdditionalFiles.ToArray(), testState.AdditionalReferences.ToArray(), testState.ExpectedDiagnostics.ToArray(), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// General method that gets a collection of actual <see cref="Diagnostic"/>s found in the source after the
        /// analyzer is run, then verifies each of them.
        /// </summary>
        /// <param name="sources">An array of strings to create source documents from to run the analyzers on.</param>
        /// <param name="additionalFiles">Additional documents to include in the project.</param>
        /// <param name="additionalMetadataReferences">Additional metadata references to include in the project.</param>
        /// <param name="expected">A collection of <see cref="DiagnosticResult"/>s that should appear after the analyzer
        /// is run on the sources.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        protected async Task VerifyDiagnosticsAsync((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, MetadataReference[] additionalMetadataReferences, DiagnosticResult[] expected, CancellationToken cancellationToken)
        {
            var analyzers = GetDiagnosticAnalyzers().ToImmutableArray();
            VerifyDiagnosticResults(await GetSortedDiagnosticsAsync(sources, additionalFiles, additionalMetadataReferences, analyzers, cancellationToken).ConfigureAwait(false), analyzers, expected);

            // Automatically test for exclusions
            if (VerifyExclusions)
            {
                // Also check if the analyzer honors exclusions
                if (expected.Any(x => IsSubjectToExclusion(x, sources)))
                {
                    // Diagnostics reported by the compiler and analyzer diagnostics which don't have a location will
                    // still be reported. We also insert a new line at the beginning so we have to move all diagnostic
                    // locations which have a specific position down by one line.
                    var expectedResults = expected
                        .Where(x => !IsSubjectToExclusion(x, sources))
                        .Select(x => IsInSourceFile(x, sources) ? x.WithLineOffset(1) : x)
                        .ToArray();

                    var commentPrefix = Language == LanguageNames.CSharp ? "//" : "'";
                    VerifyDiagnosticResults(await GetSortedDiagnosticsAsync(sources.Select(x => (x.filename, x.content.Replace(new TextSpan(0, 0), $" {commentPrefix} <auto-generated>\r\n"))).ToArray(), additionalFiles, additionalMetadataReferences, analyzers, cancellationToken).ConfigureAwait(false), analyzers, expectedResults);
                }
            }
        }

        /// <summary>
        /// Checks each of the actual <see cref="Diagnostic"/>s found and compares them with the corresponding
        /// <see cref="DiagnosticResult"/> in the array of expected results. <see cref="Diagnostic"/>s are considered
        /// equal only if the <see cref="DiagnosticResult.Spans"/>, <see cref="DiagnosticResult.Id"/>,
        /// <see cref="DiagnosticResult.Severity"/>, and <see cref="DiagnosticResult.Message"/> of the
        /// <see cref="DiagnosticResult"/> match the actual <see cref="Diagnostic"/>.
        /// </summary>
        /// <param name="actualResults">The <see cref="Diagnostic"/>s found by the compiler after running the analyzer
        /// on the source code.</param>
        /// <param name="analyzers">The analyzers that have been run on the sources.</param>
        /// <param name="expectedResults">A collection of <see cref="DiagnosticResult"/>s describing the expected
        /// diagnostics for the sources.</param>
        private void VerifyDiagnosticResults(IEnumerable<Diagnostic> actualResults, ImmutableArray<DiagnosticAnalyzer> analyzers, DiagnosticResult[] expectedResults)
        {
            var expectedCount = expectedResults.Length;
            var actualCount = actualResults.Count();

            var diagnosticsOutput = actualResults.Any() ? FormatDiagnostics(analyzers, actualResults.ToArray()) : "    NONE.";
            var message = $"Mismatch between number of diagnostics returned, expected \"{expectedCount}\" actual \"{actualCount}\"\r\n\r\nDiagnostics:\r\n{diagnosticsOutput}\r\n";
            Verify.Equal(expectedCount, actualCount, message);

            for (var i = 0; i < expectedResults.Length; i++)
            {
                var actual = actualResults.ElementAt(i);
                var expected = expectedResults[i];

                if (!expected.HasLocation)
                {
                    Verify.Equal(Location.None, actual.Location, $"Expected:\nA project diagnostic with No location\nActual:\n{FormatDiagnostics(analyzers, actual)}");
                }
                else
                {
                    VerifyDiagnosticLocation(analyzers, actual, actual.Location, expected.Spans[0]);
                    var additionalLocations = actual.AdditionalLocations.ToArray();

                    Verify.Equal(
                        expected.Spans.Length - 1,
                        additionalLocations.Length,
                        $"Expected {expected.Spans.Length - 1} additional locations but got {additionalLocations.Length} for Diagnostic:\r\n    {FormatDiagnostics(analyzers, actual)}\r\n");

                    for (var j = 0; j < additionalLocations.Length; ++j)
                    {
                        VerifyDiagnosticLocation(analyzers, actual, additionalLocations[j], expected.Spans[j + 1]);
                    }
                }

                Verify.Equal(
                    expected.Id,
                    actual.Id,
                    $"Expected diagnostic id to be \"{expected.Id}\" was \"{actual.Id}\"\r\n\r\nDiagnostic:\r\n    {FormatDiagnostics(analyzers, actual)}\r\n");

                Verify.Equal(
                    expected.Severity,
                    actual.Severity,
                    $"Expected diagnostic severity to be \"{expected.Severity}\" was \"{actual.Severity}\"\r\n\r\nDiagnostic:\r\n    {FormatDiagnostics(analyzers, actual)}\r\n");

                if (expected.Message != null)
                {
                    Verify.Equal(expected.Message, actual.GetMessage(), $"Expected diagnostic message to be \"{expected.Message}\" was \"{actual.GetMessage()}\"\r\n\r\nDiagnostic:\r\n    {FormatDiagnostics(analyzers, actual)}\r\n");
                }
            }
        }

        /// <summary>
        /// Helper method to <see cref="VerifyDiagnosticResults"/> that checks the location of a
        /// <see cref="Diagnostic"/> and compares it with the location described by a
        /// <see cref="FileLinePositionSpan"/>.
        /// </summary>
        /// <param name="analyzers">The analyzer that have been run on the sources.</param>
        /// <param name="diagnostic">The diagnostic that was found in the code.</param>
        /// <param name="actual">The location of the diagnostic found in the code.</param>
        /// <param name="expected">The <see cref="FileLinePositionSpan"/> describing the expected location of the
        /// diagnostic.</param>
        private void VerifyDiagnosticLocation(ImmutableArray<DiagnosticAnalyzer> analyzers, Diagnostic diagnostic, Location actual, FileLinePositionSpan expected)
        {
            var actualSpan = actual.GetLineSpan();

            var assert = actualSpan.Path == expected.Path || (actualSpan.Path?.Contains("Test0.") == true && expected.Path.Contains("Test."));
            Verify.True(assert, $"Expected diagnostic to be in file \"{expected.Path}\" was actually in file \"{actualSpan.Path}\"\r\n\r\nDiagnostic:\r\n    {FormatDiagnostics(analyzers, diagnostic)}\r\n");

            VerifyLinePosition(analyzers, diagnostic, actualSpan.StartLinePosition, expected.StartLinePosition, "start");
            if (expected.StartLinePosition < expected.EndLinePosition)
            {
                VerifyLinePosition(analyzers, diagnostic, actualSpan.EndLinePosition, expected.EndLinePosition, "end");
            }
        }

        private void VerifyLinePosition(ImmutableArray<DiagnosticAnalyzer> analyzers, Diagnostic diagnostic, LinePosition actualLinePosition, LinePosition expectedLinePosition, string positionText)
        {
            // Only check the line position if it matters
            if (expectedLinePosition.Line > 0)
            {
                Verify.Equal(
                    expectedLinePosition.Line,
                    actualLinePosition.Line,
                    $"Expected diagnostic to {positionText} on line \"{expectedLinePosition.Line + 1}\" was actually on line \"{actualLinePosition.Line + 1}\"\r\n\r\nDiagnostic:\r\n    {FormatDiagnostics(analyzers, diagnostic)}\r\n");
            }

            // Only check the column position if it matters
            if (expectedLinePosition.Character > 0)
            {
                Verify.Equal(
                    expectedLinePosition.Character,
                    actualLinePosition.Character,
                    $"Expected diagnostic to {positionText} at column \"{expectedLinePosition.Character + 1}\" was actually at column \"{actualLinePosition.Character + 1}\"\r\n\r\nDiagnostic:\r\n    {FormatDiagnostics(analyzers, diagnostic)}\r\n");
            }
        }

        /// <summary>
        /// Helper method to format a <see cref="Diagnostic"/> into an easily readable string.
        /// </summary>
        /// <param name="analyzers">The analyzers that this verifier tests.</param>
        /// <param name="diagnostics">A collection of <see cref="Diagnostic"/>s to be formatted.</param>
        /// <returns>The <paramref name="diagnostics"/> formatted as a string.</returns>
        private static string FormatDiagnostics(ImmutableArray<DiagnosticAnalyzer> analyzers, params Diagnostic[] diagnostics)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < diagnostics.Length; ++i)
            {
                var diagnosticsId = diagnostics[i].Id;

                builder.Append("// ").AppendLine(diagnostics[i].ToString());

                var applicableAnalyzer = analyzers.FirstOrDefault(a => a.SupportedDiagnostics.Any(dd => dd.Id == diagnosticsId));
                if (applicableAnalyzer != null)
                {
                    var analyzerType = applicableAnalyzer.GetType();

                    var location = diagnostics[i].Location;
                    if (location == Location.None)
                    {
                        builder.AppendFormat("GetGlobalResult({0}.{1})", analyzerType.Name, diagnosticsId);
                    }
                    else if (!location.IsInSource)
                    {
                        var lineSpan = diagnostics[i].Location.GetLineSpan();
                        builder.AppendFormat($"new DiagnosticResult({analyzerType.Name}.{diagnosticsId}).WithSpan(\"{lineSpan.Path}\", {lineSpan.StartLinePosition.Line + 1}, {lineSpan.StartLinePosition.Character + 1}, {lineSpan.EndLinePosition.Line + 1}, {lineSpan.EndLinePosition.Character + 1})", analyzerType.Name, diagnosticsId);
                    }
                    else
                    {
                        var resultMethodName = diagnostics[i].Location.SourceTree.FilePath.EndsWith(".cs") ? "GetCSharpResultAt" : "GetBasicResultAt";
                        var linePosition = diagnostics[i].Location.GetLineSpan().StartLinePosition;

                        builder.AppendFormat(
                            "{0}({1}, {2}, {3}.{4})",
                            resultMethodName,
                            linePosition.Line + 1,
                            linePosition.Character + 1,
                            analyzerType.Name,
                            diagnosticsId);
                    }

                    if (i != diagnostics.Length - 1)
                    {
                        builder.Append(',');
                    }

                    builder.AppendLine();
                }
            }

            return builder.ToString();
        }

        private static bool IsSubjectToExclusion(DiagnosticResult result, (string filename, SourceText content)[] sources)
        {
            if (result.Id.StartsWith("CS", StringComparison.Ordinal)
                || result.Id.StartsWith("BC", StringComparison.Ordinal))
            {
                // This is a compiler diagnostic
                return false;
            }

            if (result.Id.StartsWith("AD", StringComparison.Ordinal))
            {
                // This diagnostic is reported by the analyzer infrastructure
                return false;
            }

            if (result.Spans.IsEmpty)
            {
                return false;
            }

            if (!IsInSourceFile(result, sources))
            {
                // This diagnostic is not reported in a source file
                return false;
            }

            return true;
        }

        private static bool IsInSourceFile(DiagnosticResult result, (string filename, SourceText content)[] sources)
        {
            return sources.Any(source => source.filename.Equals(result.Spans[0].Path));
        }

        /// <summary>
        /// Given classes in the form of strings, their language, and an <see cref="DiagnosticAnalyzer"/> to apply to
        /// it, return the <see cref="Diagnostic"/>s found in the string after converting it to a
        /// <see cref="Document"/>.
        /// </summary>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <param name="additionalFiles">Additional documents to include in the project.</param>
        /// <param name="additionalMetadataReferences">Additional metadata references to include in the project.</param>
        /// <param name="analyzers">The analyzers to be run on the sources.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A collection of <see cref="Diagnostic"/>s that surfaced in the source code, sorted by
        /// <see cref="Diagnostic.Location"/>.</returns>
        private Task<ImmutableArray<Diagnostic>> GetSortedDiagnosticsAsync((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, MetadataReference[] additionalMetadataReferences, ImmutableArray<DiagnosticAnalyzer> analyzers, CancellationToken cancellationToken)
        {
            return GetSortedDiagnosticsAsync(GetSolution(sources, additionalFiles, additionalMetadataReferences), analyzers, CompilerDiagnostics, cancellationToken);
        }

        /// <summary>
        /// Given an analyzer and a collection of documents to apply it to, run the analyzer and gather an array of
        /// diagnostics found. The returned diagnostics are then ordered by location in the source documents.
        /// </summary>
        /// <param name="solution">The <see cref="Solution"/> that the analyzer(s) will be run on.</param>
        /// <param name="analyzers">The analyzer to run on the documents.</param>
        /// <param name="compilerDiagnostics">The behavior of compiler diagnostics in validation scenarios.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that the task will observe.</param>
        /// <returns>A collection of <see cref="Diagnostic"/>s that surfaced in the source code, sorted by
        /// <see cref="Diagnostic.Location"/>.</returns>
        protected static async Task<ImmutableArray<Diagnostic>> GetSortedDiagnosticsAsync(Solution solution, ImmutableArray<DiagnosticAnalyzer> analyzers, CompilerDiagnostics compilerDiagnostics, CancellationToken cancellationToken)
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions, cancellationToken);
                var includedCompilerDiagnostics = compilation.GetDiagnostics(cancellationToken).Where(IsCompilerDiagnosticIncluded);
                var diags = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
                var allDiagnostics = await compilationWithAnalyzers.GetAllDiagnosticsAsync().ConfigureAwait(false);
                var failureDiagnostics = allDiagnostics.Where(diagnostic => diagnostic.Id == "AD0001");
                diagnostics.AddRange(diags.Concat(includedCompilerDiagnostics).Concat(failureDiagnostics));
            }

            var results = SortDistinctDiagnostics(diagnostics);
            return results.ToImmutableArray();

            // Local function
            bool IsCompilerDiagnosticIncluded(Diagnostic diagnostic)
            {
                switch (compilerDiagnostics)
                {
                case CompilerDiagnostics.None:
                default:
                    return false;

                case CompilerDiagnostics.Errors:
                    return diagnostic.Severity >= DiagnosticSeverity.Error;

                case CompilerDiagnostics.Warnings:
                    return diagnostic.Severity >= DiagnosticSeverity.Warning;

                case CompilerDiagnostics.Suggestions:
                    return diagnostic.Severity >= DiagnosticSeverity.Info;

                case CompilerDiagnostics.All:
                    return true;
                }
            }
        }

        /// <summary>
        /// Given an array of strings as sources and a language, turn them into a <see cref="Project"/> and return the
        /// solution.
        /// </summary>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <param name="additionalFiles">Additional documents to include in the project.</param>
        /// <param name="additionalMetadataReferences">Additional metadata references to include in the project.</param>
        /// <returns>A solution containing a project with the specified sources and additional files.</returns>
        private Solution GetSolution((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, MetadataReference[] additionalMetadataReferences)
        {
            Verify.LanguageIsSupported(Language);

            var project = CreateProject(sources, additionalFiles, additionalMetadataReferences, Language);
            var documents = project.Documents.ToArray();

            Verify.Equal(sources.Length, documents.Length, "Amount of sources did not match amount of Documents created");

            return project.Solution;
        }

        /// <summary>
        /// Create a project using the input strings as sources.
        /// </summary>
        /// <remarks>
        /// <para>This method first creates a <see cref="Project"/> by calling <see cref="CreateProjectImpl"/>, and then
        /// applies compilation options to the project by calling <see cref="ApplyCompilationOptions"/>.</para>
        /// </remarks>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <param name="additionalFiles">Additional documents to include in the project.</param>
        /// <param name="additionalMetadataReferences">Additional metadata references to include in the project.</param>
        /// <param name="language">The language the source classes are in. Values may be taken from the
        /// <see cref="LanguageNames"/> class.</param>
        /// <returns>A <see cref="Project"/> created out of the <see cref="Document"/>s created from the source
        /// strings.</returns>
        protected Project CreateProject((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, MetadataReference[] additionalMetadataReferences, string language)
        {
            language = language ?? language;
            var project = CreateProjectImpl(sources, additionalFiles, additionalMetadataReferences, language);
            return ApplyCompilationOptions(project);
        }

        /// <summary>
        /// Create a project using the input strings as sources.
        /// </summary>
        /// <param name="sources">Classes in the form of strings.</param>
        /// <param name="additionalFiles">Additional documents to include in the project.</param>
        /// <param name="additionalMetadataReferences">Additional metadata references to include in the project.</param>
        /// <param name="language">The language the source classes are in. Values may be taken from the
        /// <see cref="LanguageNames"/> class.</param>
        /// <returns>A <see cref="Project"/> created out of the <see cref="Document"/>s created from the source
        /// strings.</returns>
        protected virtual Project CreateProjectImpl((string filename, SourceText content)[] sources, (string filename, SourceText content)[] additionalFiles, MetadataReference[] additionalMetadataReferences, string language)
        {
            var fileNamePrefix = DefaultFilePathPrefix;
            var fileExt = DefaultFileExt;

            var projectId = ProjectId.CreateNewId(debugName: DefaultTestProjectName);
            var solution = CreateSolution(projectId, language);

            solution = solution.AddMetadataReferences(projectId, additionalMetadataReferences);

            for (var i = 0; i < sources.Length; i++)
            {
                (var newFileName, var source) = sources[i];
                var documentId = DocumentId.CreateNewId(projectId, debugName: newFileName);
                solution = solution.AddDocument(documentId, newFileName, source);
            }

            for (var i = 0; i < additionalFiles.Length; i++)
            {
                (var newFileName, var source) = additionalFiles[i];
                var documentId = DocumentId.CreateNewId(projectId, debugName: newFileName);
                solution = solution.AddAdditionalDocument(documentId, newFileName, source);
            }

            foreach (var transform in SolutionTransforms)
            {
                solution = transform(solution, projectId);
            }

            return solution.GetProject(projectId);
        }

        /// <summary>
        /// Creates a solution that will be used as parent for the sources that need to be checked.
        /// </summary>
        /// <param name="projectId">The project identifier to use.</param>
        /// <param name="language">The language for which the solution is being created.</param>
        /// <returns>The created solution.</returns>
        protected virtual Solution CreateSolution(ProjectId projectId, string language)
        {
            var compilationOptions = CreateCompilationOptions();

            var xmlReferenceResolver = new TestXmlReferenceResolver();
            foreach (var xmlReference in XmlReferences)
            {
                xmlReferenceResolver.XmlReferences.Add(xmlReference.Key, xmlReference.Value);
            }

            compilationOptions = compilationOptions.WithXmlReferenceResolver(xmlReferenceResolver);

            var solution = CreateWorkspace()
                .CurrentSolution
                .AddProject(projectId, DefaultTestProjectName, DefaultTestProjectName, language)
                .WithProjectCompilationOptions(projectId, compilationOptions)
                .AddMetadataReference(projectId, MetadataReferences.CorlibReference)
                .AddMetadataReference(projectId, MetadataReferences.SystemReference)
                .AddMetadataReference(projectId, MetadataReferences.SystemCoreReference)
                .AddMetadataReference(projectId, MetadataReferences.CodeAnalysisReference)
                .AddMetadataReference(projectId, MetadataReferences.SystemCollectionsImmutableReference);

            if (language == LanguageNames.VisualBasic)
            {
                solution = solution.AddMetadataReference(projectId, MetadataReferences.MicrosoftVisualBasicReference);
            }

            if (MetadataReferences.SystemRuntimeReference != null)
            {
                solution = solution.AddMetadataReference(projectId, MetadataReferences.SystemRuntimeReference);
            }

            if (typeof(object).GetTypeInfo().Assembly.GetType("System.ValueTuple`2", throwOnError: false) == null
                && MetadataReferences.SystemValueTupleReference != null)
            {
                solution = solution.AddMetadataReference(projectId, MetadataReferences.SystemValueTupleReference);
            }

            foreach (var transform in OptionsTransforms)
            {
                solution.Workspace.Options = transform(solution.Workspace.Options);
            }

            var parseOptions = solution.GetProject(projectId).ParseOptions;
            solution = solution.WithProjectParseOptions(projectId, parseOptions.WithDocumentationMode(DocumentationMode.Diagnose));

            return solution;
        }

        /// <summary>
        /// Applies compilation options to a project.
        /// </summary>
        /// <remarks>
        /// <para>The default implementation configures the project by enabling all supported diagnostics of analyzers
        /// included in <see cref="GetDiagnosticAnalyzers"/> as well as <c>AD0001</c>. After configuring these
        /// diagnostics, any diagnostic IDs indicated in <see cref="DisabledDiagnostics"/> are explicitly suppressed
        /// using <see cref="ReportDiagnostic.Suppress"/>.</para>
        /// </remarks>
        /// <param name="project">The project.</param>
        /// <returns>The modified project.</returns>
        protected virtual Project ApplyCompilationOptions(Project project)
        {
            var analyzers = GetDiagnosticAnalyzers();

            var supportedDiagnosticsSpecificOptions = new Dictionary<string, ReportDiagnostic>();
            foreach (var analyzer in analyzers)
            {
                foreach (var diagnostic in analyzer.SupportedDiagnostics)
                {
                    // make sure the analyzers we are testing are enabled
                    supportedDiagnosticsSpecificOptions[diagnostic.Id] = ReportDiagnostic.Default;
                }
            }

            // Report exceptions during the analysis process as errors
            supportedDiagnosticsSpecificOptions.Add("AD0001", ReportDiagnostic.Error);

            foreach (var id in DisabledDiagnostics)
            {
                supportedDiagnosticsSpecificOptions[id] = ReportDiagnostic.Suppress;
            }

            // update the project compilation options
            var modifiedSpecificDiagnosticOptions = supportedDiagnosticsSpecificOptions.ToImmutableDictionary().SetItems(project.CompilationOptions.SpecificDiagnosticOptions);
            var modifiedCompilationOptions = project.CompilationOptions.WithSpecificDiagnosticOptions(modifiedSpecificDiagnosticOptions);

            var solution = project.Solution.WithProjectCompilationOptions(project.Id, modifiedCompilationOptions);
            return solution.GetProject(project.Id);
        }

        public virtual AdhocWorkspace CreateWorkspace()
        {
            var exportProvider = ExportProviderFactory.Value.CreateExportProvider();

#if NETSTANDARD1_5 || NETSTANDARD2_0
            return new AdhocWorkspace();
#else
            var host = MefV1HostServices.Create(exportProvider.AsExportProvider());
            return new AdhocWorkspace(host);
#endif
        }

        protected abstract CompilationOptions CreateCompilationOptions();

        /// <summary>
        /// Sort <see cref="Diagnostic"/>s by location in source document.
        /// </summary>
        /// <param name="diagnostics">A collection of <see cref="Diagnostic"/>s to be sorted.</param>
        /// <returns>A collection containing the input <paramref name="diagnostics"/>, sorted by
        /// <see cref="Diagnostic.Location"/> and <see cref="Diagnostic.Id"/>.</returns>
        private static Diagnostic[] SortDistinctDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            return diagnostics
                .OrderBy(d => d.Location.GetLineSpan().Path, StringComparer.Ordinal)
                .ThenBy(d => d.Location.SourceSpan.Start)
                .ThenBy(d => d.Location.SourceSpan.End)
                .ThenBy(d => d.Id).ToArray();
        }

        /// <summary>
        /// Gets the analyzers being tested.
        /// </summary>
        /// <returns>
        /// New instances of all the analyzers being tested.
        /// </returns>
        protected abstract IEnumerable<DiagnosticAnalyzer> GetDiagnosticAnalyzers();
    }
}
