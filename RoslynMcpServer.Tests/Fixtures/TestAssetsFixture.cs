using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Locator;
using Xunit;

namespace RoslynMcpServer.Tests.Fixtures
{
    public class TestAssetsFixture
    {
        public string SampleSolutionPath { get; }

        static TestAssetsFixture()
        {
            EnsureMsbuildRegistered();
        }

        public TestAssetsFixture()
        {
            var relative = Path.Combine("..", "..", "..", "..", "TestAssets", "SampleSolution", "SampleSolution.sln");
            SampleSolutionPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
        }

        private static void EnsureMsbuildRegistered()
        {
            if (MSBuildLocator.IsRegistered)
            {
                return;
            }

            var instance = MSBuildLocator
                .QueryVisualStudioInstances()
                .OrderByDescending(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
                .ThenByDescending(i => i.Version)
                .FirstOrDefault();

            if (instance != null)
            {
                MSBuildLocator.RegisterInstance(instance);
            }
            else
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    [CollectionDefinition(Name)]
    public class TestAssetsCollection : ICollectionFixture<TestAssetsFixture>
    {
        public const string Name = "TestAssetsCollection";
    }
}
