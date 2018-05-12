using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.DotNet.PlatformAbstractions;
using Microsoft.DotNet.Tools.Test.Utilities;
using Microsoft.Extensions.DependencyModel;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.DotNet.Cli.Publish.Tests
{
    public class GivenThatAPublishedDepsJsonShouldContainVersionInformation : TestBase
    {
        const string TestAppName = "RollForwardApp";
        const string TargetFramework = "netcoreapp2.0";

        DirectoryInfo GetPublishDirectory(string projectDirectory, string runtimeIdentifier = null)
        {
            runtimeIdentifier = runtimeIdentifier ?? string.Empty;
            var publishDirectory = Path.Combine(projectDirectory, "bin", "Debug", TargetFramework, runtimeIdentifier, "publish");
            return new DirectoryInfo(publishDirectory);
        }

        [Fact]
        public void Versions_are_included_in_deps_json()
        {
            var testInstance = TestAssets.Get(TestAppName)
                            .CreateInstance()
                            .WithSourceFiles();

            var testProjectDirectory = testInstance.Root.FullName;

            new RestoreCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute()
                .Should().Pass();

            new PublishCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute()
                .Should().Pass();

            var publishDirectory = GetPublishDirectory(testProjectDirectory);
            publishDirectory.Should().HaveFile(TestAppName + ".deps.json");

            var depsFilePath = Path.Combine(publishDirectory.FullName, $"{TestAppName}.deps.json");
            CheckVersionsInDepsFile(depsFilePath);
        }

        void CheckVersionsInDepsFile(string depsFilePath)
        {
            DependencyContext dependencyContext;
            using (var depsJsonFileStream = File.OpenRead(depsFilePath))
            {
                dependencyContext = new DependencyContextJsonReader().Read(depsJsonFileStream);
            }

            var libuvRuntimeLibrary = dependencyContext.RuntimeLibraries.Single(l => l.Name == "Libuv");
            var libuvRuntimeFiles = libuvRuntimeLibrary.NativeLibraryGroups.SelectMany(rag => rag.RuntimeFiles).ToList();
            libuvRuntimeFiles.Should().NotBeEmpty();
            foreach (var runtimeFile in libuvRuntimeFiles)
            {
                runtimeFile.AssemblyVersion.Should().BeNull();
                runtimeFile.FileVersion.Should().Be("0.0.0.0");
            }

            var immutableRuntimeLibrary = dependencyContext.RuntimeLibraries.Single(l => l.Name == "System.Collections.Immutable");
            var immutableRuntimeFiles = immutableRuntimeLibrary.RuntimeAssemblyGroups.SelectMany(rag => rag.RuntimeFiles).ToList();
            immutableRuntimeFiles.Should().NotBeEmpty();
            foreach (var runtimeFile in immutableRuntimeFiles)
            {
                runtimeFile.AssemblyVersion.Should().Be("1.2.3.0");
                runtimeFile.FileVersion.Should().Be("4.6.26216.2");
            }
        }

        [Fact]
        public void Versions_are_included_for_self_contained_apps()
        {
            Versions_are_included(build: false);
        }

        [Fact]
        public void Versions_are_included_for_build()
        {
            Versions_are_included(build: true);
        }

        private void Versions_are_included(bool build, [CallerMemberName] string callingMethod = "")
        {
            var rid = RuntimeEnvironment.GetRuntimeIdentifier();

            var testInstance = TestAssets.Get(TestAppName)
                            .CreateInstance(callingMethod)
                            .WithSourceFiles()
                            .WithProjectChanges(project =>
                            {
                                var ns = project.Root.Name.Namespace;

                                project.Root.Element(ns + "PropertyGroup")
                                    .Add(new XElement(ns + "RuntimeIdentifier", rid));
                            });

            var testProjectDirectory = testInstance.Root.FullName;

            new RestoreCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute()
                .Should().Pass();

            DotnetCommand command;
            if (build)
            {
                command = new BuildCommand();
            }
            else
            {
                command = new PublishCommand();
            }

            command = command.WithWorkingDirectory(testProjectDirectory);

            command.Execute()
                .Should()
                .Pass();

            var outputDirectory = GetPublishDirectory(testProjectDirectory, rid);
            if (build)
            {
                //  Don't use publish directory
                outputDirectory = outputDirectory.Parent;
            }
            outputDirectory.Should().HaveFile(TestAppName + ".deps.json");

            var depsFilePath = Path.Combine(outputDirectory.FullName, $"{TestAppName}.deps.json");
            CheckVersionsInDepsFile(depsFilePath);
        }

        [Fact]
        public void Inbox_version_of_assembly_is_loaded_over_applocal_version()
        {
            var (coreDir, publishDir, immutableDir) = TestConflictResult();
            immutableDir.Should().BeEquivalentTo(coreDir, "immutable collections library from Framework should win");
        }

        [Fact]
        public void Inbox_version_is_loaded_if_runtime_file_versions_arent_in_deps()
        {
            void testProjectChanges(XDocument project)
            {
                var ns = project.Root.Name.Namespace;
                project.Root.Element(ns + "PropertyGroup")
                                    .Add(new XElement(ns + "IncludeRuntimeFileVersions", "false"));
            }

            var (coreDir, publishDir, immutableDir) = TestConflictResult(testProjectChanges);
            immutableDir.Should().BeEquivalentTo(publishDir, "published immutable collections library from should win");
        }

        [Fact]
        public void Local_version_of_assembly_with_higher_version_is_loaded_over_inbox_version()
        {
            void publishFolderChanges(string publishFolder)
            {
                var depsJsonPath = Path.Combine(publishFolder, "DepsJsonVersions.deps.json");
                var depsJson = JObject.Parse(File.ReadAllText(depsJsonPath));
                var target = ((JProperty)depsJson["targets"].First).Value;
                var file = target["System.Collections.Immutable/1.5.0-preview1-26216-02"]["runtime"]["lib/netstandard2.0/System.Collections.Immutable.dll"];
                //  Set fileVersion in deps.json to 4.7.0.0, which should be bigger than in box 4.6.x version
                file["fileVersion"] = "4.7.0.0";
                File.WriteAllText(depsJsonPath, depsJson.ToString());
            }

            var (coreDir, publishDir, immutableDir) = TestConflictResult(publishFolderChanges: publishFolderChanges);
            immutableDir.Should().BeEquivalentTo(publishDir, "published immutable collections library from should win");
        }

        private (string coreDir, string publishDir, string immutableDir) TestConflictResult(
            Action<XDocument> testProjectChanges = null, Action<string> publishFolderChanges = null, [CallerMemberName] string callingMethod = "")
        {
            var testInstance = TestAssets.Get(TestAppName)
                            .CreateInstance(callingMethod: callingMethod)
                            .WithSourceFiles();

            if (testProjectChanges != null)
            {
                testInstance = testInstance.WithProjectChanges(testProjectChanges);
            }

            var testProjectDirectory = testInstance.Root.FullName;

            new RestoreCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute()
                .Should().Pass();

            new PublishCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute()
                .Should().Pass();

            var publishDirectory = GetPublishDirectory(testProjectDirectory);

            if (publishFolderChanges != null)
            {
                publishFolderChanges(publishDirectory.FullName);
            }

            //  Assembly from package should be deployed, as it is newer than the in-box version for netcoreapp2.0,
            //  which is what the app targets
            publishDirectory.Should().HaveFile("System.Collections.Immutable.dll");

            var exePath = Path.Combine(publishDirectory.FullName, TestAppName + ".dll");

            //  We want to test a .NET Core 2.0 app rolling forward to .NET Core 2.1.
            //  This wouldn't happen in our test environment as we also have the .NET Core 2.0 shared
            //  framework installed.  So we get the RuntimeFrameworkVersion of an app
            //  that targets .NET Core 2.1, and then use the --fx-version parameter to the host
            //  to force the .NET Core 2.0 app to run on that version
            string rollForwardVersion = GetRollForwardNetCoreAppVersion();

            var runAppCommand = new DotnetCommand();
            //new string[] { "exec", "--fx-version", rollForwardVersion, exePath });

            var runAppResult = runAppCommand
                .ExecuteWithCapturedOutput($"exec --fx-version {rollForwardVersion} {exePath}");

            runAppResult
                .Should()
                .Pass();

            var stdOutLines = runAppResult.StdOut.Split(Environment.NewLine);

            string coreDir = Path.GetDirectoryName(stdOutLines[0]);
            string immutableDir = Path.GetDirectoryName(stdOutLines[1]);

            return (coreDir, publishDirectory.FullName, immutableDir);
        }

        string GetRollForwardNetCoreAppVersion()
        {
            var testInstance = TestAssets.Get(TestAppName)
                            .CreateInstance()
                            .WithSourceFiles()
                            .WithProjectChanges(project =>
                            {
                                var ns = project.Root.Name.Namespace;
                                project.Root.Element(ns + "PropertyGroup")
                                    .Element(ns + "TargetFramework")
                                    .SetValue("netcoreapp2.1");

                            });

            var testProjectDirectory = testInstance.Root.FullName;

            new RestoreCommand()
                .WithWorkingDirectory(testProjectDirectory)
                .Execute()
                .Should().Pass();

            var assetsFilePath = Path.Combine

            var testAsset = _testAssetsManager.CreateTestProject(testProject)
                .Restore(Log, testProject.Name);
            var getValuesCommand = new GetValuesCommand(Log, Path.Combine(testAsset.TestRoot, testProject.Name),
                        testProject.TargetFrameworks, "RuntimeFrameworkVersion")
            {
                ShouldCompile = false
            };

            getValuesCommand.Execute().Should().Pass();

            return getValuesCommand.GetValues().Single();
        }
    }
}
